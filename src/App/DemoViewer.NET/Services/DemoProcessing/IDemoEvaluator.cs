#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     A feature that needs a background parse of a demo (demo-processing-queue "one parse, many
///     evaluators"). The <see cref="DemoScheduler" /> asks every registered evaluator's cheap
///     <see cref="Wants" /> for a demo it was told about and submits the interested ones together as one
///     visit; the queue parses the demo ONCE and each interested evaluator's <see cref="Evaluate" /> runs on
///     that single held <see cref="ParsedDemo" />, in After order. Adding a new background feature =
///     implementing this interface and registering it: no extra parse. Wrapped as an
///     <see cref="IDemoPass" /> by <see cref="EvaluatorPassAdapter" />.
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
    ///     Cheap interest/staleness check for <paramref name="path" />: NO <see cref="ParsedDemo" /> yet.
    ///     Returning false means this evaluator has nothing to do for the demo, so it is not submitted on its
    ///     behalf. Asked again right before the evaluator's turn in the slot. Called off the UI thread: answer
    ///     from the index, never from the file.
    /// </summary>
    bool Wants(string path);

    /// <summary>
    ///     True when <see cref="Wants" /> is false only because the demo lacks what an evaluator this one runs
    ///     after writes (the library's parse stamp, the Round Facts rows). The visit then carries this evaluator
    ///     when that upstream evaluator is on it, and asks <see cref="Wants" /> again once upstream has run.
    ///     Default false: the evaluator reads nothing another one writes.
    /// </summary>
    bool WantsAfterUpstream(string path) => false;

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

    /// <summary>
    ///     Whether <see cref="Evaluate" /> reads the parse's user commands. The shared background parse drops
    ///     them when no evaluator on the demo does.
    /// </summary>
    bool ReadsUserCommands => true;

    /// <summary>
    ///     What a forward pass must produce for <see cref="EvaluateForward" /> on <paramref name="path" />, or
    ///     null when this evaluator needs the retained parse. The queue reads a demo forward only when every
    ///     owner on the entry can take it.
    /// </summary>
    ForwardNeeds? ForwardFor(string path) => null;

    /// <summary>Like <see cref="Evaluate" />, on a forward pass. Only called when <see cref="ForwardFor" /> was non-null.</summary>
    void EvaluateForward(string path, ForwardDemoResult pass)
    {
        // no-op by default
    }

    /// <summary>Within-tier ordering hint, higher = sooner (typically the file's mtime ticks, newest first).</summary>
    long OrderHint(string path) => 0;
}
