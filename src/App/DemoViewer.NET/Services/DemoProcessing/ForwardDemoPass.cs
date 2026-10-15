#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoProcessing.Collectors;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>What a forward pass has to produce beyond the header, rounds and frame clock it always reads.</summary>
[Flags]
public enum ForwardNeeds
{
    None = 0,

    /// <summary>
    ///     The library's final scores, clans and coaches. A rules run carries them as tables; a read without one
    ///     replays the two entity classes in the tap.
    /// </summary>
    FinalState = 1,

    /// <summary>The merged highlights plus stamped rules run, without snapshots.</summary>
    Rules = 2
}

/// <summary>
///     What one forward read of a demo leaves behind. No frames: the reader drops each one after the
///     rules evaluation and the tap have seen it.
/// </summary>
public sealed class ForwardDemoResult
{
    public required DemoDescriptor Demo { get; init; }
    public required IReadOnlyList<ClipRound> Rounds { get; init; }
    public required int FrameCount { get; init; }
    public required int FirstServerTick { get; init; }
    public required int LastServerTick { get; init; }

    /// <summary>The tap's replay of the final state; null when the read ran rules, which carry it as tables.</summary>
    public LibraryTeamState? FinalState { get; init; }
    public AnalysisRun? Run { get; init; }

    /// <summary>What the read's collectors put; empty when it carried none.</summary>
    internal CollectorResults Collected { get; init; } = CollectorResults.None;
}

/// <summary>
///     One forward read serving every consumer that does not need random frame access: the library's
///     tier-2 facts and one merged rules build for highlights and round facts. Peak memory is the
///     reader's window plus the evaluation state, not the demo.
/// </summary>
public static class ForwardDemoPass
{
    /// <summary>The configured output round facts are read from; what a build records when told nothing else.</summary>
    public const string RoundFactsTable = "round_facts";

    private const string FreezeEndEvent = "round_freeze_end";
    private const int CheckEveryFrames = 1024;

    /// <summary>The reader options <c>DemoAnalysis.Run(path)</c> uses: a 1024-frame parallel decode window.</summary>
    public static ParseOptions ReaderOptions(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        ReadAheadFrames = 1024
    };

    /// <summary>
    ///     A build over <paramref name="docs" /> whose configured outputs are cut to <paramref name="outputs" />:
    ///     every table a bare run records costs nodes and memory on every demo, so only the tables of rulesets
    ///     whose stored outputs are stale for this demo are kept. Null keeps <c>round_facts</c> alone.
    /// </summary>
    public static BuildResult Build(DemoReader reader, IReadOnlyList<RulesetDoc> docs, IReadOnlySet<string>? outputs = null,
        CancellationToken cancellationToken = default) =>
        Build(reader, docs, outputs, CollectorSet.Empty, cancellationToken);

    /// <inheritdoc cref="Build(DemoReader, IReadOnlyList{RulesetDoc}, IReadOnlySet{string}, CancellationToken)" />
    public static BuildResult Build(ParsedDemo parsed, IReadOnlyList<RulesetDoc> docs, IReadOnlySet<string>? outputs = null) =>
        Build(parsed, docs, outputs, CollectorSet.Empty);

    // The observed reads must reach the build: it activates their columns, and the run's view refuses any other.
    internal static BuildResult Build(DemoReader reader, IReadOnlyList<RulesetDoc> docs, IReadOnlySet<string>? outputs,
        CollectorSet collectors, CancellationToken cancellationToken = default) =>
        CutOutputs(DemoAnalysis.Build(reader, docs, collectors.DeclareReads(new AnalysisOptions { CancellationToken = cancellationToken })), outputs);

    internal static BuildResult Build(ParsedDemo parsed, IReadOnlyList<RulesetDoc> docs, IReadOnlySet<string>? outputs, CollectorSet collectors) =>
        CutOutputs(DemoAnalysis.Build(parsed, docs, collectors.DeclareReads(new AnalysisOptions())), outputs);

    // The library tables are recorded on every read: they are two small tables, and the library pass
    // must find them on whichever run its visit made.
    private static BuildResult CutOutputs(BuildResult build, IReadOnlySet<string>? outputs) => build with
    {
        Outputs = build.Outputs?.Where(o => LibraryTeamTables.Tables.Contains(o.Id)
                                            || (outputs?.Contains(o.Id) ?? string.Equals(o.Id, RoundFactsTable, StringComparison.Ordinal))).ToList()
    };

    /// <summary>The build's own plan, widened to what the tap reads.</summary>
    public static DecodePlan PlanFor(BuildResult? build, ForwardNeeds needs)
    {
        const MessageCategories headerAndTables =
            MessageCategories.Header | MessageCategories.StringTables | MessageCategories.GameEvents;
        DecodePlan plan = build is not null
            ? DemoAnalysis.PlanDecode(build)
            : new DecodePlan { Categories = headerAndTables, GameEventNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };

        MessageCategories categories = plan.Categories | headerAndTables;
        if (TapReplays(needs))
        {
            categories |= MessageCategories.Schema | MessageCategories.Entities;
        }

        IReadOnlySet<string>? events = plan.GameEventNames;
        if (events is not null && !events.Contains(FreezeEndEvent))
        {
            events = new HashSet<string>(events, StringComparer.OrdinalIgnoreCase) { FreezeEndEvent };
        }

        return plan with { Categories = categories, GameEventNames = events };
    }

