#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The engine-backed row source: evaluates the effective <c>round_facts</c> ruleset (the shipped one,
///     or a user's same-id override) on its own through <c>DemoAnalysis</c> on the forward path, and turns
///     its <c>round_facts</c> table into the two-rows-per-round shape the projection reads. Everything the
///     record knows comes out of that table; the few things the rules language cannot say (see
///     <see cref="FromTable" />) are read off the same rows, never off the demo.
/// </summary>
public sealed class EngineRoundFactsRowSource : IRoundFactsRowSource
{
    /// <summary>The table the ruleset declares under <c>show: tables:</c>.</summary>
    public const string TableName = "round_facts";

    /// <summary>The line the log carries when the effective rules hold no enabled <c>round_facts</c>.</summary>
    public const string NoRulesetDiagnostic =
        "round_facts: no rows; the effective rules carry no enabled round_facts ruleset";

    private const int SideT = 2;
    private const int SideCt = 3;

    // A tick column of a thing that did not happen that round reads 0 on the engine's side. Tick 0 is
    // before any freeze end, so it can never be a real one; the record says "did not happen" with null.
    private static readonly HashSet<string> TickColumns = new(StringComparer.Ordinal)
    {
        RoundFactsColumns.FreezeEndTick, RoundFactsColumns.EndTick, RoundFactsColumns.OfficiallyEndedTick,
        RoundFactsColumns.PlantTick, RoundFactsColumns.DefuseTick, RoundFactsColumns.ExplodeTick,
        RoundFactsColumns.FirstContactTick, RoundFactsColumns.OpeningKillTick
    };

    // Dimensions every configured table carries that say nothing about the round; kept out of Extra.
    private static readonly HashSet<string> DemoDimensions = new(StringComparer.Ordinal) { "match_id", "map" };

    private readonly RulesRoundFactsRulesetIdentity _rules;

    /// <summary>A source over its own read of the shipped-plus-user rules.</summary>
    public EngineRoundFactsRowSource() : this(new RulesRoundFactsRulesetIdentity())
    {
    }

    /// <param name="rules">
    ///     The identity whose effective doc is evaluated, so the rows and the fingerprint they are stored
    ///     under come from one read of the rules directories.
    /// </param>
    public EngineRoundFactsRowSource(RulesRoundFactsRulesetIdentity rules)
    {
        _rules = rules;
    }

