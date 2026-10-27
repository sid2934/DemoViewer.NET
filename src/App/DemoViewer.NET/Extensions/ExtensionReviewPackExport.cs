#region

using DemoViewer.NET.Extensions.Sdk.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export.Pack;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The host's half of <see cref="IExtensionReviewPackExport" />: the Review Queue planned and rendered
///     as one pack, with the segment tree kept here. A <see cref="ReviewPackPlan" /> carries the numbers a
///     summary strip shows and the real <see cref="PackPlan" /> behind them, so the published surface names
///     no Playback2D type and a plan cannot be assembled by hand.
///     <para>
///         <b>This sits above the scene export, not beside it.</b> The run delegate is the app's own
///         <c>FirstPartyExports.PackExport</c>: a user-requested queue job that takes the heavy-job gate's
///         single export session, leases each demo through its visit and loops the clips through the one
///         renderer. Taken as a delegate rather than reaching for <c>FirstPartyExports</c> so the demo
///         content hash the app already wired into the annotation store comes through untouched; an
///         annotation store built here without it would re-hash every demo per pack and lose the ink of any
///         demo whose sidecar is keyed by the cached hash.
///     </para>
///     <para>
///         <b>UI thread only.</b> A run hands the caller user-level demo leases and the one export session,
///         which is a lock rather than a priority, so a background caller cannot be demoted into it and is
///         refused instead. The only legitimate caller is a click.
///     </para>
/// </summary>
internal sealed class ExtensionReviewPackExport : IExtensionReviewPackExport
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<bool> _onUiThread;
    private readonly ReviewQueue _queue;
    private readonly Func<PackPlan, IProgress<PackProgress>, CancellationToken, Task<PackResult>> _run;

    /// <param name="run">Renders a planned pack off the UI thread: the app's own pack export.</param>
    /// <param name="queue">The queue the pack is planned over.</param>
    /// <param name="defaultOutputDirectory">Where a new pack's path starts, or null for the caller's default.</param>
    /// <param name="fileExists">The existence probe for a clip's demo; <see cref="File.Exists" /> when null.</param>
    /// <param name="onUiThread">Whether the caller is on the UI thread; always true when null.</param>
    internal ExtensionReviewPackExport(Func<PackPlan, IProgress<PackProgress>, CancellationToken, Task<PackResult>> run,
        ReviewQueue queue, string? defaultOutputDirectory = null, Func<string, bool>? fileExists = null,
        Func<bool>? onUiThread = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(queue);
        _run = run;
        _queue = queue;
        DefaultOutputDirectory = defaultOutputDirectory;
        _fileExists = fileExists ?? File.Exists;
        _onUiThread = onUiThread ?? (static () => true);
    }

    /// <summary>MP4 first: it is the one that plays on any phone, and it is what a pack opens on.</summary>
    public IReadOnlyList<string> Formats { get; } = [ExportFormats.Mp4, ExportFormats.WebM, ExportFormats.Gif];

    /// <inheritdoc />
    public string? DefaultOutputDirectory { get; }

    /// <inheritdoc />
    public string? Validate(string outputPath, string formatId)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(formatId);
        return PackPlanner.Validate(PackSettings.For(outputPath, formatId));
    }

    /// <inheritdoc />
    public ReviewPackPlan Plan(string outputPath, string formatId)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(formatId);
        Require(nameof(Plan));

        // Thousands of clips share a few hundred demos; probe each path once per plan.
        Dictionary<string, bool> probed = new(StringComparer.Ordinal);
        PackSettings settings = PackSettings.For(outputPath, formatId);
        PackPlan plan = PackPlanner.Plan(_queue.Entries, settings,
            path => probed.TryGetValue(path, out bool exists) ? exists : probed[path] = _fileExists(path));
        return new ReviewPackPlan
        {
            Native = plan,
            ClipCount = plan.Clips.Count,
            DemoCount = plan.DemoCount,
            TitleCardCount = plan.Segments.Count(s => s is PackTitleCard),
            TrimmedCount = plan.Clips.Count(c => c.Trimmed),
            Seconds = plan.Seconds,
            SkippedCount = plan.Skipped.Count,
            SkipReasons = [.. plan.Skipped.Select(k => k.Reason).Distinct(StringComparer.Ordinal)],
            OutputDescription = settings.IsGif ? PackPlanner.GifFolder(settings.OutputPath) : settings.OutputPath
        };
    }

    /// <inheritdoc />
    public async Task<ReviewPackResult> RunAsync(ReviewPackPlan plan, IProgress<ReviewPackProgress> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        Require(nameof(RunAsync));
        if (plan.Native is not PackPlan native)
        {
            throw new ArgumentException("That plan did not come from this host's Plan.", nameof(plan));
        }

        PackResult result = await _run(native, new Relay(progress), ct).ConfigureAwait(true);
        return new ReviewPackResult(result.ClipsRendered,
            [.. result.Failed.Select(f => f.Reason).Distinct(StringComparer.Ordinal)]);
    }

    // A plain forwarder, not a Progress<T>: Progress posts even from its own context, so wrapping the
    // caller's own Progress in a second one costs an extra dispatcher hop per report, and the last report
    // can then land after the run's continuation and overwrite what it wrote.
    private sealed class Relay(IProgress<ReviewPackProgress> to) : IProgress<PackProgress>
    {
        public void Report(PackProgress value) =>
            to.Report(new ReviewPackProgress(value.Segment, value.SegmentCount, value.Label));
    }

    private void Require(string member)
    {
        if (!_onUiThread())
        {
            throw new InvalidOperationException(
                $"IExtensionReviewPackExport.{member} is UI-thread only; post the call to the UI thread.");
        }
    }
}
