#region

using DemoViewer.NET.Configuration;
using System.Globalization;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Export.Pack;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The export plumbing a first-party extension renders through, built app-side so the extension holds
///     neither the heavy-job gate nor the processing queue: a 2D export job over the gate's export session, and
///     Pack Export over demo leases.
/// </summary>
/// <param name="gate">The heavy-job gate.</param>
/// <param name="queue">The processing queue.</param>
/// <param name="settings">The app's settings, which hold the export choices; null where nothing persists them.</param>
public sealed class FirstPartyExports(HeavyJobGate gate, IDemoProcessingQueue queue, SettingsService? settings = null)
{
    /// <summary>The app's settings as they stand: the export choices the 2D export shares.</summary>
    public AppSettings Settings => settings?.Current ?? new AppSettings();

    /// <summary>Changes and writes the app's settings; nothing when there is no settings file.</summary>
    public void PersistSettings(Action<AppSettings> mutate) => settings?.Write(mutate);

    /// <summary>The folder exports go to by default, or null for the app's default.</summary>
    public string? ExportOutputDirectory => settings?.Current.Playback2D.ExportOutputDirectory;

    /// <summary>An export job for <paramref name="runner" /> that takes the gate's export session while it renders.</summary>
    /// <param name="runner">What renders.</param>
    /// <param name="isLiveSyncBusy">True while a Live Sync session owns the game.</param>
    /// <param name="isReelRunning">True while a highlight reel renders.</param>
    /// <param name="log">The export's line sink.</param>
    public ExportJobService NewJob(IExportRunner runner, Func<bool>? isLiveSyncBusy, Func<bool>? isReelRunning,
        Action<string>? log) =>
        new(runner, gate, isLiveSyncBusy, isReelRunning, log);

    /// <summary>
    ///     Renders a Review Queue pack as one video, as a user-requested queue job. Each clip's demo is read by the
    ///     demo's visit and lent to the render, or lent by the shell when the demo is open. Null on the browser,
    ///     which has no ffmpeg and no files.
    /// </summary>
    public Func<PackPlan, IProgress<PackProgress>, CancellationToken, Task<PackResult>>? PackExport =>
        OperatingSystem.IsBrowser() ? null : ExportPackAsync;

    private Task<PackResult> ExportPackAsync(PackPlan plan, IProgress<PackProgress> progress, CancellationToken ct)
    {
        ILogger log = DiagnosticsLog.CreateLogger(PackExportLog.Category);
        return PackExportQueue.RunAsync(queue,
            string.Create(CultureInfo.InvariantCulture,
                $"Pack export: {plan.Segments.Count} segments to {Path.GetFileName(plan.Settings.OutputPath)}"),
            plan.Settings.OutputPath, async (relay, token) =>
            {
                using IDisposable session = await gate.EnterExportSessionAsync(token).ConfigureAwait(false);
                using PackClipRenderer clips = new(
                    (path, leaseCt) => DemoJob.LeaseAsync(queue, path, "pack export", PackExportQueue.Owner,
                        PassLevel.UserRequested, leaseCt),
                    new AnnotationStore(AppPaths.ConfigRoot),
                    log: line => PackExportLog.Line(log, line));
                PackExporter exporter = new(clips, new PackEncoder(log: line => PackExportLog.Encoder(log, line)),
                    log: line => PackExportLog.Line(log, line));
                return await exporter.ExportAsync(plan, relay, token).ConfigureAwait(false);
            }, progress, ct);
    }
}
