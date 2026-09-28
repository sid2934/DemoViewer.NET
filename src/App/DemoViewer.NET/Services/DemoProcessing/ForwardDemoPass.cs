#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>What a forward pass has to produce beyond the header, rounds and frame clock it always reads.</summary>
[Flags]
public enum ForwardNeeds
{
    None = 0,

    /// <summary>The final CCSTeam scores, clans and coach slots (library tier 2).</summary>
    FinalState = 1,

    /// <summary>The merged highlights plus <c>round_facts</c> rules run, without snapshots.</summary>
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
    public FinalTeamState? FinalState { get; init; }
    public AnalysisRun? Run { get; init; }
}

/// <summary>The scoreboard state at the last frame, as the library card stores it.</summary>
public sealed record FinalTeamState(int? Ct, int? T, string? CtClan, string? TClan, HashSet<int> CoachSlots)
{
    /// <summary>The only classes the read looks at; the tracker stores nothing else.</summary>
    public static IReadOnlySet<string> Classes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "CCSTeam", "CCSPlayerController" };

    public static EntityTracker NewTracker() => new() { StoreClassFilter = Classes };

    /// <summary>
    ///     Scores and clans from CCSTeam, coaches from the controller at entity index slot + 1. Scores are
    ///     both or nothing, and nothing when they sum to zero (warmup only); coach slots come back either way.
    /// </summary>
    public static FinalTeamState Read(EntityTracker tracker, IEnumerable<int> playerSlots)
    {
        int? ct = null, t = null;
        string? ctClan = null, tClan = null;
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != "CCSTeam")
            {
                continue;
            }

            int teamNum = CoerceInt(ent["m_iTeamNum"]);
            int score = CoerceInt(ent["m_iScore"]);
            string clan = ent["m_szClanTeamname"] as string ?? "";
            if (teamNum == 2)
            {
                t = score;
                if (clan.Length > 0)
                {
                    tClan = clan;
                }
            }
            else if (teamNum == 3)
            {
                ct = score;
                if (clan.Length > 0)
                {
                    ctClan = clan;
                }
            }
        }

        HashSet<int> coachSlots = [];
        foreach (int slot in playerSlots)
        {
            EntityState? controller = tracker.CurrentEntities[slot + 1];
            if (controller is not null
                && controller.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase)
                && CoerceInt(controller["m_iCoachingTeam"]) != 0)
            {
                coachSlots.Add(slot);
            }
        }

        return ct is null || t is null || ct + t == 0
            ? new FinalTeamState(null, null, null, null, coachSlots)
            : new FinalTeamState(ct, t, ctClan, tClan, coachSlots);
    }

    public static FinalTeamState Empty() => new(null, null, null, null, []);

    private static int CoerceInt(object? v) => v switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        sbyte s => s,
        _ => 0
    };
}

/// <summary>
///     One forward read serving every consumer that does not need random frame access: the library's
///     tier-2 facts and one merged rules build for highlights and round facts. Peak memory is the
///     reader's window plus the evaluation state, not the demo.
/// </summary>
public static class ForwardDemoPass
{
    /// <summary>The configured output round facts are read from; the only one the bare run records.</summary>
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
    ///     A build over <paramref name="docs" /> whose configured outputs are cut to <c>round_facts</c>. A
    ///     bare run cannot project a per-event output, and recording the other tables costs for nothing.
    /// </summary>
    public static BuildResult Build(IDemoFrameSource source, IReadOnlyList<RulesetDoc> docs) =>
        OnlyRoundFactsOutput(DemoAnalysis.Build(source, docs));

    /// <inheritdoc cref="Build(IDemoFrameSource, IReadOnlyList{RulesetDoc})" />
    public static BuildResult Build(ParsedDemo parsed, IReadOnlyList<RulesetDoc> docs) =>
        OnlyRoundFactsOutput(DemoAnalysis.Build(parsed, docs));

    private static BuildResult OnlyRoundFactsOutput(BuildResult build) => build with
    {
        Outputs = build.Outputs?.Where(o => string.Equals(o.Id, RoundFactsTable, StringComparison.Ordinal)).ToList()
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
        if ((needs & ForwardNeeds.FinalState) != 0)
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
    /// <param name="cancellationToken">Stops the read; the pass then throws <see cref="OperationCanceledException" />.</param>
    public static ForwardDemoResult Run(
        DemoReader reader,
        ForwardNeeds needs,
        IReadOnlyList<RulesetDoc>? docs,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // Build before Configure: the build probes the dialect, which needs the reader unstarted.
        BuildResult? build = (needs & ForwardNeeds.Rules) != 0
            ? Build(reader, docs ?? throw new ArgumentNullException(nameof(docs)))
            : null;
        reader.Configure(PlanFor(build, needs));

        Tap tap = new(reader, (needs & ForwardNeeds.FinalState) != 0 ? FinalTeamState.NewTracker() : null,
            progress, cancellationToken);
        AnalysisRun? run = null;
        if (build is not null)
        {
            run = DemoAnalysis.Evaluate(tap, build, new AnalysisOptions
            {
                CaptureSnapshots = false,
                CancellationToken = cancellationToken
            });
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
                : tap.Frames == 0 ? FinalTeamState.Empty()
                : FinalTeamState.Read(tap.Tracker, demo.Players.Keys),
            Run = run
        };
    }

    // Everything happens inside TryReadNext: the evaluator may pull from a producer thread, and the
    // scanner releases a frame's folded data once it has applied it.
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
