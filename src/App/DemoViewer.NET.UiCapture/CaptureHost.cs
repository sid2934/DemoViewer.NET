#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     Headless Skia render host for the UI/UX design-iteration workflow — renders a real,
///     App-themed Avalonia control to a PNG with no display. Extracted from the App.Tests headless
///     harness (<c>HeadlessSession</c>): it uses the REAL <see cref="DemoViewer.NET.App" /> so styles,
///     <c>DarkPalette</c> tokens, converters, and DataTemplates load into every capture, and the Skia
///     backend (<c>UseHeadlessDrawing = false</c>) so frames actually rasterize.
///     <para>
///         Scope: single UserControls / panels with a mock DataContext. Views hosting a live MSAGL
///         <c>GraphView</c> (the Analysis graph) do NOT settle geometry headlessly — capture those via
///         the Visualization SvgExporter or a real display instead.
///     </para>
/// </summary>
public static class CaptureHost
{
    private static readonly Lazy<HeadlessUnitTestSession> _session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(CaptureHost)));

    // The central theme registry (built-ins + <config>/themes/ drop-ins), so a capture can render under ANY
    // theme id — Dark / Light / System, the built-in customs (High-Contrast, E-Girl), or a drop-in being
    // authored. Installed once (on the UI thread) before the first themed render.
    private static readonly ThemeRegistry _themeRegistry = new();
    private static bool _themesInstalled;

    // Same auto-close + compositor double-tick discipline as the test harness: the headless session
    // installs no application lifetime, so windows leak and an animating leaked window can wedge the
    // compositor. We close what a capture opened and drain the render backlog before returning.
    private static readonly HashSet<Window> _openWindows = [];
    private static bool _tracking;

    /// <summary>Where capture PNGs land by default (matches the App.Tests convention).</summary>
    public static string ArtifactDir
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "demoviewer-uitests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>The Avalonia entry the headless session boots — the real app for full styling.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            })
            .WithInterFont();

    /// <summary>
    ///     Resolves a theme <b>id</b> to its <see cref="ThemeVariant" />, re-scanning the drop-in folder first
    ///     so a theme file authored just before this run is picked up. Returns <c>null</c> for a blank id
    ///     (render with the app default). An unknown id resolves to <c>Default</c> (System).
    /// </summary>
    public static ThemeVariant? ResolveTheme(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        _themeRegistry.Reload(); // UI-free (filesystem + dictionaries) — safe on the calling thread
        return _themeRegistry.VariantFor(id);
    }

    // Merge the custom-variant override dictionaries into the running app once, on the UI thread, so a custom
    // variant's tokens actually resolve during render (a native Light/Dark render doesn't need this).
    private static void EnsureThemesInstalled()
    {
        if (_themesInstalled)
        {
            return;
        }

        _themeRegistry.Install(Application.Current!);
        _themesInstalled = true;
    }

    private static void EnsureTracking()
    {
        if (_tracking)
        {
            return;
        }

        _tracking = true;
        Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (s, _) => _openWindows.Add((Window)s!));
        Window.WindowClosedEvent.AddClassHandler(typeof(Window), (s, _) => _openWindows.Remove((Window)s!));
    }

    /// <summary>Resolves a relative/blank output path against <see cref="ArtifactDir" />.</summary>
    public static string ResolveOut(string? outPath, string fallbackName) =>
        string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(ArtifactDir, fallbackName)
            : Path.IsPathRooted(outPath)
                ? outPath
                : Path.Combine(ArtifactDir, outPath);

    /// <summary>
    ///     Renders <paramref name="factory" />'s control (optionally with <paramref name="dataContext" />)
    ///     at <paramref name="size" /> and writes a PNG to <paramref name="outPng" />. Returns the path.
    /// </summary>
    public static Task<string> CaptureView(
        Func<Control> factory, Size size, string outPng, object? dataContext = null,
        ThemeVariant? theme = null) =>
        RunOnUi(() =>
        {
            // Set the APP variant (not just the window) BEFORE building content, so code-built variants
            // whose helpers resolve tokens via Application.Current.ActualThemeVariant (Tok()/WrapInShell in
            // Variants.cs) honor the requested theme too — otherwise those mocks would pull the Default→Light
            // dict under --theme Dark and diverge. Real DynamicResource markup already tracks the window
            // variant; this makes the code-built mocks consistent with it.
            if (theme is not null)
            {
                EnsureThemesInstalled(); // so a custom variant's tokens resolve (no-op for native Light/Dark)
                Application.Current!.RequestedThemeVariant = theme;
            }

            Control content = factory();
            if (dataContext is not null)
            {
                content.DataContext = dataContext;
            }

            Render(content, size, outPng, theme);
            return outPng;
        });

    /// <summary>
    ///     Renders two variants side-by-side (A | divider | B) with captions into one PNG — the
    ///     comparison surface for an option-fork. Each half is laid out at <paramref name="halfSize" />.
    /// </summary>
    public static Task<string> CaptureAb(
        Func<Control> optionA, Func<Control> optionB, Size halfSize, string outPng,
        string labelA = "Option A", string labelB = "Option B", ThemeVariant? theme = null) =>
        RunOnUi(() =>
        {
            // See CaptureView: set the app variant before building content so code-built mocks follow the theme.
            if (theme is not null)
            {
                EnsureThemesInstalled();
                Application.Current!.RequestedThemeVariant = theme;
            }

            Control host = ComposeAb(optionA(), optionB(), halfSize, labelA, labelB);
            Size full = new(halfSize.Width * 2 + 40, halfSize.Height + 48);
            Render(host, full, outPng, theme);
            return outPng;
        });

    private static Grid ComposeAb(Control a, Control b, Size half, string labelA, string labelB)
    {
        static Control Column(string label, Control body, double w, double h)
        {
            StackPanel stack = new()
            {
                Spacing = 6,
                Margin = new Thickness(10)
            };
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new Border
            {
                Width = w,
                Height = h,
                Child = body,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x50))
            });
            return stack;
        }

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*")
        };
        Control colA = Column(labelA, a, half.Width, half.Height);
        Control colB = Column(labelB, b, half.Width, half.Height);
        Border divider = new()
        {
            Width = 1,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3c))
        };
        Grid.SetColumn(colA, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(colB, 2);
        grid.Children.Add(colA);
        grid.Children.Add(divider);
        grid.Children.Add(colB);
        return grid;
    }

    private static void Render(Control content, Size size, string outPng, ThemeVariant? theme = null)
    {
        Window window = new()
        {
            Width = size.Width,
            Height = size.Height,
            Content = content
        };
        if (theme is not null)
        {
            window.RequestedThemeVariant = theme; // drives ThemeDictionaries + DynamicResource resolution
        }

        window.Show();

        // Settle: the headless render timer never ticks on its own — force it (twice, to flush async
        // template/measure work) with a job pump between.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap? frame = window.CaptureRenderedFrame()
                                 ?? throw new InvalidOperationException("CaptureRenderedFrame returned null.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        frame.Save(outPng);
        window.Close();
    }

    /// <summary>Runs <paramref name="body" /> on the shared headless UI thread and returns its result.</summary>
    private static async Task<string> RunOnUi(Func<string> body)
    {
        Task<string> dispatched = _session.Value.Dispatch(() =>
        {
            EnsureTracking();
            string result;
            try
            {
                result = body();
            }
            finally
            {
                foreach (Window w in _openWindows.ToArray())
                {
                    try
                    {
                        w.Close();
                    }
                    catch
                    {
                        /* leaked-window teardown mess; ignore */
                    }
                }

                _openWindows.Clear();
                try
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                }
                catch
                {
                    /* post-body flush of any leaked animating content; ignore */
                }
            }

            return Task.FromResult(result);
        }, CancellationToken.None);

        return await dispatched.WaitAsync(TimeSpan.FromSeconds(120));
    }
}
