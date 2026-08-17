#region

using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Renders a small, dependency-free subset of GitHub-flavored markdown — headings, paragraphs,
///     bullet lists, blockquotes, <c>**bold**</c> / <c>`code`</c> inlines, and
///     <c>&lt;details&gt;</c>/<c>&lt;summary&gt;</c> blocks (as collapsed expanders) — enough for
///     the release-note bodies shown by the update notice and What's New windows.
///     <para>
///         Deliberately NOT a general markdown engine: unknown syntax degrades to plain text,
///         never to an error, and no colors are hardcoded so the text inherits the active theme.
///         Hard-wrapped source lines are re-joined into paragraphs (markdown soft-wrap semantics),
///         because the release bodies are authored wrapped at ~100 columns.
///     </para>
/// </summary>
public sealed partial class MarkdownBlock : ContentControl
{
    /// <summary>The markdown source to render. Null/empty clears the content.</summary>
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    // Theme-neutral half-alpha gray for the blockquote bar — readable on dark and light without
    // depending on a resource key existing in every theme.
    private static readonly IBrush _quoteBarBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88));

    static MarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownBlock>(static (c, _) => c.Rebuild());
    }

    /// <summary>Gets or sets the markdown source.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild() =>
        Content = string.IsNullOrWhiteSpace(Markdown) ? null : RenderBlocks(Markdown);

    /// <summary>Renders <paramref name="markdown" /> to a block-stack control (test seam).</summary>
    internal static Control RenderBlocks(string markdown)
    {
        // Preprocess: normalize newlines, drop HTML comments, reduce images to their alt text.
        markdown = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        markdown = HtmlComment().Replace(markdown, string.Empty);
        markdown = ImageSyntax().Replace(markdown, "$1");

        StackPanel panel = new()
        {
            Spacing = 10
        };
        string[] lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("<details", StringComparison.OrdinalIgnoreCase))
            {
                panel.Children.Add(ParseDetails(lines, ref i));
                continue;
            }

            Match heading = Heading().Match(trimmed);
            if (heading.Success)
            {
                panel.Children.Add(RenderHeading(heading.Groups[1].Length, heading.Groups[2].Value));
                i++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                panel.Children.Add(RenderQuote(lines, ref i));
                continue;
            }

            if (IsBullet(trimmed))
            {
                panel.Children.Add(RenderBulletList(lines, ref i));
                continue;
            }

            panel.Children.Add(RenderParagraph(lines, ref i));
        }

        return panel;
    }

    // ── Block renderers ─────────────────────────────────────────────────────

    private static TextBlock RenderHeading(int level, string text)
    {
        TextBlock tb = new()
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            FontSize = level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                _ => 13.5
            },
            Margin = new Thickness(0, level <= 2 ? 4 : 2, 0, 0)
        };
        AddInlines(tb.Inlines!, text);
        return tb;
    }

    // Consecutive '>' lines become one quote block; the stripped inner text is re-rendered
    // recursively so bold/paragraph rules apply inside the quote too.
    private static Border RenderQuote(string[] lines, ref int i)
    {
        List<string> inner = new();
        while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
        {
            string stripped = lines[i].TrimStart().TrimStart('>');
            inner.Add(stripped.StartsWith(' ') ? stripped[1..] : stripped);
            i++;
        }

        return new Border
        {
            BorderBrush = _quoteBarBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Child = RenderBlocks(string.Join('\n', inner))
        };
    }

    private static StackPanel RenderBulletList(string[] lines, ref int i)
    {
        StackPanel list = new()
        {
            Spacing = 4
        };
        while (i < lines.Length && IsBullet(lines[i].Trim()))
        {
            string item = lines[i].Trim()[2..];
            i++;
            // Hard-wrapped continuation lines belong to the same item until a blank line or the
            // start of another block.
            while (i < lines.Length && IsContinuation(lines[i]))
            {
                item += " " + lines[i].Trim();
                i++;
            }

            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("16,*")
            };
            row.Children.Add(new TextBlock
            {
                Text = "•"
            });
            TextBlock content = new()
            {
                TextWrapping = TextWrapping.Wrap
            };
            AddInlines(content.Inlines!, item);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            list.Children.Add(row);
        }

        return list;
    }

    private static TextBlock RenderParagraph(string[] lines, ref int i)
    {
        string text = lines[i].Trim();
        i++;
        while (i < lines.Length && IsContinuation(lines[i]))
        {
            text += " " + lines[i].Trim();
            i++;
        }

        TextBlock tb = new()
        {
            TextWrapping = TextWrapping.Wrap
        };
        AddInlines(tb.Inlines!, text);
        return tb;
    }

    // A <details> block becomes a collapsed Expander headed by its <summary> text — exactly how
    // the release bodies use it ("What was new in 0.5.1"). Tracks nesting depth so a details
    // inside a details stays inside the outer body.
    private static Expander ParseDetails(string[] lines, ref int i)
    {
        int depth = 0;
        List<string> body = new();
        do
        {
            string line = lines[i];
            depth += CountOccurrences(line, "<details");
            depth -= CountOccurrences(line, "</details");
            body.Add(line);
            i++;
        } while (i < lines.Length && depth > 0);

        string joined = string.Join('\n', body);
        // Strip the outer <details> tags and pull the summary out of the body.
        joined = OuterDetailsTag().Replace(joined, string.Empty);
        string summary = "Details";
        Match m = SummaryTag().Match(joined);
        if (m.Success)
        {
            summary = m.Groups[1].Value.Trim();
            joined = joined.Remove(m.Index, m.Length);
        }

        return new Expander
        {
            Header = summary,
            IsExpanded = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = new Border
            {
                Padding = new Thickness(4, 8, 0, 0),
                Child = RenderBlocks(joined)
            }
        };
    }

    // ── Inline rendering ────────────────────────────────────────────────────

    // **bold**, `code`, and [text](url) — links render as their text (underlined); the windows
    // hosting this control offer an explicit "View on GitHub" button instead of in-text nav.
    private static void AddInlines(InlineCollection inlines, string text)
    {
        int pos = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Index > pos)
            {
                inlines.Add(new Run(text[pos..m.Index]));
            }

            if (m.Groups["b"].Success)
            {
                inlines.Add(new Run(m.Groups["b"].Value)
                {
                    FontWeight = FontWeight.SemiBold
                });
            }
            else if (m.Groups["c"].Success)
            {
                inlines.Add(new Run(m.Groups["c"].Value)
                {
                    FontFamily = new FontFamily("Consolas, Menlo, monospace")
                });
            }
            else
            {
                inlines.Add(new Run(m.Groups["lt"].Value)
                {
                    TextDecorations = TextDecorations.Underline
                });
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            inlines.Add(new Run(text[pos..]));
        }
    }

    // ── Line classification ─────────────────────────────────────────────────

    private static bool IsBullet(string trimmed) =>
        trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);

    // A continuation line extends the current paragraph/bullet: non-blank and not the start of
    // any other block shape.
    private static bool IsContinuation(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0
               && !IsBullet(trimmed)
               && !trimmed.StartsWith('>')
               && !trimmed.StartsWith('#')
               && !trimmed.StartsWith("<details", StringComparison.OrdinalIgnoreCase)
               && !trimmed.StartsWith("</details", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string line, string token)
    {
        int count = 0;
        int idx = 0;
        while ((idx = line.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += token.Length;
        }

        return count;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"(\*\*(?<b>.+?)\*\*)|(`(?<c>[^`]+)`)|(\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\))")]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageSyntax();

    [GeneratedRegex(@"</?details[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OuterDetailsTag();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SummaryTag();
}
