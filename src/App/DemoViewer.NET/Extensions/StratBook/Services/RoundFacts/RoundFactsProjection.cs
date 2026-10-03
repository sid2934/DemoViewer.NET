#region

using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     Turns the <c>round_facts</c> table into the record: pairs the two rows of each round by side,
///     builds the kill timeline from the list columns, maps known columns onto typed fields and
///     everything else into <see cref="RoundFacts.Extra" />, and says per column whether the engine
///     provided it. Round numbers are <c>ClipRound.Number</c> (overview correction 12) and the freeze
///     end is the deriver's tick; a row that disagrees is kept and says so in its warnings.
/// </summary>
public static class RoundFactsProjection
{
    private const int SideT = 2;
    private const int SideCt = 3;

    /// <summary>Projects one demo's table onto its rounds.</summary>
    /// <param name="rounds">The frame-clock rounds from <c>ClipRounds.Derive</c>, in order.</param>
    /// <param name="table">The ruleset's output.</param>
    /// <param name="thresholds">
    ///     The classifier parameters, or null to read them from <paramref name="table" />'s parameters
    ///     with the shipped defaults behind any the ruleset did not declare.
    /// </param>
    public static RoundFactsRows Project(
        IReadOnlyList<ClipRound> rounds,
        RoundFactsTable table,
        BuyThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(table);

        thresholds ??= ThresholdsFrom(table.Parameters);
        RoundFactsRows result = new()
        {
            Schema = DemoCacheRecord.RoundFactsSchema
        };

        Dictionary<int, List<IReadOnlyDictionary<string, object?>>> byRound = new();
        foreach (IReadOnlyDictionary<string, object?> row in table.Rows)
        {
            if (RoundFactsValues.ToInt(row.GetValueOrDefault(RoundFactsColumns.RoundNumber)) is not int number)
            {
                result.Warnings.Add("a row carries no round_number and was dropped");
                continue;
            }

            if (!byRound.TryGetValue(number, out List<IReadOnlyDictionary<string, object?>>? rows))
            {
                rows = [];
                byRound[number] = rows;
            }

            rows.Add(row);
        }

        HashSet<int> derived = [.. rounds.Select(r => r.Number)];
        foreach (int number in byRound.Keys.Where(n => !derived.Contains(n)).Order())
        {
            result.Warnings.Add($"round {number}: rows with no derived round were dropped");
        }

        RoundFacts? previous = null;
        for (int i = 0; i < rounds.Count; i++)
        {
            ClipRound clip = rounds[i];
            if (!byRound.TryGetValue(clip.Number, out List<IReadOnlyDictionary<string, object?>>? rows))
            {
                result.Warnings.Add($"round {clip.Number}: no rows");
                continue;
            }

            RoundFacts round = ProjectRound(clip, i + 1 < rounds.Count, rows, table, thresholds, previous);
            result.Rounds.Add(round);
            previous = round;
        }

        return result;
    }

    /// <summary>The classifier parameters a ruleset declared, with the shipped defaults behind the rest.</summary>
    public static BuyThresholds ThresholdsFrom(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        BuyThresholds defaults = BuyThresholds.Default;
        return new BuyThresholds
        {
            EcoMaxPerPlayer = Param("eco_max_per_player", defaults.EcoMaxPerPlayer),
            FullMinPerPlayerCt = Param("full_min_per_player_ct", defaults.FullMinPerPlayerCt),
            FullMinPerPlayerT = Param("full_min_per_player_t", defaults.FullMinPerPlayerT),
            ForceMoneyMaxPerPlayer = Param("force_money_max_per_player", defaults.ForceMoneyMaxPerPlayer),
            MoneySaneMax = Param("money_sane_max", defaults.MoneySaneMax),
            RegulationRounds = Param("regulation_rounds", defaults.RegulationRounds)
        };

        int Param(string key, int fallback) =>
            RoundFactsValues.ToInt(parameters.GetValueOrDefault(key)) ?? fallback;
    }

