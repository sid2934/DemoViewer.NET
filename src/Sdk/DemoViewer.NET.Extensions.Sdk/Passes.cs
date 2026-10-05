using CS2DemoKit.Parser;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>A pass's answer to "does this demo need you".</summary>
public enum DemoInterest
{
    /// <summary>Nothing to do for this demo.</summary>
    No,

    /// <summary>Run on this demo.</summary>
    Yes,

    /// <summary>
    ///     Cannot tell yet: the demo lacks what a pass named in <c>after</c> writes. The pass joins the demo's
    ///     visit when one of those passes is on it, and is asked again right before its turn.
    /// </summary>
    AfterUpstream
}

/// <summary>What a pass gets when its turn comes on a demo's visit.</summary>
public interface IPassContext
{
    /// <summary>The demo.</summary>
    string DemoPath { get; }

    /// <summary>The demo's parse, shared with every other pass on the visit. Read only; do not keep it.</summary>
    ParsedDemo Parsed { get; }

    /// <summary>
    ///     Fires when the visit is cancelled or the app shuts down. The parse cannot stop, the pass can; throw
    ///     <see cref="OperationCanceledException" /> to end the turn.
    /// </summary>
    CancellationToken CancellationToken { get; }
}

/// <summary>
///     Work that runs on every demo the library visits, registered once with
///     <see cref="IExtensionContributions.Pass" />. A visit asks every pass whether it wants the demo, reads the
///     demo once, and runs the passes that do in <c>after</c> order, asking each again right before its turn.
///     One parse serves the library, the host's passes and every extension's.
///     <para>
///         A pass that throws is skipped for that demo for the rest of the session and counted against the
///         extension. A pass that keeps overrunning its time budget is switched off for the session.
///     </para>
/// </summary>
public interface IExtensionPass
{
    /// <summary>Unique across the app, and the id other passes name in <c>after</c>.</summary>
    string Id { get; }

    /// <summary>False lets the parse leave out player inputs when no other pass on the visit reads them.</summary>
    bool ReadsUserCommands => true;

    /// <summary>
    ///     Whether <paramref name="demoPath" /> needs this pass. Asked when the library adds or changes the demo,
    ///     when the host re-checks the library, and right before the pass's turn. Called often and never on the UI
    ///     thread: answer from memory or an index, never from the demo file.
    /// </summary>
    DemoInterest Interest(string demoPath);

    /// <summary>
    ///     Does the pass's work on the demo's parse. Runs on a queue thread holding the one parse slot: finish
    ///     synchronously, and never touch the UI from here.
    /// </summary>
    void Run(IPassContext context);

    /// <summary>The demo could not be read, so the pass can mark its own state. The next check may ask again.</summary>
    void OnFailed(string demoPath)
    {
    }

    /// <summary>How urgent <paramref name="demoPath" /> is: <see cref="JobPriority.UserRequested" /> for one the user asked for.</summary>
    JobPriority PriorityFor(string demoPath) => JobPriority.Background;

    /// <summary>Order among demos of one priority; higher runs sooner (the file's write time, for newest first).</summary>
    long OrderHint(string demoPath) => 0;
}

/// <summary>The scheduling of an extension's own passes.</summary>
public interface IExtensionPasses
{
    /// <summary>
    ///     Asks every pass again about <paramref name="demoPath" /> and visits it ahead of the backlog when one
    ///     wants it. For a demo the user asked to have processed.
    /// </summary>
    void Request(string demoPath);

    /// <summary>Asks every pass about every demo in the library, off the UI thread. For a setting that widened what a pass wants.</summary>
    void RecheckAll();

    /// <summary>True while one of the extension's own passes named <paramref name="passId" /> has a demo queued or running.</summary>
    bool IsBusy(string passId);
}
