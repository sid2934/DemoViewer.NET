#region

using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One applied fire of a traced stat or highlight (the applied-fire slice) — the demo
///     time and player at which the selected node had a rising edge (highlights / flag conjunctions) or
///     its trigger edge applied (value-node stats like <c>count:</c>/<c>sum:</c>).
/// </summary>
/// <param name="FrameIndex">Zero-based demo frame index of the fire, or <c>-1</c> when the frame could not be located.</param>
/// <param name="Tick">Server tick of the fire.</param>
/// <param name="RoundNumber">Live round number at the fire, or <c>null</c> when the run tracked no round context.</param>
/// <param name="PlayerSlot">Owning player slot, or <c>null</c> for a game-scoped (<c>for: match</c>) fire.</param>
/// <param name="PlayerName">Owning player display name, or <c>null</c> for a game-scoped fire.</param>
public sealed record WorkbenchTraceFire(
    int FrameIndex,
    int Tick,
    int? RoundNumber,
    int? PlayerSlot,
    string? PlayerName)
{
    /// <summary>Player display label (name, else <c>slot N</c>, else <c>game</c>).</summary>
    public string PlayerLabel =>
        PlayerName ?? (PlayerSlot is { } slot ? $"slot {slot}" : "game");

    /// <summary>Round display label (the number, or an em-dash when unknown/warmup).</summary>
    public string RoundLabel =>
        RoundNumber is { } r && r > 0 ? r.ToString(CultureInfo.InvariantCulture) : "—";
}

/// <summary>
///     A pickable trace target — a declared stat or highlight and how many times it fired in
///     the last evaluation. <see cref="Id" /> is the report lookup key; <see cref="Label" /> is the bare
///     rule id the author sees.
/// </summary>
/// <param name="Id">The report lookup key (<c>kind:ruleId</c>), unique across stats and highlights.</param>
/// <param name="Kind">The target kind (<c>highlight</c> or <c>stat</c>).</param>
/// <param name="Label">The bare declared rule id.</param>
/// <param name="FireCount">Number of applied fires captured for the target.</param>
public sealed record WorkbenchTraceTarget(string Id, string Kind, string Label, int FireCount)
{
    /// <summary>Combobox display: <c>label  (kind, N fires)</c>.</summary>
    public string Display =>
        $"{Label}  ({Kind}, {FireCount} fire{(FireCount == 1 ? "" : "s")})";
}

/// <summary>
///     The result of tracing one evaluation: every declared stat/highlight as a
///     <see cref="WorkbenchTraceTarget" />, plus the applied fires for each. UI-free and unit-testable
///     (like <see cref="WorkbenchCompletionSource" />) — built from the M5 <see cref="EvaluationResult" />.
/// </summary>
public sealed class WorkbenchTraceReport
{
    /// <summary>An empty report (no evaluation, or a filesystem-less/degraded run).</summary>
    public static readonly WorkbenchTraceReport Empty =
        new([], new Dictionary<string, IReadOnlyList<WorkbenchTraceFire>>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, IReadOnlyList<WorkbenchTraceFire>> _firesByTarget;

    internal WorkbenchTraceReport(
        IReadOnlyList<WorkbenchTraceTarget> targets,
        IReadOnlyDictionary<string, IReadOnlyList<WorkbenchTraceFire>> firesByTarget)
    {
        Targets = targets;
        _firesByTarget = firesByTarget;
    }

    /// <summary>Every declared stat + highlight in the evaluated rulesets, in declared order.</summary>
    public IReadOnlyList<WorkbenchTraceTarget> Targets { get; }

    /// <summary>The applied fires for a target (by its <see cref="WorkbenchTraceTarget.Id" />), or empty.</summary>
    public IReadOnlyList<WorkbenchTraceFire> FiresFor(string targetId) =>
        _firesByTarget.TryGetValue(targetId, out IReadOnlyList<WorkbenchTraceFire>? fires) ? fires : [];
}

/// <summary>
///     Builds a <see cref="WorkbenchTraceReport" /> from an evaluation (the applied-fire slice).
///     Two ground-truth sources feed it, both already recorded by the M5 evaluation:
///     <list type="bullet">
///         <item>
///             <b>Highlights + flag conjunctions</b> — the <see cref="RuleChainTimeline" />, which records
///             every rising edge of a <c>_chain_&lt;id&gt;</c> (highlight) or <c>&lt;statId&gt;</c>
///             (<c>flag: when:</c>) logic node with full player attribution.
///         </item>
///         <item>
///             <b>Value-node stats</b> (<c>count:</c>/<c>sum:</c>/<c>capture:</c>…) — the
///             <see cref="EvaluationResult.AppliedMessagesByEdge" /> map: the trigger edge whose
///             <see cref="StateEdge.WrittenNode" /> is the stat's node applied at those message indices.
///             Player attribution comes from the materialized-player node map.
///         </item>
///     </list>
///     <para>
///         Known limitation (documented, deferred): targets key on the <b>bare</b> rule id, so two rulesets
///         declaring the same stat/highlight id would merge fires; clause-level "why did/didn't it fire at
///         tick T" verdicts are the remaining M6 stretch (see the phase plan).
///     </para>
/// </summary>
public static class WorkbenchTraceModel
{
    private const string ChainKeyPrefix = "_chain_";
    private const string RoundNumberNodeName = "RoundNumber";
    private const string RoundNumberRuleId = "round_number";