    /// <summary>
    ///     Reads <paramref name="reader" /> to the end once. It must not have read a frame yet; the caller
    ///     keeps ownership and disposes it.
    /// </summary>
    /// <param name="reader">An unstarted reader.</param>
    /// <param name="needs">What to produce beyond header, rounds and clock.</param>
    /// <param name="docs">The merged rulesets; required when <paramref name="needs" /> has <see cref="ForwardNeeds.Rules" />.</param>
    /// <param name="progress">Fraction of the file consumed, at most every <see cref="CheckEveryFrames" /> frames.</param>
    /// <param name="outputs">The configured outputs the rules run records; null records <c>round_facts</c> alone.</param>
    /// <param name="cancellationToken">Stops the read; the pass then throws <see cref="OperationCanceledException" />.</param>
    public static ForwardDemoResult Run(
        DemoReader reader,
        ForwardNeeds needs,
        IReadOnlyList<RulesetDoc>? docs,
        Action<double>? progress = null,
        IReadOnlySet<string>? outputs = null,
        CancellationToken cancellationToken = default) =>
        Run(reader, needs, docs, progress, outputs, CollectorSet.Empty, cancellationToken);

    /// <summary>
    ///     <see cref="Run(DemoReader, ForwardNeeds, IReadOnlyList{RulesetDoc}, Action{double}, IReadOnlySet{string}, CancellationToken)" />
    ///     with <paramref name="collectors" /> on the rules run. Collectors ride only a rules run, so they need
    ///     <see cref="ForwardNeeds.Rules" />.
    /// </summary>
    internal static ForwardDemoResult Run(
        DemoReader reader,
        ForwardNeeds needs,
        IReadOnlyList<RulesetDoc>? docs,
        Action<double>? progress,
        IReadOnlySet<string>? outputs,
        CollectorSet collectors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!collectors.IsEmpty && (needs & ForwardNeeds.Rules) == 0)
        {
            throw new ArgumentException("collectors ride the rules run; the read does not make one", nameof(collectors));
        }

        // Build before Configure: the build probes the dialect, which needs the reader unstarted.
        BuildResult? build = (needs & ForwardNeeds.Rules) != 0
            ? Build(reader, docs ?? throw new ArgumentNullException(nameof(docs)), outputs, collectors, cancellationToken)
            : null;
        reader.Configure(PlanFor(build, needs));

        Tap tap = new(reader, TapReplays(needs) ? LibraryFinalStateReplay.NewTracker() : null, progress, cancellationToken);
        AnalysisRun? run = null;
        CollectorObserver? observer = null;
        if (build is not null)
        {
            AnalysisOptions options;
            (options, observer) = collectors.Declare(new AnalysisOptions
            {
                CaptureSnapshots = false,
                CancellationToken = cancellationToken
            });
            run = DemoAnalysis.Evaluate(tap, build, options);
        }
        else
        {
            while (tap.TryReadNext(out _))
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        DemoDescriptor demo = run?.Demo ?? reader.Enrichment.Snapshot();
        return new ForwardDemoResult
        {
            Demo = demo,
            Rounds = ClipRounds.Derive(tap.FreezeEnds),
            FrameCount = tap.Frames,
            FirstServerTick = tap.FirstServerTick,
            LastServerTick = tap.LastServerTick,
            FinalState = tap.Tracker is null ? null
                : tap.Frames == 0 ? LibraryTeamState.Empty()
                : LibraryFinalStateReplay.Read(tap.Tracker, demo.Players.Keys),
            Run = run,
            Collected = observer?.Results ?? CollectorResults.None
        };
    }

    // Everything happens inside TryReadNext: the evaluator may pull from a producer thread, and the
    // scanner releases a frame's folded data once it has applied it.
    // A rules run carries the final state as tables; only a read without one replays it.
    private static bool TapReplays(ForwardNeeds needs) =>
        (needs & ForwardNeeds.FinalState) != 0 && (needs & ForwardNeeds.Rules) == 0;

    private sealed class Tap(
        DemoReader inner,
        EntityTracker? tracker,
        Action<double>? progress,
        CancellationToken cancellationToken) : IDemoFrameSource
    {
        public List<GameEvent> FreezeEnds { get; } = [];
        public EntityTracker? Tracker => tracker;
        public int Frames { get; private set; }
        public int FirstServerTick { get; private set; }
        public int LastServerTick { get; private set; }

        public IDemoEnrichmentView Enrichment => inner.Enrichment;
        public int? FrameCount => inner.FrameCount;
        public double? Progress => inner.Progress;
        public bool SupportsRandomAccess => false;
        IReadOnlyList<DemoFrame>? IDemoFrameSource.Frames => null;
        public IReadOnlyList<DemoFrame> SignonPrefix => inner.SignonPrefix;
        public DemoFrame? LastInstanceBaselineFullPacket => inner.LastInstanceBaselineFullPacket;

        public bool TryReadNext([NotNullWhen(true)] out DemoFrame? frame)
        {
            if (!inner.TryReadNext(out frame))
            {
                return false;
            }

            if (Frames == 0)
            {
                FirstServerTick = frame.ServerTick;
            }

            LastServerTick = frame.ServerTick;
            if (++Frames % CheckEveryFrames == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inner.Progress is double fraction)
                {
                    progress?.Invoke(fraction);
                }
            }

            foreach (NetMessage message in frame.DecodedMessages)
            {
                if (message is GameEventMessage { DecodedEvent: { Name: FreezeEndEvent } evt })
                {
                    FreezeEnds.Add(evt);
                }
            }

            tracker?.AdvanceOneFrame(frame);
            return true;
        }

        public bool TryPeekNext([NotNullWhen(true)] out DemoFrame? frame) => inner.TryPeekNext(out frame);
    }
}