    private static RoundFacts ProjectRound(
        ClipRound clip,
        bool hasNext,
        List<IReadOnlyDictionary<string, object?>> rows,
        RoundFactsTable table,
        BuyThresholds thresholds,
        RoundFacts? previous)
    {
        RoundFacts round = new()
        {
            Number = clip.Number,
            FreezeEndTick = clip.StartTickFrameClock
        };

        foreach (string column in RoundFactsColumns.Known)
        {
            round.Sources[column] = table.UnavailableColumns.Contains(column)
                ? RoundFactsSourceKind.Unavailable
                : RoundFactsSourceKind.Engine;
        }

        // Pair by side. Two rows per round is the contract; anything else lands with a warning.
        IReadOnlyDictionary<string, object?>? ctRow = null, tRow = null;
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            int? side = RoundFactsValues.ToInt(row.GetValueOrDefault(RoundFactsColumns.Side));
            switch (side)
            {
                case SideCt when ctRow is null:
                    ctRow = row;
                    break;
                case SideT when tRow is null:
                    tRow = row;
                    break;
                case SideCt or SideT:
                    round.ProjectionWarnings.Add($"a second row for side {side} was ignored");
                    break;
                default:
                    round.ProjectionWarnings.Add($"a row with side '{RoundFactsValues.ToText(row.GetValueOrDefault(RoundFactsColumns.Side)) ?? "null"}' was ignored");
                    break;
            }
        }

        if (ctRow is null && tRow is null)
        {
            round.ProjectionWarnings.Add("no row carries a side; the round holds only its derived bounds");
            round.IsLive = hasNext;
            return round;
        }

        if (ctRow is null || tRow is null)
        {
            round.ProjectionWarnings.Add($"only the {(ctRow is null ? "T" : "CT")} row is present");
        }

        // Round-level columns: read from whichever row has them, and say so when the pair disagrees.
        object? Cell(string column)
        {
            object? ct = ctRow?.GetValueOrDefault(column);
            object? t = tRow?.GetValueOrDefault(column);
            if (ct is not null && t is not null && !RoundFactsValues.SameValue(ct, t))
            {
                round.ProjectionWarnings.Add(
                    $"{column}: ct={RoundFactsValues.ToText(ct)} t={RoundFactsValues.ToText(t)}");
            }

            return ct ?? t;
        }

        int? Int(string column) => RoundFactsValues.ToInt(Cell(column));

        if (Int(RoundFactsColumns.FreezeEndTick) is int freezeEnd && freezeEnd != clip.StartTickFrameClock)
        {
            round.ProjectionWarnings.Add(
                $"freeze_end_tick {freezeEnd} disagrees with the derived round start {clip.StartTickFrameClock}");
        }

        if (Int(RoundFactsColumns.EndTick) is int endTick)
        {
            round.EndTick = endTick;
            round.EndSource = RoundEndSource.WinStatus;
        }
        else if (Int(RoundFactsColumns.OfficiallyEndedTick) is int officiallyEnded)
        {
            round.EndTick = officiallyEnded;
            round.EndSource = RoundEndSource.OfficiallyEndedEvent;
        }

