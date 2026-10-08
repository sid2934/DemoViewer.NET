#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThirdPartyFake.ViewModels;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The SDK UI kit's controls as an extension sees them: the status chip in every dot state, solid, pulsing and
///     hollow, on the strip surface; the moved controls side by side; and the third-party fake's tab resolved
///     through a bare ContentControl from its own namespace. Render each with <c>--theme dark</c> and
///     <c>--theme light</c>; <c>uikit-controls</c> needs <c>--size 660x660</c> and the fake <c>--size 660x440</c>.
/// </summary>
public static partial class Variants
{
    // A 1x1 red GIF87a: enough for GifView to decode and paint a frame without a file in the repository.
    private const string OnePixelGif = "R0lGODdhAQABAPAAAP8AAAAAACwAAAAAAQABAAACAkQBADs=";

    // The host's stack as the shell draws it, off the strip's right end. The flood posts ten cards and
    // shows the cap's three; the other extension's card stays.
    private static Panel UiKitNotifications()
    {
        DemoViewer.NET.Extensions.ExtensionScope hello = new("dev.example.hello", "pack.hello", "Hello", typeof(Variants).Assembly);
        DemoViewer.NET.Extensions.ExtensionScope loud = new("dev.example.loud", "pack.loud", "Loud", typeof(Variants).Assembly);
        DemoViewer.NET.Extensions.ExtensionFaults faults = new([hello, loud], static a => a());
        DemoViewer.NET.Extensions.NotificationCenter center = new(static a => a());
        IExtensionNotifications helloNotes = center.For(faults.GuardFor(hello));
        IExtensionNotifications loudNotes = center.For(faults.GuardFor(loud));

        helloNotes.Post(new Notification("greeted", NotificationSeverity.Success, "Greeted de_mirage.dem", "212,480 frames.")
        {
            Action = new NotificationAction("Show", static () => { })
        });
        for (int i = 1; i <= 10; i++)
        {
            NotificationSeverity severity = (NotificationSeverity)(i % 4);
            loudNotes.Post(new Notification("loud" + i, severity, $"Loud message {i}",
                i % 2 == 0 ? null : "A body long enough to wrap onto a second line inside the card's fixed width."));
        }

        Panel root = new() { Width = 720, Height = 520, Background = Tok(ThemeTokens.ShellBg) };
        root.Children.Add(new DemoViewer.NET.Controls.StatusStrip
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            StatusText = "Ready.",
            RightText = "de_mirage"
        });
        root.Children.Add(new DemoViewer.NET.Controls.NotificationTray
        {
            Items = center.Cards,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 34)
        });
        return root;
    }

    private static Border UiKitStatusChips()
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("110,*,*,*"),
            Margin = new Thickness(12),
            RowSpacing = 6,
            ColumnSpacing = 8
        };

        string[] treatments = ["solid", "pulsing", "hollow"];
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int column = 0; column < treatments.Length; column++)
        {
            grid.Children.Add(Cell(new TextBlock { Text = treatments[column], Classes = { StyleClasses.ColLabel } }, 0, column + 1));
        }

        StatusChipDotState[] states = Enum.GetValues<StatusChipDotState>();
        for (int row = 0; row < states.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(Cell(new TextBlock
            {
                Text = states[row].ToString(),
                Classes = { StyleClasses.Mono },
                Foreground = Tok(ThemeTokens.TextMid),
                VerticalAlignment = VerticalAlignment.Center
            }, row + 1, 0));

            for (int column = 0; column < treatments.Length; column++)
            {
                StatusChipViewModel chip = new()
                {
                    DotState = states[row],
                    IsPulsing = column == 1,
                    IsHollow = column == 2,
                    Label = $"Kit · {states[row].ToString().ToLowerInvariant()}"
                };
                grid.Children.Add(Cell(new Border
                {
                    Background = Tok(ThemeTokens.PanelHeaderBg),
                    Padding = new Thickness(4, 2),
                    Child = new StatusChip { DataContext = chip }
                }, row + 1, column + 1));
            }
        }

        return new Border { Width = 640, Background = Tok(ThemeTokens.ShellBg), Child = grid };

        static Control Cell(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            return control;
        }
    }

    private static Border UiKitControls()
    {
        string gif = Path.Combine(Path.GetTempPath(), "demoviewer-uicapture-uikit.gif");
        File.WriteAllBytes(gif, Convert.FromBase64String(OnePixelGif));

        StackPanel root = new() { Margin = new Thickness(16), Spacing = 12 };

        root.Children.Add(Section("MarkdownBlock", new MarkdownBlock
        {
            Markdown = "## Release notes\n\nA **bold** claim and some `code`.\n\n- first item\n- second item"
        }));

        root.Children.Add(Section("ParseLinkChip", new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new ParseLinkChip { Label = "DemoParser.Parse", Detail = "entry", SourceBadge = "DemoParser.cs:42", WebFallback = "https://example.com" },
                new ParseLinkChip { Label = "FieldDecoder.Decode", Detail = "no link", SourceBadge = "FieldDecoder.cs", Indent = new Thickness(16, 0, 0, 0) }
            }
        }));

        root.Children.Add(Section("KeyValueTable", new KeyValueTable
        {
            Rows =
            [
                new KvpRow("Demos indexed", "1 204", false, null),
                new KvpRow("Pending", "3", true, "11"),
                new KvpRow("Last scan", "2 minutes ago", false, null)
            ]
        }));

        root.Children.Add(Section("GameIcon and GifView", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new GameIcon { Key = "equipment/ak47", IconHeight = 16, Foreground = Tok(ThemeTokens.AccentInteractive) },
                new GameIcon { Key = "modifier/headshot", IconHeight = 16, Foreground = Tok(ThemeTokens.AccentError) },
                new GameIcon { Key = "ui/missing_on_purpose", IconHeight = 16, Fallback = "?", Foreground = Tok(ThemeTokens.TextDim) },
                new GifView { Source = gif, Width = 24, Height = 24 }
            }
        }));

        root.Children.Add(Section("StyleClasses", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Button { Content = "primary", Classes = { StyleClasses.Primary } },
                new Button { Content = "ghost", Classes = { StyleClasses.Ghost } },
                new Button { Content = "chip", Classes = { StyleClasses.Chip } },
                new Border { Classes = { StyleClasses.Badge }, Child = new TextBlock { Text = "badge" } }
            }
        }));

        return WrapInShell(new ScrollViewer { Content = root }, 640, 620);

        static Control Section(string caption, Control body) => new Border
        {
            Classes = { StyleClasses.Card },
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { new TextBlock { Text = caption, Classes = { StyleClasses.SectionLabel } }, body }
            }
        };
    }

    private static Border UiKitThirdPartyFake() => new Border
    {
        Width = 640,
        Height = 420,
        Background = Tok(ThemeTokens.ShellBg),
        Child = new ContentControl { Content = new FakeTabViewModel() }
    };
}
