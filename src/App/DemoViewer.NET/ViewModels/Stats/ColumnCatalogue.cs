namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>Analyst-facing column group — canonical board order.</summary>
public enum StatGroup
{
    Core,
    Rating,
    Combat,
    Damage,
    OpeningDuels,
    Weapons,
    SpecialKills,
    Utility,
    Objectives,
    Economy,
    MultiKill,
    RoundWins,
    Survival,
    Other
}

/// <summary>Flat emphasis accents for intrinsically good/bad columns (heat scales are Phase C).</summary>
public enum Emphasis
{
    None,
    Positive,
    Negative
}

/// <summary>How a column contributes to a team totals row.</summary>
public enum ColumnAggregate
{
    Sum,
    Average,
    None
}

/// <summary>
///     View-only presentation metadata for one stat column. <see cref="Key" /> is the engine label
///     (the <c>MetricTable.ValueColumns</c> entry — the export/golden parity key) and is NEVER
///     renamed; everything else is display.
/// </summary>
public sealed record ColumnMeta(
    string Key,
    string Display,
    StatGroup Group,
    int Order,
    bool Numeric,
    Emphasis Emphasis,
    ColumnAggregate Aggregate,
    string Tooltip,
    double Width = 60);

/// <summary>
///     The app-side single source of truth for how shipped stat columns present (display name,
///     group, canonical order, alignment, totals aggregation, tooltip) — the design notes in git history
///     the design notes in git history explain why this cannot come from the YAML <c>group:</c> field (dropped in
///     projection;
///     shipped taxonomy is only game/round). Unknown labels (user-authored columns) fall through to
///     <see cref="StatGroup.Other" /> with numeric defaults.
/// </summary>
public static class ColumnCatalogue
{
    private static int _seq;

    // Declared in canonical board order (the _seq counter IS the order).
    private static readonly Dictionary<string, ColumnMeta> _byKey = BuildCatalogue();

    private static ColumnMeta M(string key, string display, StatGroup group, string tooltip,
        Emphasis emphasis = Emphasis.None, ColumnAggregate agg = ColumnAggregate.Sum, double width = 60) =>
        new(key, display, group, _seq++, true, emphasis, agg, tooltip, width);

