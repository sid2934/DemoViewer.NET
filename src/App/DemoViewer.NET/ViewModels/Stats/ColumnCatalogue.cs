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

    /// <summary>
    ///     Hit rates from rules/aim_rating.rules.yaml: how often a bullet found its target. Sits after
    ///     Combat because the ORDINAL is the chip rail's order, and these columns explain the Combat
    ///     numbers rather than adding to them.
    /// </summary>
    Accuracy,

    /// <summary>
    ///     The other half of the same ruleset: degrees and milliseconds, how good the aim itself was
    ///     rather than how often it landed. One chip held all twenty-six and did not fit the board. The
    ///     seam is the unit, because a reader comparing percentages is asking a different question from
    ///     one comparing reaction times, and the split lands ten against eight rather than thirteen
    ///     against four.
    /// </summary>
    AimQuality,
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
                ? StatScale.Absolute(aMin, aMax, NeutralLow, NeutralHigh, Polarity)
                : null;
        }

        // Absolute colour over a peer bar. Handed the peers rather than a computed bar because a
        // degenerate peer set still leaves a fully-formed colour scale, and Hybrid knows that.
        if (ColourMin is { } cMin && ColourMax is { } cMax && cMax > cMin)
        {
            return StatScale.Hybrid(peers, cMin, cMax, NeutralLow, NeutralHigh, Polarity);
        }

        // Peer colour: with no peers there is nothing to say.
        if (StatScale.FromPeers(peers, Polarity) is not { } bar)
        {
            return null;
        }

        // The dead zone is a FRACTION of the observed range, because the range is not known until here.
        // Zero width would tint half the column, which is the heat-map failure the ramp exists to avoid.
        double dz = Math.Clamp(PeerDeadZone, 0, 1);
        double span = bar.Max - bar.Min;
        return StatScale.Banded(bar.Min, bar.Max,
            bar.Min + ((0.5 - (dz / 2)) * span),
            bar.Min + ((0.5 + (dz / 2)) * span),
            Polarity);
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
///     The population a per-opportunity column was measured over, so a cell can say how thin its sample
///     is. These used to be eight columns of their own. A count reads better beside the value it
///     qualifies than three columns to the right of it, and a denominator a reader cannot connect to its
///     metric has been deleted, not moved.
/// </summary>
/// <param name="Key">
///     Engine label of the count column. Still emitted, exported and read by the colour gates; never a
///     column on the board.
/// </param>
/// <param name="Noun">Plural noun for the count ("engagements", "contacts"). A count of one drops the s.</param>
/// <param name="Unit">Suffix on the value in the phrase: "%", " ms", "°". Empty for a bare number.</param>
/// <param name="Preposition">
///     How the value stands to the count, and part of the claim. "over" is a claim of division, and
///     the default: the value is the mean, or the share, of that many. "after" is for the three
///     after-contact rates, whose count is the gate the ruleset guards them with and NOT their divisor
///     (FB% is first_bullet_hits over first_bullet_shots, and the shot counts are not exported). No
///     fraction of 2 contacts is 56%, so "56% over 2 contacts" sends a reader looking for one.
/// </param>
public sealed record Denominator(string Key, string Noun, string Unit = "", string Preposition = "over");