    /// <summary>Traces the evaluation. A null result (no snapshots) yields <see cref="WorkbenchTraceReport.Empty" />.</summary>
    public static WorkbenchTraceReport Build(
        EvaluationResult? result, IReadOnlyList<RulesetDoc> docs, ParsedDemo? demo)
    {
        if (result is null)
        {
            return WorkbenchTraceReport.Empty;
        }

        // Declared vocabulary, in source order, deduped by bare id (cross-ruleset id collisions merge).
        HashSet<string> highlightIds = new(StringComparer.Ordinal);
        HashSet<string> statIds = new(StringComparer.Ordinal);
        List<(string Kind, string Id)> order = [];
        foreach (RulesetDoc doc in docs)
        {
            foreach (HighlightDef h in doc.Highlights)
            {
                if (highlightIds.Add(h.Id))
                {
                    order.Add(("highlight", h.Id));
                }
            }

            foreach (StatDef s in doc.Stats)
            {
                if (statIds.Add(s.Id))
                {
                    order.Add(("stat", s.Id));
                }
            }
        }

        Dictionary<int, int> roundByFrame = BuildRoundByFrame(result, demo);
        Dictionary<DemoFrame, int> frameIndexOf = new(ReferenceEqualityComparer.Instance);
        if (demo is not null)
        {
            for (int i = 0; i < demo.Frames.Count; i++)
            {
                frameIndexOf[demo.Frames[i]] = i;
            }
        }

        int? Round(int frameIndex)
        {
            return roundByFrame.Count > 0 ? roundByFrame.GetValueOrDefault(frameIndex, 0) : null;
        }

        // ── source 1: timeline rising edges, grouped by chain name ──────────────────────────────────
        Dictionary<string, List<WorkbenchTraceFire>> timelineByChain = new(StringComparer.Ordinal);
        foreach (RuleChainEvent ev in result.Timeline.Events)
        {
            if (!timelineByChain.TryGetValue(ev.ChainName, out List<WorkbenchTraceFire>? list))
            {
                timelineByChain[ev.ChainName] = list = [];
            }

            list.Add(new WorkbenchTraceFire(
                ev.FrameIndex, ev.Tick, Round(ev.FrameIndex), ev.PlayerSlot, ev.PlayerName));
        }

        // ── source 2: applied trigger edges for value-node stats, keyed by written-node name ────────
        Dictionary<StateNode, (int Slot, string Name)> nodePlayer =
            new(ReferenceEqualityComparer.Instance);
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            foreach (StateNode node in mp.Nodes)
            {
                nodePlayer[node] = (mp.PlayerSlot, mp.PlayerName);
            }
        }