        round.EndReason = Int(RoundFactsColumns.EndReason) is int reason && Enum.IsDefined((RoundEndReason)reason)
            ? (RoundEndReason)reason
            : RoundEndReason.Unknown;
        round.WinnerSide = Int(RoundFactsColumns.WinnerSide) is (SideT or SideCt) and int winner ? winner : 0;
        round.MatchRoundNumber = Int(RoundFactsColumns.MatchRound) is int played ? played + 1 : null;
        round.GamePhase = Int(RoundFactsColumns.GamePhase);
        round.Half = BuyTypeClassifier.HalfOf(round.MatchRoundNumber, thresholds.RegulationRounds);
        round.RoundTimeSeconds = Int(RoundFactsColumns.RoundTime);
        round.PlantTick = Int(RoundFactsColumns.PlantTick);
        round.PlantSite = ParseSite(RoundFactsValues.ToText(Cell(RoundFactsColumns.PlantSite)));
        round.PlantSiteEntity = Int(RoundFactsColumns.PlantSiteEntity);
        round.PlanterSlot = Int(RoundFactsColumns.PlanterSlot);
        round.DefuseTick = Int(RoundFactsColumns.DefuseTick);
        round.ExplodeTick = Int(RoundFactsColumns.ExplodeTick);
        round.OpeningKillTick = Int(RoundFactsColumns.OpeningKillTick);
        int? firstContact = Int(RoundFactsColumns.FirstContactTick);
        round.FirstContactTick = firstContact is int contact && round.OpeningKillTick is int opening
            ? Math.Min(contact, opening)
            : firstContact ?? round.OpeningKillTick;
        round.IsLive = round.EndTick is not null || hasNext;

        round.Kills = BuildKills(round, ctRow, tRow, Cell);

        round.Ct = ProjectSide(SideCt, ctRow, round, thresholds, previous);
        round.T = ProjectSide(SideT, tRow, round, thresholds, previous);

        // Unknown columns are the extension point. Equal on both rows (or on one) → one value; a
        // side-scoped user column keeps both under a side suffix.
        foreach (string column in (ctRow?.Keys ?? []).Concat(tRow?.Keys ?? []).Distinct(StringComparer.Ordinal))
        {
            if (RoundFactsColumns.Known.Contains(column))
            {
                continue;
            }

            bool inCt = ctRow?.ContainsKey(column) == true;
            bool inT = tRow?.ContainsKey(column) == true;
            object? ct = inCt ? ctRow![column] : null;
            object? t = inT ? tRow![column] : null;
            if (!inCt || !inT || RoundFactsValues.SameValue(ct, t))
            {
                round.Extra[column] = RoundFactsValues.Normalize(inCt ? ct : t);
            }
            else
            {
                round.Extra[$"{column}.ct"] = RoundFactsValues.Normalize(ct);
                round.Extra[$"{column}.t"] = RoundFactsValues.Normalize(t);
            }
        }

