#region

using DemoViewer.NET.Controls.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>Analyst-facing column group: canonical board order.</summary>
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
    Other
}

/// <summary>
///     Flat emphasis accents for intrinsically good/bad columns. Superseded by <see cref="StatScaleSpec" />
///     wherever a column declares one: a column with a scale ignores its <see cref="Emphasis" />, so the
///     two never paint the same cell twice.
/// </summary>
public enum Emphasis
{
    None,
    Positive,
    Negative
}

/// <summary>Where a column's bar domain comes from.</summary>
public enum StatDomain
{
    /// <summary>No bar and no ramp: the column keeps its flat <see cref="Emphasis" />.</summary>
    None,

    /// <summary>Bar spans the values on screen, so it reads as "who topped this server".</summary>
    Peer,

    /// <summary>Bar spans a fixed domain that belongs to the metric, not the lobby.</summary>
    Absolute
}

/// <summary>
///     How a column wants to be scaled, minus the numbers that can only be known per rebuild.
///     <para>
///         <b>This is a SHAPE, not a scale.</b> <see cref="ColumnMeta" /> instances are shared singletons
///         handed to the match table, the round table, the details overlay and the totals row, so a
///         concrete peer min/max cannot live here without one view's domain leaking into another. The
///         concrete <see cref="StatScale" /> is produced per rebuild by <see cref="Resolve" /> and rides
///         on the cell.
///     </para>
///     <para>
///         <b>Bar and colour are separate on purpose.</b> Leaving <see cref="ColourMin" />/
///         <see cref="ColourMax" /> null gives a peer-relative tint; setting them pins the tint to a fixed
///         benchmark while the bar still compares the players on screen. See docs/ui/stats-components.md.
///     </para>
/// </summary>
/// <param name="Domain">Where the bar's extent comes from.</param>
/// <param name="Polarity">Which direction is good. <see cref="StatPolarity.Neutral" /> means bar, no tint.</param>
/// <param name="PeerDeadZone">
///     Fraction of the observed peer range left untinted, centred. Only used when the colour is
///     peer-relative. Defaults to half: without a dead zone a ten-player column tints five of them, which
///     is the heat-map failure the ramp exists to avoid.
/// </param>
/// <param name="ColourMin">Value at which the tint saturates bad. Null means "use the peer minimum".</param>
/// <param name="ColourMax">Value at which the tint saturates good. Null means "use the peer maximum".</param>
/// <param name="NeutralLow">Lower edge of the untinted band, in raw units. Absolute-colour columns only.</param>
/// <param name="NeutralHigh">Upper edge of the untinted band, in raw units.</param>
/// <param name="ColourGateColumns">
///     Columns whose values are summed to decide whether this cell has earned a tint at all. The
///     opening-duel win rate is the case this exists for: it is pinned at exactly 50% by definition, so a
///     lurker who wins two duels of two reads 100% and means nothing.
/// </param>
/// <param name="ColourGateMinimum">The summed gate value a row must reach before its cell is tinted.</param>
public sealed record StatScaleSpec(
    StatDomain Domain,
    StatPolarity Polarity = StatPolarity.HigherIsBetter,
    double PeerDeadZone = 0.5,
    double? ColourMin = null,
    double? ColourMax = null,
    double? NeutralLow = null,
    double? NeutralHigh = null,
    IReadOnlyList<string>? ColourGateColumns = null,
    double ColourGateMinimum = 0)
{
    /// <summary>
    ///     Turns the shape into a concrete scale for one rebuild. <paramref name="peers" /> is every
    ///     finite value of this column currently on screen, excluding totals rows.
    ///     <para>
    ///         Returns null when there is nothing to draw, which the cell renders as plain text. A peer
    ///         column whose values are all equal still returns a scale if its colour is absolute: the bar
    ///         collapses on its own and the benchmark still has an opinion.
    ///     </para>
    /// </summary>
    public StatScale? Resolve(IReadOnlyList<double> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        if (Domain == StatDomain.None)
        {
            return null;
        }

        if (Domain == StatDomain.Absolute)
        {
            return ColourMin is { } aMin && ColourMax is { } aMax && aMax > aMin
                ? new StatScale(aMin, aMax, Polarity, NeutralLow, NeutralHigh)
                : null;
        }

        StatScale? bar = StatScale.FromPeers(peers, Polarity);

        // Absolute colour: the bar may be degenerate and the tint still stands.
        if (ColourMin is { } cMin && ColourMax is { } cMax && cMax > cMin)
        {
            return new StatScale(bar?.Min ?? 0, bar?.Max ?? 0, Polarity, NeutralLow, NeutralHigh,
                cMin, cMax);
        }

        // Peer colour: no peers, nothing to say.
        if (bar is null)
        {
            return null;
        }

        // The dead zone is expressed as a fraction of the observed range because the range is not known
        // until here. Zero width would tint half the column.
        double dz = Math.Clamp(PeerDeadZone, 0, 1);
        double span = bar.Max - bar.Min;
        double low = bar.Min + ((0.5 - (dz / 2)) * span);
        double high = bar.Min + ((0.5 + (dz / 2)) * span);
        return bar with { NeutralLow = low, NeutralHigh = high };
    }
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
///     (the <c>MetricTable.ValueColumns</c> entry: the export/golden parity key) and is NEVER
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
    double Width = 60,
    StatScaleSpec? Scale = null);

