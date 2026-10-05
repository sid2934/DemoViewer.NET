#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>How a visit reads the demo.</summary>
public enum ParseMode
{
    /// <summary>A streaming read that keeps no frames: header, rounds, final state, a rules run.</summary>
    Forward,

    /// <summary>The full <see cref="ParsedDemo" /> held in memory for the slot.</summary>
    Retained
}

/// <summary>
///     What a pass needs from the parse of one demo. A visit reads forward only when every pass on it
///     can take a forward read; one pass needing the retained parse puts the whole visit on it, and a
///     forward pass then receives that retained parse instead.
/// </summary>
/// <param name="Mode">Forward or retained.</param>
/// <param name="Forward">What a forward read must produce. Ignored for <see cref="ParseMode.Retained" />.</param>
/// <param name="UserCommands">Whether the pass reads player inputs. A retained parse drops them when no pass does.</param>
public sealed record PassNeeds(ParseMode Mode, ForwardNeeds Forward = ForwardNeeds.None, bool UserCommands = false)
{
    /// <summary>The retained parse with every message category.</summary>
    public static PassNeeds RetainedParse { get; } = new(ParseMode.Retained, ForwardNeeds.None, true);

    /// <summary>The retained parse without player inputs.</summary>
    public static PassNeeds RetainedWithoutUserCommands { get; } = new(ParseMode.Retained);

    /// <summary>A forward read producing <paramref name="needs" />.</summary>
    public static PassNeeds ForwardRead(ForwardNeeds needs) => new(ParseMode.Forward, needs);
}

/// <summary>A pass's answer to "do you want this demo".</summary>
public enum PassInterest
{
    /// <summary>Nothing to do for this demo.</summary>
    No,

    /// <summary>Run on this demo.</summary>
    Yes,

    /// <summary>
    ///     Cannot answer until a pass it runs after has written: the demo lacks the upstream output this pass
    ///     reads. The visit includes the pass when one of its <see cref="IDemoPass.After" /> ancestors is on
    ///     it, and asks again right before its turn.
    /// </summary>
    IfUpstreamRuns
}

/// <summary>Why a demo is being visited. Higher runs sooner.</summary>
public enum PassLevel
{
    /// <summary>Opt-in sweeps over the library.</summary>
    Background = 0,

    /// <summary>A demo the library just added or found changed.</summary>
    Backlog = 1,

    /// <summary>The demo open in the shell.</summary>
    OpenDemo = 2,

    /// <summary>A user asked for it.</summary>
    UserRequested = 3
}

/// <summary>The demo a visit is about.</summary>
/// <param name="Path">The .dem path, the visit's key.</param>
public sealed record VisitedDemo(string Path)
{
    /// <summary>The file name, for labels.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>What a pass gets when its turn comes in the slot. Exactly one of the two reads is set.</summary>
public sealed class PassInput
{
    /// <param name="demo">The demo.</param>
    /// <param name="level">Why it is being visited.</param>
    /// <param name="retained">The held parse, when the visit read retained.</param>
    /// <param name="forward">The forward result, when the visit read forward.</param>
    /// <param name="cancellationToken">Fires when the visit is cancelled. A retained parse cannot stop, its passes can.</param>
    public PassInput(VisitedDemo demo, PassLevel level, ParsedDemo? retained, ForwardDemoResult? forward,
        CancellationToken cancellationToken)
    {
        if (retained is null == forward is null)
        {
            throw new ArgumentException("A pass input carries either the retained parse or the forward result.");
        }

        Demo = demo;
        Level = level;
        Retained = retained;
        Forward = forward;
        CancellationToken = cancellationToken;
    }

    /// <summary>The demo.</summary>
    public VisitedDemo Demo { get; }

    /// <summary>Why it is being visited.</summary>
    public PassLevel Level { get; }

    /// <summary>The held parse, or null when the visit read forward.</summary>
    public ParsedDemo? Retained { get; }

    /// <summary>The forward result, or null when the visit read retained.</summary>
    public ForwardDemoResult? Forward { get; }

    /// <summary>Fires when the visit is cancelled.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
///     Work that runs on every demo the library visits, registered once. A visit of a demo asks each pass
///     whether it wants the demo, reads the demo once in the mode the wanted passes need, and runs them in
///     the slot in <see cref="After" /> order, asking each again right before its turn. One parse serves
///     every pass.
///     <para>
///         <see cref="Interest" /> is called often and off the UI thread: answer from the index, never
///         from the file. <see cref="Run" /> holds the parse slot: finish synchronously, never touch the UI.
///     </para>
/// </summary>
public interface IDemoPass
{
    /// <summary>Unique across the app; the owner chip on the queue row.</summary>
    string Id { get; }

    /// <summary>Pass ids whose writes this one reads. On one visit they run before it.</summary>
    IReadOnlyList<string> After { get; }

    /// <summary>What this pass needs from the parse of <paramref name="demo" />.</summary>
    PassNeeds Needs(VisitedDemo demo);

    /// <summary>
    ///     Identifies the configuration the pass's output depends on. A change means every demo is stale for
    ///     this pass. Empty when the output never goes stale.
    /// </summary>
    string Fingerprint => string.Empty;

    /// <summary>Whether <paramref name="demo" /> still needs this pass at <paramref name="level" />.</summary>
    PassInterest Interest(VisitedDemo demo, PassLevel level);

    /// <summary>Does the pass's work on the visit's read. A throw skips this pass only.</summary>
    void Run(PassInput input);

    /// <summary>The read of <paramref name="demo" /> failed, so the pass can mark its own state.</summary>
    void OnFailed(VisitedDemo demo, Exception failure)
    {
    }
}

/// <summary>How one pass's turn on a visit ended.</summary>
public enum PassOutcome
{
    /// <summary>The pass ran.</summary>
    Ran,

    /// <summary>The pass declined the demo when asked again in the slot.</summary>
    Skipped,

    /// <summary><see cref="IDemoPass.Run" /> or <see cref="IDemoPass.Interest" /> threw.</summary>
    Failed,

    /// <summary>The read of the demo failed; <see cref="IDemoPass.OnFailed" /> was called.</summary>
    ParseFailed,

    /// <summary>The visit was cancelled, or the pass threw <see cref="OperationCanceledException" />.</summary>
    Cancelled
}

/// <summary>
///     A set of passes to run on one demo, joining the demo's visit if one is queued or compatible and
///     running. Coalesced by path like <see cref="DemoProcessingRequest" />.
/// </summary>
/// <param name="Path">The .dem path.</param>
/// <param name="Level">Why the demo is visited. Decides the queue priority.</param>
/// <param name="Passes">The passes. One already on the visit is not added twice.</param>
/// <param name="OrderHint">Within a priority, higher runs sooner (file mtime ticks for newest first).</param>
/// <param name="DisplayName">The row label, or null for the file name.</param>
/// <param name="PassEnded">
///     Told once per submitted pass how its turn ended, in the slot. The error is set for
///     <see cref="PassOutcome.Failed" /> and <see cref="PassOutcome.ParseFailed" />.
/// </param>
public sealed record DemoVisitRequest(
    string Path,
    PassLevel Level,
    IReadOnlyList<IDemoPass> Passes,
    long OrderHint = 0,
    string? DisplayName = null,
    Action<IDemoPass, PassOutcome, Exception?>? PassEnded = null);