        return round;
    }

    // The four list columns share one index: kill i happened at kill_ticks[i], killed a player on
    // kill_victim_side[i], and left kill_team_alive[i] / kill_enemy_alive[i] standing as the row's side
    // sees it. The CT row's team is CT; the T row's team is T; either row alone still yields both counts.
    private static List<KillStep> BuildKills(
        RoundFacts round,
        IReadOnlyDictionary<string, object?>? ctRow,
        IReadOnlyDictionary<string, object?>? tRow,
        Func<string, object?> cell)
    {
        int[]? ticks = RoundFactsValues.ToIntList(cell(RoundFactsColumns.KillTicks));
        if (ticks is null)
        {
            return [];
        }

        int[]? victimSides = RoundFactsValues.ToIntList(cell(RoundFactsColumns.KillVictimSide));
        int[]? victimSlots = RoundFactsValues.ToIntList(cell(RoundFactsColumns.KillVictimSlot));
        int[]? attackerSlots = RoundFactsValues.ToIntList(cell(RoundFactsColumns.KillAttackerSlot));

        int[]? ctAlive, tAlive;
        if (ctRow is not null)
        {
            ctAlive = RoundFactsValues.ToIntList(ctRow.GetValueOrDefault(RoundFactsColumns.KillTeamAlive));
            tAlive = RoundFactsValues.ToIntList(ctRow.GetValueOrDefault(RoundFactsColumns.KillEnemyAlive));
        }
        else
        {
            IReadOnlyDictionary<string, object?> row = tRow!;
            tAlive = RoundFactsValues.ToIntList(row.GetValueOrDefault(RoundFactsColumns.KillTeamAlive));
            ctAlive = RoundFactsValues.ToIntList(row.GetValueOrDefault(RoundFactsColumns.KillEnemyAlive));
        }

        int count = ticks.Length;
        foreach ((string name, int[]? list) in new[]
                 {
                     (RoundFactsColumns.KillVictimSide, victimSides),
                     (RoundFactsColumns.KillTeamAlive, ctAlive),
                     (RoundFactsColumns.KillEnemyAlive, tAlive)
                 })
        {
            if (list is not null && list.Length != ticks.Length)
            {
                round.ProjectionWarnings.Add($"{name} has {list.Length} entries for {ticks.Length} kills");
                count = Math.Min(count, list.Length);
            }
        }

        List<KillStep> kills = new(count);
        for (int i = 0; i < count; i++)
        {
            kills.Add(new KillStep
            {
                Tick = ticks[i],
                VictimSide = victimSides is not null ? victimSides[i] : 0,
                VictimSlot = victimSlots is not null && i < victimSlots.Length ? victimSlots[i] : -1,
                AttackerSlot = attackerSlots is not null && i < attackerSlots.Length ? attackerSlots[i] : -1,
                CtAlive = ctAlive is not null ? ctAlive[i] : 0,
                TAlive = tAlive is not null ? tAlive[i] : 0
            });
        }

        return kills;
    }

    private static SideFacts ProjectSide(
        int side,
        IReadOnlyDictionary<string, object?>? row,
        RoundFacts round,
        BuyThresholds thresholds,
        RoundFacts? previous)
    {
        SideFacts facts = new()
        {
            Side = side,
            Thresholds = thresholds
        };

        if (row is null)
        {
            return facts;
        }

        facts.Slots = RoundFactsValues.ToIntList(row.GetValueOrDefault(RoundFactsColumns.Slots)) ?? [];
        facts.PlayersAtFreezeEnd =
            RoundFactsValues.ToInt(row.GetValueOrDefault(RoundFactsColumns.Players)) ?? facts.Slots.Length;
        facts.ScoreBefore = RoundFactsValues.ToInt(row.GetValueOrDefault(RoundFactsColumns.ScoreBefore)) ?? 0;
        facts.EquipmentFreezeEnd =
            RoundFactsValues.ToInt(row.GetValueOrDefault(RoundFactsColumns.Equipment)) ?? 0;

        // The ruleset says whether every account was plausible when it can see them; the sum alone can
        // only be checked against the ceiling scaled by the head count.
        int? money = RoundFactsValues.ToInt(row.GetValueOrDefault(RoundFactsColumns.Money));
        bool reliable = RoundFactsValues.ToBool(row.GetValueOrDefault(RoundFactsColumns.MoneyReliable))
                        ?? (money is int m && m >= 0 && m <= thresholds.MoneySaneMax * Math.Max(facts.PlayersAtFreezeEnd, 1));
        facts.MoneyReliable = money is not null && reliable;
        facts.MoneyAtFreezeEnd = facts.MoneyReliable ? money : null;

        int previousWinner = previous?.WinnerSide ?? 0;
        facts.WonPreviousRound = previousWinner == side;
        bool lostPrevious = previousWinner is SideT or SideCt && previousWinner != side;

        string? buyText = RoundFactsValues.ToText(row.GetValueOrDefault(RoundFactsColumns.BuyType));
        facts.BuyType = buyText is not null && Enum.TryParse(buyText, true, out BuyType declared)
            ? declared
            : BuyTypeClassifier.Classify(side, facts.PlayersAtFreezeEnd, facts.EquipmentFreezeEnd,
                money, facts.MoneyReliable, lostPrevious, round.MatchRoundNumber, thresholds);

        return facts;
    }

    private static BombSite ParseSite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BombSite.Unknown;
        }

        string site = text.Trim();
        if (site.StartsWith("Bombsite", StringComparison.OrdinalIgnoreCase))
        {
            site = site["Bombsite".Length..];
        }

        return site.ToUpperInvariant() switch
        {
            "A" => BombSite.A,
            "B" => BombSite.B,
            _ => BombSite.Unknown
        };
    }
}