/// <summary>
///     The app-side single source of truth for how shipped stat columns present (display name,
///     group, canonical order, alignment, totals aggregation, tooltip): the design notes in git history
///     the design notes in git history explain why this cannot come from the YAML <c>group:</c> field (dropped in
///     projection;
///     shipped taxonomy is only game/round). Unknown labels (user-authored columns) fall through to
///     <see cref="StatGroup.Other" /> with numeric defaults.
/// </summary>
public static class ColumnCatalogue
{
    // ORDERING IS LOAD-BEARING: these are declared BEFORE _byKey because C# runs static field
    // initializers in declaration order, and _byKey's initializer calls BuildCatalogue(), which reads
    // them. Declared after, every one of them is still null when the catalogue is built, every column
    // silently gets no scale, and the board renders exactly as if the feature were never wired up.
    // ── Scale shapes ──────────────────────────────────────────────────────────
    //
    // Which columns get a scale, and on what evidence, is recorded in docs/ui/stats-components.md.
    // The short version: a metric whose population mean is PINNED by the structure of the game (every
    // kill is one death, every opening duel has one winner) can carry an absolute band, because the
    // anchor never drifts with lobby skill. A per-opportunity efficiency metric cannot, because it
    // conflates the player's skill with the lobby's.
    //
    // Three columns are deliberately given a bar and NO tint. That is not an oversight:
    //   HS%    Leetify structurally excludes AWP shots from their headshot metric, and the measured
    //          correlation with actual production is weak (R = 0.30). Nobody publishes a rank-segmented
    //          distribution, so any cut point here would be invented.
    //   Surv%  High survival is anti-correlated with aggression. High survival + low ADR is passivity;
    //          low survival + high ADR is a healthy entry fragger. One band cannot say that.
    //   FK+/-  A signed differential whose sign is already the whole story; the bar carries it.

    /// <summary>More is better, tint only the outliers. The default for a plain count column.</summary>
    private static readonly StatScaleSpec _peerUp = new(StatDomain.Peer);

    /// <summary>Less is better (deaths, opening deaths), tint only the outliers.</summary>
    private static readonly StatScaleSpec _peerDown = new(StatDomain.Peer, StatPolarity.LowerIsBetter);

    /// <summary>Ranked by a bar, never judged by colour. See the note above.</summary>
    private static readonly StatScaleSpec _peerNoTint = new(StatDomain.Peer, StatPolarity.Neutral);

