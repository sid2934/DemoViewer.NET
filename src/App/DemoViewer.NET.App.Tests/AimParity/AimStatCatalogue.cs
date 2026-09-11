#region

using System.Collections.Immutable;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     Which of the aim board's three capability tiers a stat sits in. A tier that the demo or the
///     checkout cannot support reads 0.0 rather than blank (see the header of
///     <c>rules/aim_rating.rules.yaml</c>), so a comparison must know the tier before it can tell a
///     score from an empty population.
/// </summary>
public enum AimTier
{
    /// <summary>Needs only <c>weapon_fire</c> and <c>player_hurt</c>. Every source carries these.</summary>
    ShotsAndHits = 1,

    /// <summary>Needs the per-tick shot enrichments (movement, recoil, aim punch).</summary>
    ShotEnrichment = 2,

    /// <summary>
    ///     Needs the synthesized <c>enemy_spotted</c> event, which exists only when the run is
    ///     handed baked map geometry.
    /// </summary>
    Visibility = 3
}

/// <summary>
///     One row of the aim board: the stat's pinned key, the rule that produces it, and the
///     population it is measured over.
/// </summary>
/// <param name="Canonical">
///     The key this stat is pinned under in <c>tests/fixtures/&lt;demo-id&gt;/aim.expected.golden.json</c>.
///     snake_case to match the rest of the golden schema.
/// </param>
/// <param name="RuleId">
///     The qualified v2 stat id (<c>aim_rating.&lt;stat&gt;</c>) read out of the materialized graph.
///     Read by rule id rather than by the <c>show:</c> column label so the harness covers the
///     populations that are not on the scoreboard (<c>cs_clean</c> is a numerator, not a column).
/// </param>
/// <param name="Tier">The capability tier, which decides whether a 0.0 is a score or an empty population.</param>
/// <param name="Note">The population this stat is measured over, spelled out.</param>
public sealed record AimStatDefinition(
    string Canonical,
    string RuleId,
    AimTier Tier,
    string Note);

/// <summary>
///     The aim stat universe the pin covers.
///     <para>
///         <b>Every denominator is named.</b> The aim board's whole design premise (see the header
///         of <c>rules/aim_rating.rules.yaml</c>) is that two tools calling something
///         "counter-strafing percent" usually agree about the shots and disagree about which shots
///         were counted. This table is where our own answer is written down, one row at a time, so a
///         column's population is a stated fact rather than something a reader has to reconstruct
///         from the ruleset.
///     </para>
/// </summary>
public static class AimStatCatalogue
{
    /// <summary>The ruleset id every stat below is qualified by.</summary>
    public const string RulesetId = "aim_rating";

    /// <summary>
    ///     The stat table. Ordered by tier so a report degrades readably on a source that supports
    ///     only the first one.
    /// </summary>
    public static ImmutableArray<AimStatDefinition> Stats { get; } =
    [
        // ── Tier 1: shots and hits ────────────────────────────────────────────────────────────
        new("shots_fired", "aim_rating.bullet_shots",
            AimTier.ShotsAndHits,
            "bullet weapon_fire during a live round"),

        new("enemy_hits", "aim_rating.enemy_hits",
            AimTier.ShotsAndHits,
            "damage instances against an enemy, so a penetrating bullet counts twice"),

        new("accuracy_pct", "aim_rating.accuracy_pct",
            AimTier.ShotsAndHits,
            "enemy_hits over bullet_shots"),

        new("hs_accuracy_pct", "aim_rating.hs_accuracy_pct",
            AimTier.ShotsAndHits,
            "head damage instances over bullet_shots"),

        new("hs_damage_pct", "aim_rating.hs_damage_pct",
            AimTier.ShotsAndHits,
            "head damage over ALL enemy damage, utility included"),

        // ── Tier 2: the shot enrichments ──────────────────────────────────────────────────────
        new("cs_attempts", "aim_rating.cs_attempts",
            AimTier.ShotEnrichment,
            "shots admitted by the movement lookback gate: the counter-strafing denominator"),

        new("cs_clean", "aim_rating.cs_clean",
            AimTier.ShotEnrichment,
            "admitted shots taken below the movement-inaccuracy threshold"),

        new("moving_shots", "aim_rating.moving_shots",
            AimTier.ShotEnrichment,
            "admitted shots the engine charged a movement penalty for"),

        new("counter_strafe_pct", "aim_rating.counter_strafe_pct",
            AimTier.ShotEnrichment,
            "clean over attempted; inherits whatever the admission window leaves on the table"),

        new("counter_strafe_all_pct", "aim_rating.counter_strafe_all_pct",
            AimTier.ShotEnrichment,
            "clean over ALL bullets, so a thin attempt count cannot flatter the ratio"),

        new("spray_residual_shots", "aim_rating.spray_residual_shots",
            AimTier.ShotEnrichment,
            "the spray-control population: bullets inside a spray run, past its first"),

        new("spray_error_deg", "aim_rating.spray_error_deg",
            AimTier.ShotEnrichment,
            "mean 3D angle from the run's first landed bullet, in degrees; measured on the landed "
            + "arm, so it also needs bullet_damage. SprayControlOracle prints beside it"),

        // ── Tier 3: the enemy_spotted contact gate ────────────────────────────────────────────
        new("contact_shots", "aim_rating.contact_shots",
            AimTier.Visibility,
            "bullets fired after first contact; the latch holds for the round, so it over-counts a rotator"),

        new("contact_hits", "aim_rating.contact_hits",
            AimTier.Visibility,
            "same latch on the hit side"),

        new("spotted_accuracy_pct", "aim_rating.spotted_accuracy_pct",
            AimTier.Visibility,
            "the ratio of the two above"),

        new("spray_accuracy_pct", "aim_rating.spray_accuracy_pct",
            AimTier.Visibility,
            "hit side from bullet_damage, shot side from weapon_fire, inside a spray run"),

        new("preaim_deg", "aim_rating.preaim_deg",
            AimTier.Visibility,
            "mean degrees off the chest at FIRST contact; lower is better"),

        new("first_contacts", "aim_rating.first_contacts",
            AimTier.Visibility,
            "the preaim denominator; zero here is what marks a preaim of 0.0 as an empty population")
    ];
}
