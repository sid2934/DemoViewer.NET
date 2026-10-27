namespace DemoViewer.NET.Extensions.Sdk.Export;

/// <summary>
///     One planned review pack: the numbers a summary strip shows, and the host's own plan behind them.
///     <para>
///         <b>An opaque token.</b> The segment tree a pack renders from is the host's, so it is not
///         published and this is what stands in for it: get a plan from
///         <see cref="IExtensionReviewPackExport.Plan" />, show its numbers, hand it back to
///         <see cref="IExtensionReviewPackExport.RunAsync" />. Planning is pure, so a plan may be thrown
///         away and asked for again whenever the queue or the chosen output moves.
///     </para>
/// </summary>
public sealed class ReviewPackPlan
{
    internal ReviewPackPlan()
    {
    }

    /// <summary>How many queued clips the pack will render.</summary>
    public int ClipCount { get; internal init; }

    /// <summary>How many demos those clips come from.</summary>
    public int DemoCount { get; internal init; }

    /// <summary>How many section title cards the pack holds.</summary>
    public int TitleCardCount { get; internal init; }

    /// <summary>How many clips run past the format's frame cap and will be cut at it.</summary>
    public int TrimmedCount { get; internal init; }

    /// <summary>The pack's running time, in seconds, caps applied.</summary>
    public double Seconds { get; internal init; }

    /// <summary>How many queued clips the pack leaves out.</summary>
    public int SkippedCount { get; internal init; }

    /// <summary>Why clips were left out, each reason once, for the summary line.</summary>
    public IReadOnlyList<string> SkipReasons { get; internal init; } = [];

    /// <summary>Where the pack's files go, as a line to show the user: a file, or a folder per clip.</summary>
    public string OutputDescription { get; internal init; } = "";

    /// <summary>The host's own plan. The SDK cannot name its type, and an extension has no use for it.</summary>
    internal object Native { get; init; } = new();
}

/// <summary>Where a pack export is.</summary>
/// <param name="Segment">The segment being written, one-based; 0 before the first.</param>
/// <param name="SegmentCount">Segments in the pack.</param>
/// <param name="Label">What is being written: a section's title, or a clip's number and note.</param>
public readonly record struct ReviewPackProgress(int Segment, int SegmentCount, string Label);

/// <summary>What one pack export wrote.</summary>
/// <param name="ClipsRendered">Clips that made it in.</param>
/// <param name="FailureReasons">Why clips failed to render; the pack went on without them.</param>
public sealed record ReviewPackResult(int ClipsRendered, IReadOnlyList<string> FailureReasons);

/// <summary>
///     The host's review pack export: the whole review queue as one video, the sections as title cards and
///     the clips in order from however many demos, each demo's saved ink burned in. Resolve it from the
///     host context's pack export; null means this host writes no files, which is the browser build, and a
///     pack surface stays hidden.
///     <para>
///         <b>This is a privileged call.</b> <see cref="RunAsync" /> reads every demo the queue names and
///         takes the host's single export session for the duration, so it is a user's click and nothing
///         else: it must be called from the UI thread and throws otherwise. The session is a lock rather
///         than a priority, so a background caller cannot be demoted into it.
///     </para>
/// </summary>
public interface IExtensionReviewPackExport
{
    /// <summary>The format ids a pack can be written as, in the order to offer them; the default first.</summary>
    IReadOnlyList<string> Formats { get; }

    /// <summary>The folder a new pack's path should start in, or null for the caller's own default.</summary>
    string? DefaultOutputDirectory { get; }

    /// <summary>Why this output cannot be written, as a line to show the user, or null when it can.</summary>
    /// <param name="outputPath">Where the pack would go.</param>
    /// <param name="formatId">One of <see cref="Formats" />.</param>
    string? Validate(string outputPath, string formatId);

    /// <summary>
    ///     Plans the pack over the host's queue as it stands. Pure: nothing is parsed, rendered or written.
    /// </summary>
    /// <param name="outputPath">Where the pack goes.</param>
    /// <param name="formatId">One of <see cref="Formats" />.</param>
    ReviewPackPlan Plan(string outputPath, string formatId);

    /// <summary>
    ///     Renders a plan. Submitted as the user's own job, so it runs while the queue is paused and ahead of
    ///     the backlog; a clip that fails on its own is left out and reported rather than ending the pack.
    /// </summary>
    /// <param name="plan">A plan from <see cref="Plan" />.</param>
    /// <param name="progress">Where it is, reported from a worker.</param>
    /// <param name="ct">Cancels the pack; the half-written file does not survive.</param>
    /// <exception cref="InvalidOperationException">Called from a thread other than the UI thread.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct" /> was cancelled.</exception>
    Task<ReviewPackResult> RunAsync(ReviewPackPlan plan, IProgress<ReviewPackProgress> progress, CancellationToken ct);
}