    private static Dictionary<string, ColumnMeta> BuildCatalogue()
    {
        _seq = 0;
        ColumnMeta[] metas =
        [
            // ── Core (the frozen-left headline block) ──
            M("TotalK", "K", StatGroup.Core, "Kills (enemy kills over the whole match)"),
            M("TotalD", "D", StatGroup.Core, "Deaths"),
            M("TotalA", "A", StatGroup.Core, "Assists (on enemy kills)"),
            M("ADR", "ADR", StatGroup.Core, "Average damage per round (damage capped at remaining HP)", agg: ColumnAggregate.Average, width: 66),
            M("KAST%", "KAST%", StatGroup.Core, "Rounds with a Kill, Assist, Survival or Traded death (%)", agg: ColumnAggregate.Average, width: 70),
            M("HLTV", "Rating", StatGroup.Core, "HLTV 2.0-style rating (composite of KPR, DPR, ADR, KAST and impact)", agg: ColumnAggregate.Average, width: 66),

            // ── Rating detail ──
            M("KD", "K/D", StatGroup.Rating, "Kill/death ratio", agg: ColumnAggregate.Average),
            M("KPR", "KPR", StatGroup.Rating, "Kills per round", agg: ColumnAggregate.Average),
            M("HS%", "HS %", StatGroup.Rating, "Headshot kill percentage", agg: ColumnAggregate.Average),
            M("Surv%", "Survival %", StatGroup.Rating, "Rounds survived (%)", agg: ColumnAggregate.Average, width: 76),

            // ── Combat ──
            M("TotalHS", "HS Kills", StatGroup.Combat, "Headshot kills", width: 68),
            M("FlashAst", "Flash Assists", StatGroup.Combat, "Kills on enemies you flashed", width: 92),
            M("TrdK", "Trade Kills", StatGroup.Combat, "Kills avenging a teammate within the trade window", width: 84),
            M("TradedD", "Traded Deaths", StatGroup.Combat, "Your deaths avenged by a teammate within the trade window", width: 98),
            M("Clutch", "Clutches Won", StatGroup.Combat, "1-vs-X situations won", Emphasis.Positive, width: 96),
            M("RapidKills", "Rapid Kills", StatGroup.Combat, "Kill streaks within a 10-second window", width: 82),

            // ── Damage ──
            M("EnemyDmg", "Enemy Dmg", StatGroup.Damage, "Total damage dealt to enemies (HP-capped)", width: 84),
            M("TeamDmg", "Team Dmg", StatGroup.Damage, "Damage dealt to teammates", Emphasis.Negative, width: 78),
            M("SelfDmg", "Self Dmg", StatGroup.Damage, "Self-inflicted damage", Emphasis.Negative, width: 72),
            M("HitFoe", "Hits (foe)", StatGroup.Damage, "Shots that hit an enemy", width: 76),
            M("HitTeam", "Hits (team)", StatGroup.Damage, "Shots that hit a teammate", Emphasis.Negative, width: 84),
            M("Shots", "Shots Fired", StatGroup.Damage, "Total shots fired", width: 84),
            M("AvgHP→Dmg", "Avg HP @ Dmg", StatGroup.Damage, "Your average health when dealing damage", agg: ColumnAggregate.Average, width: 98),

            // ── Opening duels ──
            M("TotalFK", "Opening K", StatGroup.OpeningDuels, "Opening (first) kills of a round", Emphasis.Positive, width: 80),
            M("TotalFD", "Opening D", StatGroup.OpeningDuels, "Opening (first) deaths of a round", Emphasis.Negative, width: 80),
            M("FK±", "Opening +/-", StatGroup.OpeningDuels, "Opening kills minus opening deaths", width: 88),
            M("Duel%", "Duel Win %", StatGroup.OpeningDuels, "Share of opening duels won (%)", agg: ColumnAggregate.Average, width: 84),
            M("CTFK", "CT Open K", StatGroup.OpeningDuels, "Opening kills on the CT side", width: 78),
            M("CTFD", "CT Open D", StatGroup.OpeningDuels, "Opening deaths on the CT side", width: 78),
            M("TFK", "T Open K", StatGroup.OpeningDuels, "Opening kills on the T side", width: 72),
            M("TFD", "T Open D", StatGroup.OpeningDuels, "Opening deaths on the T side", width: 72),

            // ── Weapon classes ──
            M("AWP", "AWP Kills", StatGroup.Weapons, "Kills with the AWP", width: 76),
            M("Pistol", "Pistol Kills", StatGroup.Weapons, "Kills with pistols", width: 86),
            M("Rifle", "Rifle Kills", StatGroup.Weapons, "Kills with rifles", width: 78),
            M("SMG", "SMG Kills", StatGroup.Weapons, "Kills with SMGs", width: 76),
            M("Knife", "Knife Kills", StatGroup.Weapons, "Kills with the knife", width: 80),
            M("DeagleHSRnds", "Deagle-HS Rounds", StatGroup.Weapons, "Rounds with 2+ Desert Eagle headshot kills", width: 118),

            // ── Special kills ──
            M("NoScope", "No-scopes", StatGroup.SpecialKills, "Sniper kills without scoping", width: 80),
            M("WB", "Wallbangs", StatGroup.SpecialKills, "Kills through penetrable surfaces", width: 80),
            M("Smoke", "Smoke Kills", StatGroup.SpecialKills, "Kills through smoke", width: 86),
            M("Blind", "Blind Kills", StatGroup.SpecialKills, "Kills while flashed", width: 80),
            M("Revenge", "Revenge Kills", StatGroup.SpecialKills, "Kills on the enemy who last killed you", width: 96),
            M("FlashK", "Flash Kills", StatGroup.SpecialKills, "Kills on flashed enemies", width: 82),

            // ── Utility ──
            M("HE", "HE Thrown", StatGroup.Utility, "HE grenades thrown", width: 78),
            M("Flash", "Flashes", StatGroup.Utility, "Flashbangs thrown", width: 66),
            M("Smokes", "Smokes", StatGroup.Utility, "Smoke grenades thrown", width: 64),
            M("Molly", "Molotovs", StatGroup.Utility, "Molotovs / incendiaries thrown", width: 72),
            M("EFlash", "Enemies Flashed", StatGroup.Utility, "Enemies blinded by your flashes", width: 110),
            M("AvgBlind", "Avg Blind (s)", StatGroup.Utility, "Average enemy blind duration per flash (seconds)", agg: ColumnAggregate.Average, width: 92),

            // ── Objectives ──
            M("Plants", "Plants", StatGroup.Objectives, "Bomb plants", width: 58),
            M("Defuses", "Defuses", StatGroup.Objectives, "Bomb defuses", width: 66),

            // ── Economy ──
            M("Equip", "Avg Equip $", StatGroup.Economy, "Average round-start equipment value", agg: ColumnAggregate.Average, width: 88),
            M("Armor", "Armor Rounds", StatGroup.Economy, "Rounds with armor at round start", width: 96),

            // ── Multi-kill rounds ──
            M("2K", "2K", StatGroup.MultiKill, "Rounds with exactly 2 kills", width: 48),
            M("3K", "3K", StatGroup.MultiKill, "Rounds with exactly 3 kills", Emphasis.Positive, width: 48),
            M("4K", "4K", StatGroup.MultiKill, "Rounds with exactly 4 kills", Emphasis.Positive, width: 48),
            M("5K", "Ace", StatGroup.MultiKill, "Rounds with 5 kills (ace)", Emphasis.Positive, width: 48),

            // ── Round wins / survival ──
            M("CTW", "CT Wins", StatGroup.RoundWins, "Rounds won on the CT side", width: 66),
            M("CTL", "CT Losses", StatGroup.RoundWins, "Rounds lost on the CT side", width: 78),
            M("TW", "T Wins", StatGroup.RoundWins, "Rounds won on the T side", width: 60),
            M("TL", "T Losses", StatGroup.RoundWins, "Rounds lost on the T side", width: 70),
            M("Survived", "Rounds Survived", StatGroup.Survival, "Rounds survived", width: 110),

            // ── Rounds view (group:round columns) — same treatment, kast.yaml labels ──
            M("Kills", "K", StatGroup.Core, "Kills this round"),
            M("Deaths", "D", StatGroup.Core, "Died this round"),
            M("Assists", "A", StatGroup.Core, "Assists this round"),
            M("Damage", "Dmg", StatGroup.Damage, "Damage dealt this round (HP-capped)", width: 62),
            M("UtilDmg", "Util Dmg", StatGroup.Damage, "Utility damage this round", width: 72),
            M("Traded", "Traded", StatGroup.Combat, "Death was traded by a teammate this round", width: 60),
            M("HasKAST", "KAST", StatGroup.Core, "Kill, Assist, Survival or Traded death this round", width: 52),
            M("HSKills", "HS", StatGroup.Combat, "Headshot kills this round", width: 46),
            M("EKills", "Enemy K", StatGroup.Combat, "Enemy kills this round", width: 68),
            M("Flashed", "Enemies Flashed", StatGroup.Utility, "Enemies flashed this round", width: 110),
            M("FK", "Opening K", StatGroup.OpeningDuels, "Opening kill this round", width: 80),
            M("FD", "Opening D", StatGroup.OpeningDuels, "Opening death this round", width: 80),
            M("DeagleHS", "Deagle HS", StatGroup.Weapons, "Desert Eagle headshot kills this round", width: 78)
        ];

        Dictionary<string, ColumnMeta> byKey = new(StringComparer.Ordinal);
        foreach (ColumnMeta meta in metas)
        {
            byKey[meta.Key] = meta;
        }

        return byKey;
    }

    /// <summary>
    ///     Resolves presentation metadata for an engine column label. Unknown labels (user-authored
    ///     columns) get <see cref="StatGroup.Other" />, the label as display name, numeric defaults,
    ///     and sort after every catalogued column.
    /// </summary>
    public static ColumnMeta Resolve(string key) =>
        _byKey.TryGetValue(key, out ColumnMeta? meta)
            ? meta
            : new ColumnMeta(key, key, StatGroup.Other, int.MaxValue, true,
                Emphasis.None, ColumnAggregate.None, key, Math.Max(60, key.Length * 8 + 16));
}
