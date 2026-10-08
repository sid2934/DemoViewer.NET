#region

using Avalonia;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>A first-party extension's own starting choices for the 2D export dialog over its scene.</summary>
/// <param name="FormatId">The output format id, e.g. <c>"gif"</c>.</param>
/// <param name="Fps">Output frames per second.</param>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="IncludeHud">Whether the HUD layers start on.</param>
/// <param name="IncludeHudClock">Whether the round clock starts on.</param>
/// <param name="IncludeAnnotations">Whether the ink starts on.</param>
/// <param name="IncludeVision">Whether the vision cones start on.</param>
public sealed record SceneExportDefaults(
    string FormatId,
    int Fps,
    int Width,
    int Height,
    bool IncludeHud,
    bool IncludeHudClock,
    bool IncludeAnnotations,
    bool IncludeVision);

/// <summary>
///     The 2D export's dialog, ffmpeg and theme palette, for a first-party extension that exports a scene of its
///     own. The extension's choices seed the dialog; the output folder and the quality come from the 2D export's
///     saved settings, and only the folder is written back.
/// </summary>
public static class FirstPartySceneExport
{
    /// <summary>Where the app keeps the ffmpeg it downloads, or null where it keeps none.</summary>
    public static string? ManagedFfmpegDirectory => FfmpegDependency.ManagedDirectory;

    /// <summary>The scene palette for the app's current theme. Off the UI thread the theme is unreadable: dark.</summary>
    public static ScenePalette ThemePalette() =>
        Dispatcher.UIThread.CheckAccess()
            ? ScenePaletteFactory.Build(Application.Current?.ActualThemeVariant)
            : ScenePalette.Dark;

    /// <summary>The export sizes the 2D tab offers.</summary>
    public static IReadOnlyList<ExportSizeOption> SizePresets => Playback2DExportDialogViewModel.SizePresets;

    /// <summary>Opens the 2D export's dialog over an extension's scene.</summary>
    /// <param name="exports">The app's export plumbing, which holds the saved folder and quality; null seeds neither.</param>
    /// <param name="defaults">The extension's starting choices.</param>
    /// <param name="ranges">The ranges offered, the default first.</param>
    /// <param name="job">The job the dialog starts.</param>
    /// <param name="outputFrameCount">How many frames a range renders to, from start, end, fps and speed.</param>
    /// <param name="isLiveSyncBusy">True while a Live Sync session owns the game.</param>
    /// <param name="captureInk">The ink to burn in, taken at Start on the UI thread.</param>
    /// <param name="scene">The dialog's title, file stem, sizes and layers.</param>
    public static Playback2DExportDialogViewModel NewDialog(FirstPartyExports? exports, SceneExportDefaults defaults,
        IReadOnlyList<ExportRangeOption> ranges, IExportJobService job, Func<int, int, int, double, int> outputFrameCount,
        Func<bool>? isLiveSyncBusy, Func<AnnotationSession?> captureInk, ExportDialogScene scene)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        Playback2DSettings saved = exports?.Settings.Playback2D ?? new Playback2DSettings();
        Playback2DSettings seed = new()
        {
            ExportFormatId = defaults.FormatId,
            ExportFps = defaults.Fps,
            ExportWidth = defaults.Width,
            ExportHeight = defaults.Height,
            ExportOutputDirectory = saved.ExportOutputDirectory,
            ExportIncludeHud = defaults.IncludeHud,
            ExportIncludeHudClock = defaults.IncludeHudClock,
            ExportIncludeAnnotations = defaults.IncludeAnnotations,
            ExportIncludeVision = defaults.IncludeVision,
            ExportQuality = saved.ExportQuality,
            ExportEncoder = EncoderLadder.Auto
        };

        Playback2DExportDialogViewModel dialog = new(
            ranges,
            seed,
            job,

            // An empty fixed script: the session's first-frame fit frames the map's bounds.
            captureLiveCamera: null,
            outputFrameCount: outputFrameCount,
            ffmpegLocator: static () => FfmpegLocator.Locate(FfmpegDependency.ManagedDirectory),
            isLiveSyncSessionActive: isLiveSyncBusy,

            // Only the folder is written, at Start below; the rest stay the extension's, never the 2D tab's.
            persistDefaults: null,
            fileExists: null,
            captureInk: captureInk,
            acquireFfmpeg: Playback2DExportDialogViewModel.ProductionAcquisition(FfmpegDependency.ManagedDirectory),
            capturePalette: ThemePalette,
            scene: scene);

        if (exports is not null)
        {
            dialog.StartRequested += () =>
            {
                if (Path.GetDirectoryName(dialog.OutputPath) is { Length: > 0 } folder)
                {
                    exports.PersistSettings(settings => settings.Playback2D.ExportOutputDirectory = folder);
                }
            };
        }

        return dialog;
    }
}