    /// <inheritdoc />
    public RoundFactsTable Rows(ParsedDemo parsed)
    {
        RulesetDoc? doc = _rules.EffectiveDoc();
        if (doc is null)
        {
            return RoundFactsTable.Unavailable(NoRulesetDiagnostic);
        }

        BuildResult build = DemoAnalysis.Build(parsed, [doc]);
        if (build.ExcludedRulesets.Count > 0)
        {
            IEnumerable<RulesetCompositionDiagnostic> why = build.ExcludedRulesets.SelectMany(r => r.Diagnostics);
            return RoundFactsTable.Unavailable(
                "round_facts: the ruleset did not compose: " + string.Join("; ", why.Take(5).Select(d => $"[{d.Code}] {d.Message}")));
        }

        // Snapshots off: 0.13 projects configured tables from the forward run alone.
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions
        {
            CaptureSnapshots = false
        });
        MetricTable? table = run.ProjectConfiguredOutputs(parsed)
            .FirstOrDefault(t => string.Equals(t.Name, TableName, StringComparison.Ordinal));
        return FromTable(table, ParametersOf(doc), ClipRounds.Derive(parsed));
    }

    /// <summary>The ruleset's <c>params:</c> as it resolved: a user override's defaults are the values in force.</summary>
    public static IReadOnlyDictionary<string, object?> ParametersOf(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        foreach (ParamDef param in doc.Params)
        {
            parameters[param.Name] = param.Default;
        }

        return parameters;
    }

    /// <summary>
    ///     Turns the engine's table into the seam's rows. The row dictionary is the table's dimensions
    ///     (<c>round_number</c>, <c>side</c>, <c>slots</c>) plus its value columns, with three things done to them:
    ///     <list type="bullet">
    ///         <item>
    ///             "Did not happen" becomes null: a 0 tick, a 0 or -1 site entity, and the planter of a
    ///             round with no plant (slot 0 is a real player, so the plant tick decides).
    ///         </item>
    ///         <item>
    ///             Rounds are renumbered onto <paramref name="rounds" /> by freeze-end tick, because round
    ///             numbering is <c>ClipRound.Number</c> everywhere (overview correction 12). The engine opens a
    ///             round the server decided inside freeze time (a surrender vote) with a synthesized freeze end
    ///             the wire never carried; the clip authority has no such round, so its rows are dropped with a
    ///             diagnostic rather than shifting every later round by one.
    ///         </item>
    ///         <item>
    ///             Two columns the language cannot express are read off the rows when the ruleset does not
    ///             emit them: the victim's side of each death (the victim slot against the two sides' freeze-end
    ///             slots) and each side's score before the round (the winners so far, following each team
    ///             across the halftime swap by its slots). Both are computed before any round is dropped, so a
    ///             dropped round's winner still counts.
    ///         </item>
    ///     </list>
    ///     <c>buy_type</c> is never unavailable: a row without it is classified by <see cref="BuyTypeClassifier" />
    ///     from the side's inputs and the ruleset's parameters.
    /// </summary>
    public static RoundFactsTable FromTable(
        MetricTable? table,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<ClipRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(rounds);

        if (table is null)
        {
            return RoundFactsTable.Unavailable($"round_facts: the ruleset produced no {TableName} table");
        }

        List<Dictionary<string, object?>> cells = [];
        foreach (MetricRow row in table.Rows)
        {
            Dictionary<string, object?> cell = new(StringComparer.Ordinal);
            foreach ((string key, object? value) in row.Dimensions)
            {
                if (!DemoDimensions.Contains(key))
                {
                    cell[key] = value;
                }
            }

            foreach ((string key, object? value) in row.Values)
            {
                cell[key] = value;
            }

            NullWhatDidNotHappen(cell);
            cells.Add(cell);
        }

        HashSet<string> emitted = new(cells.SelectMany(c => c.Keys), StringComparer.Ordinal);
        List<List<Dictionary<string, object?>>> byRound =
        [
            .. cells
                .GroupBy(c => RoundFactsValues.ToInt(c.GetValueOrDefault(RoundFactsColumns.RoundNumber)) ?? int.MinValue)
                .OrderBy(g => g.Key)
                .Select(g => g.ToList())
        ];

        if (!emitted.Contains(RoundFactsColumns.KillVictimSide) && emitted.Contains(RoundFactsColumns.KillVictimSlot))
        {
            foreach (List<Dictionary<string, object?>> pair in byRound)
            {
                FillVictimSides(pair);
            }

            emitted.Add(RoundFactsColumns.KillVictimSide);
        }

        if (!emitted.Contains(RoundFactsColumns.ScoreBefore) && emitted.Contains(RoundFactsColumns.WinnerSide))
        {
            FillScores(byRound);
            emitted.Add(RoundFactsColumns.ScoreBefore);
        }

        List<string> diagnostics = [];
        Dictionary<int, int> clipByFreezeEnd = new();
        foreach (ClipRound round in rounds)
        {
            clipByFreezeEnd.TryAdd(round.StartTickFrameClock, round.Number);
        }

        List<IReadOnlyDictionary<string, object?>> kept = new(cells.Count);
        foreach (List<Dictionary<string, object?>> pair in byRound)
        {
            int? freezeEnd = pair
                .Select(c => RoundFactsValues.ToInt(c.GetValueOrDefault(RoundFactsColumns.FreezeEndTick)))
                .FirstOrDefault(t => t is not null);
            if (freezeEnd is int tick)
            {
                if (!clipByFreezeEnd.TryGetValue(tick, out int number))
                {
                    diagnostics.Add(
                        $"round {RoundFactsValues.ToText(pair[0].GetValueOrDefault(RoundFactsColumns.RoundNumber))} opens at tick {tick} " +
                        "with no round_freeze_end on the wire (decided in freeze time); its rows were dropped");
                    continue;
                }

                foreach (Dictionary<string, object?> cell in pair)
                {
                    cell[RoundFactsColumns.RoundNumber] = number;
                }
            }

            kept.AddRange(pair);
        }

        HashSet<string> unavailable = new(
            RoundFactsColumns.Known.Where(c => !emitted.Contains(c) && c != RoundFactsColumns.BuyType),
            StringComparer.Ordinal);
        return new RoundFactsTable(kept, unavailable, parameters, diagnostics);
    }

    private static void NullWhatDidNotHappen(Dictionary<string, object?> cell)
    {
        foreach (string column in TickColumns)
        {
            if (cell.TryGetValue(column, out object? value) && RoundFactsValues.ToInt(value) is 0)
            {
                cell[column] = null;
            }
        }

        if (cell.TryGetValue(RoundFactsColumns.PlantSiteEntity, out object? site) && RoundFactsValues.ToInt(site) is <= 0)
        {
            cell[RoundFactsColumns.PlantSiteEntity] = null;
        }

        if (cell.ContainsKey(RoundFactsColumns.PlanterSlot) && cell.GetValueOrDefault(RoundFactsColumns.PlantTick) is null)
        {
            cell[RoundFactsColumns.PlanterSlot] = null;
        }

        if (cell.TryGetValue(RoundFactsColumns.PlantSite, out object? letter) && letter is string { Length: 0 })
        {
            cell[RoundFactsColumns.PlantSite] = null;
        }
    }

    private static int[] SlotsOf(Dictionary<string, object?>? row) =>
        RoundFactsValues.ToIntList(row?.GetValueOrDefault(RoundFactsColumns.Slots)) ?? [];

    private static Dictionary<string, object?>? RowFor(List<Dictionary<string, object?>> pair, int side) =>
        pair.FirstOrDefault(c => RoundFactsValues.ToInt(c.GetValueOrDefault(RoundFactsColumns.Side)) == side);

    // The same list lands on both rows, so the projection's pair check sees them agree.
    private static void FillVictimSides(List<Dictionary<string, object?>> pair)
    {
        HashSet<int> ct = [.. SlotsOf(RowFor(pair, SideCt))];
        HashSet<int> t = [.. SlotsOf(RowFor(pair, SideT))];
        foreach (Dictionary<string, object?> row in pair)
        {
            int[]? victims = RoundFactsValues.ToIntList(row.GetValueOrDefault(RoundFactsColumns.KillVictimSlot));
            if (victims is null)
            {
                continue;
            }

            row[RoundFactsColumns.KillVictimSide] = victims
                .Select(slot => ct.Contains(slot) ? SideCt : t.Contains(slot) ? SideT : 0)
                .ToArray();
        }
    }

    // m_iScore follows the team, not the side, so the tally is per team. A team is the set of slots it
    // held last round: whichever pairing of this round's sides overlaps more with last round's keeps the
    // tally, which follows the halftime and overtime swaps without knowing when they happen, and a
    // reconnect or a substitute moves the set along with it.
    private static void FillScores(List<List<Dictionary<string, object?>>> byRound)
    {
        int[]? teamA = null, teamB = null;
        int scoreA = 0, scoreB = 0;
        foreach (List<Dictionary<string, object?>> pair in byRound)
        {
            Dictionary<string, object?>? ctRow = RowFor(pair, SideCt);
            Dictionary<string, object?>? tRow = RowFor(pair, SideT);
            int[] ct = SlotsOf(ctRow);
            int[] t = SlotsOf(tRow);
            if (teamA is null || teamB is null)
            {
                teamA = ct;
                teamB = t;
            }

            bool aOnCt = Overlap(ct, teamA) + Overlap(t, teamB) >= Overlap(ct, teamB) + Overlap(t, teamA);
            if (ctRow is not null)
            {
                ctRow[RoundFactsColumns.ScoreBefore] = aOnCt ? scoreA : scoreB;
            }

            if (tRow is not null)
            {
                tRow[RoundFactsColumns.ScoreBefore] = aOnCt ? scoreB : scoreA;
            }

            int? winner = pair
                .Select(c => RoundFactsValues.ToInt(c.GetValueOrDefault(RoundFactsColumns.WinnerSide)))
                .FirstOrDefault(w => w is SideT or SideCt);
            if (winner is int side)
            {
                if ((side == SideCt) == aOnCt)
                {
                    scoreA++;
                }
                else
                {
                    scoreB++;
                }
            }

            // An empty side (a missing row) keeps the team's last known slots.
            (int[] onCt, int[] onT) = aOnCt ? (teamA, teamB) : (teamB, teamA);
            onCt = ct.Length > 0 ? ct : onCt;
            onT = t.Length > 0 ? t : onT;
            (teamA, teamB) = aOnCt ? (onCt, onT) : (onT, onCt);
        }
    }

    private static int Overlap(int[] a, int[] b) => a.Count(b.Contains);
}
