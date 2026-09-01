#region

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DemoViewer.NET.Controls;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The release-notes markdown renderer behind the update-notice and What's New windows
///     (v0.6.0). Pins the block shapes the REAL release bodies use: headings, hard-wrapped
///     paragraphs, bullets, blockquotes, bold, and the <c>&lt;details&gt;</c> previous-version
///     fold, and that unknown syntax degrades to text rather than throwing.
/// </summary>
public class MarkdownBlockTests
{
    /// <summary>The shapes an actual release body uses, rendered into the expected control tree.</summary>
    [Test]
    public async Task RendersReleaseBodyShapes()
    {
        await HeadlessSession.RunOnUi(() =>
        {
            const string body = """
                                ## What's new in 0.6.0

                                **The notice is a window now.** Hard-wrapped lines
                                join into one paragraph.

                                - first bullet with **bold**
                                - second bullet

                                > A quoted caveat.

                                <details>
                                <summary>What was new in 0.5.4</summary>

                                Old notes body.
                                </details>
                                """;

            StackPanel panel = (StackPanel)MarkdownBlock.RenderBlocks(body);

            // Heading · paragraph · bullet list · quote · details-expander = five blocks.
            if (panel.Children.Count != 5)
            {
                throw new InvalidOperationException($"Expected 5 blocks, got {panel.Children.Count}");
            }

            TextBlock heading = (TextBlock)panel.Children[0];
            AssertContains(InlineText(heading), "What's new in 0.6.0");

            // The hard-wrapped source joins to a single paragraph (markdown soft-wrap semantics).
            TextBlock paragraph = (TextBlock)panel.Children[1];
            AssertContains(InlineText(paragraph), "Hard-wrapped lines join into one paragraph.");
            if (!paragraph.Inlines!.OfType<Run>().Any(r => r.FontWeight == FontWeight.SemiBold))
            {
                throw new InvalidOperationException("Bold inline lost in paragraph");
            }

            StackPanel bullets = (StackPanel)panel.Children[2];
            if (bullets.Children.Count != 2)
            {
                throw new InvalidOperationException($"Expected 2 bullet rows, got {bullets.Children.Count}");
            }

            Border quote = (Border)panel.Children[3];
            AssertContains(InlineText((TextBlock)((StackPanel)quote.Child!).Children[0]), "A quoted caveat.");

            Expander details = (Expander)panel.Children[4];
            if (!Equals(details.Header, "What was new in 0.5.4") || details.IsExpanded)
            {
                throw new InvalidOperationException(
                    $"Details fold wrong: header '{details.Header}', expanded {details.IsExpanded}");
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Junk in, text out: the renderer must never throw on syntax it doesn't model.</summary>
    [Test]
    public async Task UnknownSyntax_DegradesToText()
    {
        await HeadlessSession.RunOnUi(() =>
        {
            // Unclosed details, stray brackets, an image, a table row: all must render as SOMETHING.
            StackPanel panel = (StackPanel)MarkdownBlock.RenderBlocks(
                "<details>\nnever closed\n\n| a | b |\n\n![alt text](x.png)\n[link](https://x)");
            if (panel.Children.Count == 0)
            {
                throw new InvalidOperationException("Degenerate input rendered nothing at all");
            }

            return Task.CompletedTask;
        });
    }

    private static string InlineText(TextBlock tb) =>
        string.Concat(tb.Inlines!.OfType<Run>().Select(r => r.Text));

    private static void AssertContains(string haystack, string needle)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{haystack}' does not contain '{needle}'");
        }
    }
}