/// <summary>
///     View-only presentation metadata for one stat column. <see cref="Key" /> is the engine label
///     (the <c>MetricTable.ValueColumns</c> entry: the export/golden parity key) and is NEVER
///     renamed; everything else is display.
///     <para>
///         A <see cref="Hidden" /> column stays in the table, the export and the colour gates and takes
///         no place on the board. <see cref="Denominator" /> names the count a cell's tooltip reads
///         beside its value.
///     </para>
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
    StatScaleSpec? Scale = null,
    bool Hidden = false,
    Denominator? Denominator = null);

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

    // ── Aim scale shapes ──────────────────────────────────────────────────────
    //
    // Almost every aim column is a PER-OPPORTUNITY EFFICIENCY (hits per bullet, clean stops per
    // attempt, degrees per contact), so by the rule above not one of them earns an absolute band. The
    // reference data available is 50 player-match rows and five pro accounts, nowhere near enough to
    // cut a percentile scale out of, so an invented band here would be a benchmark that looks
    // authoritative and is not. They take a peer bar and a peer tint.
    //
    // What they do need is the volume gate _duelWin exists for. Every ratio in the aim ruleset guards
    // its denominator with max(d, 1), which keeps the cell in the table but turns an unmeasured
    // population into a confident 0.0, and leaves a three-shot sample tinting exactly as hard as a
    // three-hundred-shot one. Below the gate a cell keeps its bar and loses its colour. A gate column
    // missing from the evaluation sums to zero and so gates the whole column off, which is the safe
    // direction: no tint beats a tint nobody can check.

    /// <summary>
    ///     The after-contact ratios (FB%, SAcc%, SprayAcc%), gated on the contact count. All three are
    ///     measured only after a round's first enemy contact, so Spots is their shared population and a
    ///     player the scanner never saw make contact has nothing at all behind their score.
    /// </summary>
    private static readonly StatScaleSpec _contactGatedUp =
        new(StatDomain.Peer, ColourGateColumns: ["Spots"], ColourGateMinimum: 8);

    /// <summary>Preaim: the same contact gate, but fewer degrees off the chest is better.</summary>
    private static readonly StatScaleSpec _contactGatedDown =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter,
            ColourGateColumns: ["Spots"], ColourGateMinimum: 8);

    /// <summary>
    ///     CS%, gated on its own denominator. A player who attempted three counter-strafes all match
    ///     reads 100% and means nothing by it, which is why the ruleset also ships CSAll% over every
    ///     bullet fired rather than shipping the rate alone.
    /// </summary>
    private static readonly StatScaleSpec _counterStrafeRate =
        new(StatDomain.Peer, ColourGateColumns: ["CSAtt"], ColourGateMinimum: 20);

    /// <summary>
    ///     Spray control: a smaller angular distance from the run's first bullet is tighter
    ///     compensation, gated on the bullets it could be measured over. That population is thinner
    ///     than the shot count suggests, because only LANDED bullets after a run's first one qualify
    ///     and bullet_damage does not fire for misses. A mean angle over one bullet is not a rating.
    /// </summary>
    private static readonly StatScaleSpec _sprayResidual =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter,
            ColourGateColumns: ["SprayN"], ColourGateMinimum: 20);

    /// <summary>
    ///     AimRx, gated on its own denominator. An aimed reaction only exists on an engagement where
    ///     the crosshair was seen arriving on the target, and the ruleset guards that denominator with
    ///     max(d, 1), so a player with none reads 0 ms: the fastest reaction on the board, under a
    ///     lower-is-better tint, off nothing at all. Declared HERE, above _byKey, for the reason the
    ///     comment at the top of this block gives.
    /// </summary>
    private static readonly StatScaleSpec _acquisitionGatedDown =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter,
            ColourGateColumns: ["AimRxn"], ColourGateMinimum: 8);

    /// <summary>
    ///     XPlace, gated on its own denominator. Crosshair travel only exists on an engagement that had
    ///     a contact, and a player without one reads 0.0, which is the shortest travel on the board and
    ///     would tint as the cleanest flick in the match.
    ///     <para>
    ///         Gated on XShots, NOT on Spot. Spot is per-round and XPlace is per-match, and
    ///         ClearsColourGate reads its gate columns off the SAME MetricRow, so a match-scoped row
    ///         carries no Spot at all: the gate summed to 0 and neutered the column outright. XShots is
    ///         the shared denominator the ruleset names for exactly this family, on the same scope.
    ///     </para>
    /// </summary>
    private static readonly StatScaleSpec _travelGatedDown =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter,
            ColourGateColumns: ["XShots"], ColourGateMinimum: 8);

    /// <summary>
    ///     TTS, gated on its own denominator for the same reason as <see cref="_acquisitionGatedDown" />:
    ///     it guards that denominator with max(d, 1), so a player the ruleset found no engagement for
    ///     reads 0 ms and tints as the fastest on the board off no data at all.
    ///     <para>
    ///         One gate column, not the ladder's two. ClearsColourGate SUMS its gate columns, so a
    ///         shared [TTSn, TTDn] gate would let a player's damage volume clear their shot column and
    ///         the other way round, which is exactly the thin sample the gate exists to catch.
    ///     </para>
    /// </summary>
    private static readonly StatScaleSpec _shootGatedDown =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter,
            ColourGateColumns: ["TTSn"], ColourGateMinimum: 8);

    /// <summary>TTD, gated on its own denominator. See <see cref="_shootGatedDown" />.</summary>
    private static readonly StatScaleSpec _damageGatedDown =
        new(StatDomain.Peer, StatPolarity.LowerIsBetter,
            ColourGateColumns: ["TTDn"], ColourGateMinimum: 8);

    private static int _seq;

    // Declared in canonical board order (the _seq counter IS the order).
    private static readonly Dictionary<string, ColumnMeta> _byKey = BuildCatalogue();

    private static ColumnMeta M(string key, string display, StatGroup group, string tooltip,
        Emphasis emphasis = Emphasis.None, ColumnAggregate agg = ColumnAggregate.Sum, double width = 60,
        StatScaleSpec? scale = null, bool hidden = false, Denominator? over = null) =>
        new(key, display, group, _seq++, true, emphasis, agg, tooltip, width, scale, hidden, over);

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

            // ── Aim: the Accuracy and Aim Quality chips ──
            //
            // Board order is the order rules/aim_rating.rules.yaml shows these in, and every Key below is
            // that file's `label:` byte for byte: an unregistered label still renders, but lands in Other
            // with no bar, no tint and a blank totals cell. Two chips, split on the unit: Accuracy holds
            // the percentages, Aim Quality the degrees and milliseconds. The per-engagement columns and
            // the one round-scoped aim column (Spot) are declared at the bottom of this array.
            //
            // The denominators are declared inline (over: new(...)) rather than as static fields on
            // purpose: a static declared below _byKey reads null here, and a null denominator is not a
            // crash but a cell that quietly shows no tooltip. Nine of them divide by the count they
            // name and read "over"; FB%, SAcc% and SprayAcc% divide by shot counts the ruleset does not
            // export and are only GATED on Spots, so their phrase says "after" and claims no fraction.
            M("Acc%", "Acc %", StatGroup.Accuracy, "Enemy bullet hits per bullet fired (%)", agg: ColumnAggregate.Average, width: 64, scale: _peerUp),
            // Bar, no tint, for the reason HS% already carries: a headshot share is a STYLE, not a
            // ranking. An AWPer's body hits kill exactly as well as a rifler's heads. HSDmg% divides by
            // ALL enemy damage on top of that, so a player putting half their output into grenades halves
            // the column without one thing about their aim having changed.
            M("HSAcc%", "HS Acc %", StatGroup.Accuracy, "Share of enemy bullet hits that landed on the head (%)", agg: ColumnAggregate.Average, width: 78, scale: _peerNoTint),
            M("HSDmg%", "HS Dmg %", StatGroup.Accuracy, "Share of enemy damage dealt by headshots, with utility damage in the denominator (%)", agg: ColumnAggregate.Average, width: 80, scale: _peerNoTint),
            M("CS%", "CS %", StatGroup.Accuracy, "Counter-strafes stopped clean, over the shots where one was attempted (%). Hover a cell for the attempts behind it", agg: ColumnAggregate.Average, width: 62, scale: _counterStrafeRate, over: new("CSAtt", "attempts", "%")),
            M("CSAll%", "CS All %", StatGroup.Accuracy, "Clean counter-strafes over every bullet fired, so a thin attempt count cannot flatter the rate (%)", agg: ColumnAggregate.Average, width: 76, scale: _peerUp),
            // Lower is better, unlike the two columns above it. These are the shots the engine charged a
            // movement penalty for. CSAll% + Linear% is the attempted share; the remainder was fired from
            // a standstill, which is neither a success nor a failure and so is not on the board at all.
            M("Linear%", "Linear %", StatGroup.Accuracy, "Bullets fired while still moving fast enough for the engine to charge movement inaccuracy (%)", agg: ColumnAggregate.Average, width: 76, scale: _peerDown),
            M("FB%", "First Bullet %", StatGroup.Accuracy, "First bullet out of the barrel that landed, after first contact (%). Hover a cell for the contacts behind it", agg: ColumnAggregate.Average, width: 100, scale: _contactGatedUp, over: new("Spots", "contacts", "%", "after")),
            M("Spray", "Spray Control", StatGroup.AimQuality, "Mean angular distance between a bullet and the first bullet of its spray: lower is tighter compensation. Hover a cell for the shots behind it", agg: ColumnAggregate.Average, width: 76, scale: _sprayResidual, over: new("SprayN", "shots", "°")),
            M("Preaim", "Preaim", StatGroup.AimQuality, "Mean degrees off the enemy's chest at first contact (lower is better). Hover a cell for the contacts behind it", agg: ColumnAggregate.Average, width: 70, scale: _contactGatedDown, over: new("Spots", "contacts", "°")),
            M("SAcc%", "Spotted Acc %", StatGroup.Accuracy, "Enemy bullet hits per bullet fired after first contact (%). Hover a cell for the contacts behind it", agg: ColumnAggregate.Average, width: 98, scale: _contactGatedUp, over: new("Spots", "contacts", "%", "after")),
            M("SprayAcc%", "Spray Acc %", StatGroup.Accuracy, "Bullets after the first out of the barrel that landed, after first contact (%). Hover a cell for the contacts behind it", agg: ColumnAggregate.Average, width: 92, scale: _contactGatedUp, over: new("Spots", "contacts", "%", "after")),
            // Populations: hidden. Each is the denominator of a column above and reads in that column's
            // cell tooltip, beside the value it qualifies, which is the only place a thin sample is
            // visible: three columns to the right, on a board that did not fit, nobody connected the
            // two. Still in the table, the export and the colour gates, which read the MetricRow and not
            // the cells. The group keeps each with the chip of the metric it gates, and the neutral scale
            // stays so the catalogue tests sweep every ruleset label the same way.
            M("CSAtt", "CS Attempts", StatGroup.Accuracy, "Shots where a counter-strafe was attempted: the CS% denominator", width: 92, scale: _peerNoTint, hidden: true),
            M("SprayN", "Spray Shots", StatGroup.AimQuality, "Shots whose spray residual could be measured: the Spray denominator", width: 88, scale: _peerNoTint, hidden: true),
            M("Spots", "Spots", StatGroup.Accuracy, "First enemy contacts: the Preaim denominator, and the gate behind every after-contact column", width: 64, scale: _peerNoTint, hidden: true),

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
            M("DeagleHS", "Deagle HS", StatGroup.Weapons, "Desert Eagle headshot kills this round", width: 78),

            // The round-scoped half of the aim ruleset. Both angle columns pair the round's FIRST contact
            // with its first landed bullet, which is an approximation the ruleset states rather than a
            // measurement, so Spot rides along: on a round with no contact their 0.0 is the empty state.
            M("XPlace", "Crosshair Travel", StatGroup.AimQuality, "Degrees the crosshair travelled from first contact to the first landed bullet (lower is better). Hover a cell for the shots behind it", agg: ColumnAggregate.Average, width: 112, scale: _travelGatedDown, over: new("XShots", "shots", "°")),
            // Signed, so neither direction is the good one: positive overshot and had to be walked back,
            // negative was dragged onto the target. Same treatment as FK+/-, where the sign is the story
            // and the bar already carries it.
            M("FlickErr", "Flick Error", StatGroup.AimQuality, "Crosshair travel minus the angle the contact demanded: positive overshot, negative undershot. Hover a cell for the shots behind it", agg: ColumnAggregate.Average, width: 84, scale: _peerNoTint, over: new("XShots", "shots", "°")),
            M("XShots", "Travel Shots", StatGroup.AimQuality, "Landed shots paired to a contact inside the engagement window: the XPlace and FlickErr denominator", width: 92, scale: _peerNoTint, hidden: true),
            M("TTS", "Time to Shoot", StatGroup.AimQuality, "Milliseconds from an enemy becoming visible to the first shot answering it: reaction without accuracy or fire rate mixed in. Hover a cell for the engagements behind it", agg: ColumnAggregate.Average, width: 96, scale: _shootGatedDown, over: new("TTSn", "engagements", " ms")),
            M("TTSn", "TTS Engagements", StatGroup.AimQuality, "Contacts answered by a shot: the TTS denominator", width: 96, scale: _peerNoTint, hidden: true),
            M("TTD", "Time to Damage", StatGroup.AimQuality, "Milliseconds from an enemy becoming visible to the first bullet that damaged them. Quantised by fire rate, so partly a first-shot-accuracy measure. Hover a cell for the engagements behind it", agg: ColumnAggregate.Average, width: 104, scale: _damageGatedDown, over: new("TTDn", "engagements", " ms")),
            M("TTDn", "TTD Engagements", StatGroup.AimQuality, "Contacts answered by a landed bullet: the TTD denominator", width: 96, scale: _peerNoTint, hidden: true),
            M("AimRx", "Aimed Reaction", StatGroup.AimQuality, "Milliseconds from the crosshair arriving on an enemy to the shot: reaction with the aim travel taken out. Hover a cell for the acquisitions behind it", agg: ColumnAggregate.Average, width: 100, scale: _acquisitionGatedDown, over: new("AimRxn", "acquisitions", " ms")),
            M("AimRxn", "AimRx Engagements", StatGroup.AimQuality, "Acquisitions answered by a shot: the AimRx denominator", width: 104, scale: _peerNoTint, hidden: true),
            M("TTK", "Time to Kill", StatGroup.AimQuality, "Milliseconds from an enemy becoming visible to killing them. Confounds aim with damage output and armour, and can read BELOW TTD because the two average over different engagements. Hover a cell for the kills behind it", agg: ColumnAggregate.Average, width: 88, scale: _peerNoTint, over: new("TTKn", "kills", " ms")),
            M("TTKn", "TTK Engagements", StatGroup.AimQuality, "Contacts that ended in a kill: the TTK denominator", width: 96, scale: _peerNoTint, hidden: true),
            M("Spot", "Spot", StatGroup.Accuracy, "The player made first contact this round: the round-board twin of Spots, and the only aim cell on the round board", width: 56, scale: _peerNoTint)
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
    ///     Whether a column takes a place on the board under a chip: its group, minus the hidden. The
    ///     one predicate the chip rail, the visible projection and the view switch all go through, so a
    ///     hidden column cannot be filtered out of one and left in another.
    /// </summary>
    public static bool ShowsUnder(string key, StatGroup group) =>
        Resolve(key) is { Hidden: false } meta && meta.Group == group;

    /// <summary>
    ///     Every catalogued column, for the tests that hold the whole catalogue to one rule rather than
    ///     a list of keys copied out of it. Not for the board: the board's order comes from the engine.
    /// </summary>
    public static IReadOnlyCollection<ColumnMeta> All => _byKey.Values;

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
