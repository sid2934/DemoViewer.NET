#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

#endregion

namespace DemoViewer.NET.NodifyTests;

/// <summary>
///     A minimal Avalonia application that merges nothing but the Fluent theme and the vendored
///     Nodify theme. Deliberately not the product's <c>App</c>: the point of these tests is what
///     <c>avares://Nodify/Theme.axaml</c> alone brings, so anything else in the resource chain would
///     be a place for a missing Nodify resource to hide.
/// </summary>
public sealed class NodifyProbeApp : Application
{
    /// <summary>The URI of the vendored theme's entry point.</summary>
    public static Uri ThemeUri { get; } = new("avares://Nodify/Theme.axaml");

    /// <inheritdoc />
    public override void Initialize()
    {
        // Fluent, because Nodify's own themes template the Nodify controls and leave the ordinary
        // Avalonia primitives they nest (ItemsControl, ScrollViewer) to the application's theme.
        Styles.Add(new FluentTheme());

        Resources.MergedDictionaries.Add(
            new ResourceInclude(new Uri("avares://DemoViewer.NET.Nodify.Tests/"))
            {
                Source = ThemeUri
            });
    }
}

/// <summary>Avalonia entry point for the headless session. Skia, so frames really rasterise.</summary>
public static class TestAppBuilder
{
    /// <summary>Builds the headless application under test.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<NodifyProbeApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            })
            .WithInterFont();
}

/// <summary>
///     One shared headless session for the assembly, because Avalonia wants a single UI thread.
///     Far smaller than the App suite's equivalent on purpose: this assembly holds a handful of
///     tests, so none of that suite's wedge attribution and retry machinery earns its place here.
/// </summary>
public static class NodifyHeadlessSession
{
    private static readonly Lock _gate = new();
    private static HeadlessUnitTestSession? _session;

    /// <summary>Where a captured frame is written for inspection after a failure.</summary>
    public static string ArtifactDir
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "demoviewer-nodify-guard");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static HeadlessUnitTestSession Session
    {
        get
        {
            lock (_gate)
            {
                return _session ??= HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            }
        }
    }

    /// <summary>
    ///     Runs an async body on the headless UI thread and awaits the body itself. The overload
    ///     taking a bare <c>Func&lt;Task&gt;</c> binds to <c>Func&lt;TResult&gt;</c> with
    ///     <c>TResult = Task</c>, which awaits only the dispatch, so a failure after the body's
    ///     first yield would pass silently. Hence the <c>Func&lt;Task&lt;bool&gt;&gt;</c>.
    /// </summary>
    public static async Task RunOnUi(Func<Task> work)
    {
        Task<bool> dispatched = Session.Dispatch(async () =>
        {
            await work();
            return true;
        }, CancellationToken.None);

        await dispatched.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