        Dictionary<string, List<WorkbenchTraceFire>> appliedByStat = new(StringComparer.Ordinal);
        if (result.AppliedMessagesByEdge is { } appliedByEdge)
        {
            foreach ((StateEdge edge, List<int> messageIndices) in appliedByEdge)
            {
                if (edge.WrittenNode is not { } written || !statIds.Contains(written.Name))
                {
                    continue; // not a declared value-node stat's trigger edge
                }

                (int? slot, string? name) = nodePlayer.TryGetValue(written, out (int Slot, string Name) p)
                    ? (p.Slot, p.Name)
                    : ((int?)null, (string?)null);

                if (!appliedByStat.TryGetValue(written.Name, out List<WorkbenchTraceFire>? list))
                {
                    appliedByStat[written.Name] = list = [];
                }

                foreach (int idx in messageIndices)
                {
                    if (idx < 0 || idx >= result.Messages.Count)
                    {
                        continue;
                    }

                    DemoFrame frame = result.Messages[idx].Frame;
                    int frameIndex = frameIndexOf.TryGetValue(frame, out int fi) ? fi : -1;
                    list.Add(new WorkbenchTraceFire(
                        frameIndex, frame.ServerTick, frameIndex >= 0 ? Round(frameIndex) : null, slot, name));
                }
            }
        }

        // ── assemble targets in declared order ──────────────────────────────────────────────────────
        List<WorkbenchTraceTarget> targets = [];
        Dictionary<string, IReadOnlyList<WorkbenchTraceFire>> firesByTarget =
            new(StringComparer.Ordinal);

        foreach ((string kind, string id) in order)
        {
            List<WorkbenchTraceFire> fires = kind == "highlight"
                ? timelineByChain.GetValueOrDefault(ChainKeyPrefix + id) ?? []
                // A flag: when: stat lowers to a conjunction named by its id (timeline); every other
                // stat kind writes a value node (applied edges).
                : timelineByChain.GetValueOrDefault(id)
                  ?? appliedByStat.GetValueOrDefault(id) ?? [];

            fires.Sort(static (a, b) =>
            {
                int byTick = a.Tick.CompareTo(b.Tick);
                return byTick != 0 ? byTick : (a.PlayerSlot ?? -1).CompareTo(b.PlayerSlot ?? -1);
            });

            string key = kind + ":" + id;
            targets.Add(new WorkbenchTraceTarget(key, kind, id, fires.Count));
            firesByTarget[key] = fires;
        }

        return new WorkbenchTraceReport(targets, firesByTarget);
    }

    /// <summary>
    ///     Best-effort frame-index → live round-number map, mirroring
    ///     <c>ConfiguredOutputProjector.BuildRoundByFrame</c> (that helper is Analysis-internal). Empty
    ///     when the run tracked no <c>round_number</c> node or carried no messages — callers then report a
    ///     null round.
    /// </summary>
    private static Dictionary<int, int> BuildRoundByFrame(EvaluationResult result, ParsedDemo? demo)
    {
        Dictionary<int, int> roundByFrame = new();
        if (demo is null || result.Messages.Count == 0)
        {
            return roundByFrame;
        }

        int roundIdx = -1;
        for (int i = 0; i < result.FinalTrackedNodes.Count; i++)
        {
            string name = result.FinalTrackedNodes[i].Name;
            if (name is RoundNumberNodeName or RoundNumberRuleId)
            {
                roundIdx = i;
                break;
            }
        }

        if (roundIdx < 0)
        {
            return roundByFrame;
        }

        Dictionary<DemoFrame, int> frameIndexOf = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < demo.Frames.Count; i++)
        {
            frameIndexOf[demo.Frames[i]] = i;
        }

        int current = 0;
        for (int m = 0; m < result.Messages.Count; m++)
        {
            if (result.MessageSnapshots[m, roundIdx] is { IsActive: true, NumericValue: { } rn })
            {
                current = (int)rn;
            }

            if (frameIndexOf.TryGetValue(result.Messages[m].Frame, out int fi))
            {
                roundByFrame[fi] = current;
            }
        }

        return roundByFrame;
    }
}
