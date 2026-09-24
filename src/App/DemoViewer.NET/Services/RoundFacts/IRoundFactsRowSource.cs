#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The seam between the engine and the record: whatever evaluates the <c>round_facts</c> ruleset
///     hands back its table here, two rows per round, and <see cref="RoundFactsProjection" /> does the
///     rest. Synthetic tables in tests and the engine's real output land on the same projection.
/// </summary>
public interface IRoundFactsRowSource
{
    /// <summary>The <c>round_facts</c> table for a held parse. Never null; a source with nothing says so in the table.</summary>
    RoundFactsTable Rows(ParsedDemo parsed);
}

/// <summary>
///     A per-round two-row table as the ruleset emits it: one dictionary per row keyed by column, the
///     columns the engine could not provide, the parameter values in force, and anything the source
///     wants logged.
/// </summary>
/// <param name="Rows">Every row, keyed by column name. Values are scalars or lists of scalars.</param>
/// <param name="UnavailableColumns">
///     Known columns the engine did not provide (a partial CS2DemoKit #54). A row's null in one of
///     these means "not provided", not "the demo did not say".
/// </param>
/// <param name="Parameters">The ruleset's <c>params:</c> values, keyed as the YAML spells them.</param>
/// <param name="Diagnostics">What the source wants a log line for.</param>
public sealed record RoundFactsTable(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlySet<string> UnavailableColumns,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>No rows, every known column unavailable, one diagnostic saying why.</summary>
    public static RoundFactsTable Unavailable(string diagnostic) => new(
        [],
        RoundFactsColumns.Known,
        new Dictionary<string, object?>(StringComparer.Ordinal),
        [diagnostic]);
}

/// <summary>
///     The column names the projection knows, as the <c>round_facts</c> ruleset's <c>stats:</c> spell
///     them. Anything else in a row lands in <see cref="RoundFacts.Extra" />.
/// </summary>
public static class RoundFactsColumns
{
    public const string RoundNumber = "round_number";
    public const string Side = "side";
    public const string FreezeEndTick = "freeze_end_tick";
    public const string EndTick = "end_tick";
    public const string OfficiallyEndedTick = "officially_ended_tick";
    public const string EndReason = "end_reason";
    public const string WinnerSide = "winner_side";
    public const string MatchRound = "match_round";
    public const string GamePhase = "game_phase";
    public const string RoundTime = "round_time";
    public const string PlantTick = "plant_tick";
    public const string PlantSite = "plant_site";
    public const string PlantSiteEntity = "plant_site_entity";
    public const string PlanterSlot = "planter_slot";
    public const string DefuseTick = "defuse_tick";
    public const string ExplodeTick = "explode_tick";
    public const string FirstContactTick = "first_contact_tick";
    public const string OpeningKillTick = "opening_kill_tick";
    public const string Slots = "slots";
    public const string Players = "players";
    public const string ScoreBefore = "score_before";
    public const string Equipment = "equipment";
    public const string Money = "money";
    public const string MoneyReliable = "money_reliable";
    public const string BuyType = "buy_type";
    public const string KillTicks = "kill_ticks";
    public const string KillVictimSide = "kill_victim_side";
    public const string KillTeamAlive = "kill_team_alive";
    public const string KillEnemyAlive = "kill_enemy_alive";
    public const string KillVictimSlot = "kill_victim_slot";
    public const string KillAttackerSlot = "kill_attacker_slot";

    /// <summary>Columns whose value is the round's, so the two rows of a pair must agree.</summary>
    public static IReadOnlySet<string> RoundLevel { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RoundNumber, FreezeEndTick, EndTick, OfficiallyEndedTick, EndReason, WinnerSide, MatchRound,
        GamePhase, RoundTime, PlantTick, PlantSite, PlantSiteEntity, PlanterSlot, DefuseTick, ExplodeTick,
        FirstContactTick, OpeningKillTick, KillTicks, KillVictimSide, KillVictimSlot, KillAttackerSlot
    };

    /// <summary>Columns whose value belongs to the row's side.</summary>
    public static IReadOnlySet<string> SideLevel { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Side, Slots, Players, ScoreBefore, Equipment, Money, MoneyReliable, BuyType, KillTeamAlive,
        KillEnemyAlive
    };

    /// <summary>Every column the projection maps onto a typed field.</summary>
    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(RoundLevel.Concat(SideLevel), StringComparer.Ordinal);
}