    /// <summary>Anything above zero is bad and nothing is ever good: team damage, self damage.</summary>
    private static readonly StatScaleSpec _penalty =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter, NeutralLow: 0, NeutralHigh: 0,
            ColourMin: 0, ColourMax: 100);

    // Absolute colour bands. Sources and confidence per metric are in the design doc; the extents are
    // set wider than the bands so the tint has room to ramp rather than saturating at the first step.

    /// <summary>HLTV 2.0. NOTE: 1.00 is anchored to PRO play, and the CS2 MR12 average drifted to ~1.06.</summary>
    private static readonly StatScaleSpec _hltv =
        new(StatDomain.Peer, ColourMin: 0.40, ColourMax: 1.80, NeutralLow: 0.95, NeutralHigh: 1.05);

    /// <summary>ADR. Centred on the arithmetic floor (~72-80), not the "60-75" every guide repeats.</summary>
    private static readonly StatScaleSpec _adr =
        new(StatDomain.Peer, ColourMin: 40, ColourMax: 120, NeutralLow: 70, NeutralHigh: 82);

    private static readonly StatScaleSpec _kast =
        new(StatDomain.Peer, ColourMin: 40, ColourMax: 95, NeutralLow: 65, NeutralHigh: 73);

    private static readonly StatScaleSpec _kd =
        new(StatDomain.Peer, ColourMin: 0.20, ColourMax: 2.20, NeutralLow: 0.92, NeutralHigh: 1.10);

    private static readonly StatScaleSpec _kpr =
        new(StatDomain.Peer, ColourMin: 0.20, ColourMax: 1.20, NeutralLow: 0.60, NeutralHigh: 0.72);

    /// <summary>Rank-dependent, so the band is softer than the pinned metrics above.</summary>
    private static readonly StatScaleSpec _blind =
        new(StatDomain.Peer, ColourMin: 1.0, ColourMax: 4.0, NeutralLow: 2.25, NeutralHigh: 2.70);

    /// <summary>
    ///     Opening-duel win rate. Pinned at exactly 50% by definition, so it is meaningless below a
    ///     volume of attempts: two duels won of two reads 100%. Gated on the player's own duel count.
    /// </summary>
    private static readonly StatScaleSpec _duelWin =
        new(StatDomain.Peer, ColourMin: 20, ColourMax: 80, NeutralLow: 47, NeutralHigh: 53,
            ColourGateColumns: ["TotalFK", "TotalFD"], ColourGateMinimum: 8);

    private static int _seq;

    // Declared in canonical board order (the _seq counter IS the order).
    private static readonly Dictionary<string, ColumnMeta> _byKey = BuildCatalogue();

    private static ColumnMeta M(string key, string display, StatGroup group, string tooltip,
        Emphasis emphasis = Emphasis.None, ColumnAggregate agg = ColumnAggregate.Sum, double width = 60,
        StatScaleSpec? scale = null) =>
        new(key, display, group, _seq++, true, emphasis, agg, tooltip, width, scale);

    private static Dictionary<string, ColumnMeta> BuildCatalogue()
    {
        _seq = 0;
        ColumnMeta[] metas =
        [
            // ── Core (the frozen-left headline block) ──
            M("TotalK", "K", StatGroup.Core, "Kills (enemy kills over the whole match)", scale: _peerUp),
            M("TotalD", "D", StatGroup.Core, "Deaths", scale: _peerDown),
            M("TotalA", "A", StatGroup.Core, "Assists (on enemy kills)", scale: _peerUp),
            M("ADR", "ADR", StatGroup.Core, "Average damage per round (damage capped at remaining HP)", agg: ColumnAggregate.Average, width: 66, scale: _adr),
            M("KAST%", "KAST%", StatGroup.Core, "Rounds with a Kill, Assist, Survival or Traded death (%)", agg: ColumnAggregate.Average, width: 70, scale: _kast),
            M("HLTV", "Rating", StatGroup.Core, "HLTV 2.0-style rating (composite of KPR, DPR, ADR, KAST and impact)", agg: ColumnAggregate.Average, width: 66, scale: _hltv),

            // ── Rating detail ──
            M("KD", "K/D", StatGroup.Rating, "Kill/death ratio", agg: ColumnAggregate.Average, scale: _kd),
            M("KPR", "KPR", StatGroup.Rating, "Kills per round", agg: ColumnAggregate.Average, scale: _kpr),
            M("HS%", "HS %", StatGroup.Rating, "Headshot kill percentage", agg: ColumnAggregate.Average, scale: _peerNoTint),
            M("Surv%", "Survival %", StatGroup.Rating, "Rounds survived (%)", agg: ColumnAggregate.Average, width: 76, scale: _peerNoTint),

            // ── Combat ──
            M("TotalHS", "HS Kills", StatGroup.Combat, "Headshot kills", width: 68),
            M("FlashAst", "Flash Assists", StatGroup.Combat, "Kills on enemies you flashed", width: 92),
            M("TrdK", "Trade Kills", StatGroup.Combat, "Kills avenging a teammate within the trade window", width: 84),
            M("TradedD", "Traded Deaths", StatGroup.Combat, "Your deaths avenged by a teammate within the trade window", width: 98),
            M("Clutch", "Clutches Won", StatGroup.Combat, "1-vs-X situations won", Emphasis.Positive, width: 96),
            M("RapidKills", "Rapid Kills", StatGroup.Combat, "Kill streaks within a 10-second window", width: 82),

            // ── Damage ──
            M("EnemyDmg", "Enemy Dmg", StatGroup.Damage, "Total damage dealt to enemies (HP-capped)", width: 84, scale: _peerUp),
            M("TeamDmg", "Team Dmg", StatGroup.Damage, "Damage dealt to teammates", Emphasis.Negative, width: 78, scale: _penalty),
            M("SelfDmg", "Self Dmg", StatGroup.Damage, "Self-inflicted damage", Emphasis.Negative, width: 72, scale: _penalty),
            M("HitFoe", "Hits (foe)", StatGroup.Damage, "Shots that hit an enemy", width: 76),
            M("HitTeam", "Hits (team)", StatGroup.Damage, "Shots that hit a teammate", Emphasis.Negative, width: 84),
            M("Shots", "Shots Fired", StatGroup.Damage, "Total shots fired", width: 84),
            M("AvgHP→Dmg", "Avg HP @ Dmg", StatGroup.Damage, "Your average health when dealing damage", agg: ColumnAggregate.Average, width: 98),

            // ── Opening duels ──
            M("TotalFK", "Opening K", StatGroup.OpeningDuels, "Opening (first) kills of a round", Emphasis.Positive, width: 80, scale: _peerUp),
            M("TotalFD", "Opening D", StatGroup.OpeningDuels, "Opening (first) deaths of a round", Emphasis.Negative, width: 80, scale: _peerDown),
            M("FK±", "Opening +/-", StatGroup.OpeningDuels, "Opening kills minus opening deaths", width: 88, scale: _peerNoTint),
            M("Duel%", "Duel Win %", StatGroup.OpeningDuels, "Share of opening duels won (%)", agg: ColumnAggregate.Average, width: 84, scale: _duelWin),
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
            M("EFlash", "Enemies Flashed", StatGroup.Utility, "Enemies blinded by your flashes", width: 110, scale: _peerUp),
            M("AvgBlind", "Avg Blind (s)", StatGroup.Utility, "Average enemy blind duration per flash (seconds)", agg: ColumnAggregate.Average, width: 92, scale: _blind),

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
            // Folded into Rating, next to Surv%. A category that can only ever hold one column is a
            // tab that costs a click to show a single number.
            M("Survived", "Rounds Survived", StatGroup.Rating, "Rounds survived", width: 110,
                scale: _peerUp),

            // ── Rounds view (group:round columns), same treatment, kast.yaml labels ──
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
    ///     Whether a group describes the PLAYER and so earns a page of its own.
    ///     <para>
    ///         <see cref="StatGroup.RoundWins" /> does not. CTW/CTL/TW/TL are properties of the TEAM,
    ///         replicated onto every one of its player rows, so a per-player page of them shows five
    ///         identical rows and invites a comparison that cannot exist. The columns stay in the
    ///         catalogue because the engine emits them, the export carries them, and the team score is
    ///         derived from them; they just are not a page. The score they add up to is already on the
    ///         team badge.
    ///     </para>
    /// </summary>
    public static bool IsPlayerFacing(StatGroup group) => group != StatGroup.RoundWins;

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
