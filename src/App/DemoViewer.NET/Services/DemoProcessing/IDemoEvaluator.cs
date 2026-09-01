#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     A feature that needs a background parse of a demo (demo-processing-queue "one parse, many
///     evaluators"). The <see cref="DemoEvaluationCoordinator" /> polls every registered evaluator's
///     cheap <see cref="Wants" /> for a path and, for each interested evaluator, submits ONE queue
///     request tagged <see cref="Id" />; the queue coalesces them by path so the demo is parsed ONCE
///     and each interested evaluator's <see cref="Evaluate" /> runs on that single held
///     <see cref="ParsedDemo" />. Adding a new background feature = implementing this interface and
///     registering it: no extra parse.
///     <para>
///         WASM-safe: the abstraction assumes no ASP.NET and no physical-file specifics beyond a path
///         string, mirroring <see cref="IDemoProcessingQueue" />.
///     </para>
/// </summary>
public interface IDemoEvaluator
{
    /// <summary>Stable owner tag (the queue's per-owner identity / UI chip), e.g. "library", "highlights".</summary>
    string Id { get; }

    /// <summary>
    ///     Cheap interest/staleness check for <paramref name="path" />: NO <see cref="ParsedDemo" /> yet
    ///     (mirrors the current per-feeder "needs work?" gate). Returning false means this evaluator has
    ///     nothing to do for the demo, so it is not submitted on its behalf.
    /// </summary>
    bool Wants(string path);

    /// <summary>
    ///     Does this evaluator's work on the single held parse. Runs INSIDE the queue's gate slot with
    ///     the <see cref="ParsedDemo" /> still in memory (the one-heavy-parse invariant), so it must
    ///     finish SYNCHRONOUSLY before the slot releases. Failures are isolated by the queue: a throw
    ///     here never fails the parse or another evaluator.
    /// </summary>
    void Evaluate(string path, ParsedDemo parsed);

    /// <summary>
    ///     Called (in-slot) when the parse itself FAILED, so the evaluator can mark its own state.
    ///     Default no-op for evaluators that don't track a failure state.
    /// </summary>
    void OnFailed(string path)
    {
        // no-op by default
    }

    /// <summary>
    ///     Priority this evaluator wants for <paramref name="path" /> (default Background; a forced
    ///     user rescan returns <see cref="DemoJobPriority.UserRequested" />).
    /// </summary>
    DemoJobPriority PriorityFor(string path) => DemoJobPriority.Background;

    /// <summary>Within-tier ordering hint, higher = sooner (typically the file's mtime ticks, newest first).</summary>
    long OrderHint(string path) => 0;

    /// <summary>
    ///     Opportunistic hand-off of a demo that is ALREADY parsed elsewhere (an interactive open, or
    ///     another evaluator's tier-2), routed via <see cref="DemoEvaluationCoordinator.FanOutParsed" />.
    ///     Unlike <see cref="Evaluate" /> this is NOT gated on <see cref="Wants" />: it is the "here is a
    ///     free parse, refresh from it if useful" hook (the order-independence the old Library→Highlights
    ///     piggyback provided): the evaluator decides internally whether the demo is one it tracks and
    ///     whether a refresh is warranted. It runs SYNCHRONOUSLY on the caller's thread with the parse held
    ///     (the caller offloads the UI thread), and failures are isolated by the coordinator. Default no-op.
    /// </summary>
    void OnParsedOpportunistically(string path, ParsedDemo parsed)
    {
        // no-op by default
    }
}
