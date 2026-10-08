#region

using Avalonia;
using Avalonia.Headless;

#endregion

[assembly: AvaloniaTestApplication(typeof(DemoViewer.NET.AppTests.TestAppBuilder))]

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Avalonia entry point for headless UI tests. Uses the REAL <see cref="DemoViewer.NET.App" /> so
///     its styles, brushes, converters, and card/hex DataTemplates are loaded, and the Skia backend
///     (UseHeadlessDrawing = false) so rendered frames can be captured to PNG for inspection.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            })
            .WithInterFont();
}
