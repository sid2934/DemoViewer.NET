#region

using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     Pins <see cref="CounterStrafeAdmissionFold" /> against the engine, and exercises its whole
///     replay on whatever demo the machine has.
///     <para>
///         <b>What the fold is for.</b> The admission gate decides which shots enter the
///         counter-strafing denominator: a shot counts as an attempt when the shooter exceeded the
///         movement-inaccuracy threshold inside
///         <see cref="AimShotContextEdge.CounterStrafeLookbackSeconds" />. That window IS the
///         denominator, so it is the one number in this metric worth being able to sweep. The fold
///         records, per shot, the narrowest window that would admit it, which turns a sweep into a
///         comparison over one pass rather than one parse per candidate.
///     </para>
///     <para>
///         <b>Order of trust.</b> The fold is an independent replay, so it is checked against the
///         engine before its curve is read anywhere:
///         <see cref="Fold_AgreesWithTheEngine_AtTheShippedWindow" /> compares the two at the
///         shipped window per player. An oracle nobody checked against the thing it describes is
///         just a second opinion.
///     </para>
///     <para>
///         <b>The window is derived, and this class does not check the derivation.</b> It comes
///         from CS2's ground friction (see
///         <see cref="AimShotContextEdge.CounterStrafeLookbackSeconds" />, and the engine's own
///         <c>CounterStrafeWindowDerivationTests</c> for the proof); what this class does is
///         reproduce the engine's counts at whatever the shipped window is, and expose the rest of
///         the curve so a future candidate can be read against our own data.
///     </para>
///     <para>
///         <b>The demo tag sits on the methods, not the class.</b> Three of the four cases resolve a
///         <c>.dem</c> and carry <c>Category("RealDemo")</c> individually;
///         <see cref="FoldConstants_MatchTheEngine" /> reads two constants and needs nothing, so it
///         stays uncategorised and therefore runs in every tier. A class-level tag held it out of
///         the in-flight tiers, which is the gap <c>ShippedRulesetResolveTests</c> was written to
///         close: a duplicated constant drifting from the engine's is exactly what an in-flight run
///         should catch, and it costs nothing to check.
///     </para>
/// </summary>
[NotInParallel]
public class CounterStrafeAdmissionFoldTests
{
    /// <summary>
    ///     Widest window the sweep considers, in ticks. Capped at the width of the engine's own
    ///     per-slot speed ring, because a window wider than the ring reads only the part that
    ///     survived the wrap: sweeping past it would report a window the engine cannot implement
    ///     without also resizing that history.
    /// </summary>
    private const int SweepMaxTicks = 64;

    /// <summary>
    ///     How far the fold's admitted count may sit from the engine's before the agreement check
    ///     fires, as a share of the player's shots. Wide enough to absorb the one documented
    ///     difference between the two (the engine's liveness gate on a sampled tick against the
    ///     fold's controller-bound gate), and far too narrow for a different admission rule to slip
    ///     through: a one-tick change in the window moves these counts by whole percent.
    /// </summary>
    private const double FoldAgreementShareOfShots = 0.05;

    /// <summary>Absolute floor under <see cref="FoldAgreementShareOfShots" />, for low-volume players.</summary>
    private const int FoldAgreementFloor = 3;

    /// <summary>
    ///     The fold duplicates two of the engine's movement constants because an oracle that imports
    ///     the value it is checking is not an oracle. Duplication drifts, so it is pinned here.
    ///     Needs no demo.
    /// </summary>
    [Test]
    public async Task FoldConstants_MatchTheEngine()
    {
        // Collected rather than asserted one by one: TUnit rejects an assertion whose subject is a
        // compile-time constant, and a mismatch list also reports every drift in one run instead of
        // stopping at the first.
        List<string> drift = [];
        Compare(drift, "teleport cap",
            CounterStrafeAdmissionFold.TeleportSpeedThreshold, AimVantageScanner.TeleportSpeedThreshold);

        // Not a duplicated constant: the fold reads the engine's fraction directly, so this checks
        // the value itself is still the engine one rather than a copy that drifted.
        Compare(drift, "movement-inaccuracy fraction", AimShotContextEdge.CounterStrafeSpeedFraction, 0.34);

        double shipped = AimShotContextEdge.CounterStrafeLookbackSeconds;
        int shippedTicks = (int)Math.Round(shipped * 64.0);
        if (shipped <= 0)
        {
            drift.Add($"the shipped lookback is {shipped}, which admits nothing");
        }

        if (shippedTicks > SweepMaxTicks)
        {
            drift.Add(
                $"the shipped lookback is {shippedTicks} ticks at 64-tick, wider than the engine's "
                + $"{SweepMaxTicks}-tick speed ring, so it reads only what survived the wrap");
        }

        drift.ForEach(Console.WriteLine);
        await Assert.That(drift).IsEmpty();
    }

    /// <summary>
    ///     Checks the fold against the engine at the shipped window, per player. This is what earns
    ///     the fold the right to be read at any other window.
    /// </summary>
    /// <param name="demoId">The fixture directory name.</param>
    [Test]
    [Category("RealDemo")]
    [MethodDataSource(nameof(DemoIds))]
    public async Task Fold_AgreesWithTheEngine_AtTheShippedWindow(string demoId)
    {
        AdmissionFoldResult fold = FoldFor(demoId);
        AimRunResult run = LiveAimStats.Derive(demoId);

        int shippedTicks = fold.TicksFor(AimShotContextEdge.CounterStrafeLookbackSeconds);
        IReadOnlyDictionary<string, int> admitted = fold.AdmittedByName(shippedTicks);
        IReadOnlyDictionary<string, int> shots = fold.ShotsByName();

        List<string> divergences = [];
        int compared = 0;
        foreach ((string name, AimPlayerRow player) in run.Players)
        {
            if (player.Read("cs_attempts") is not { } engine)
            {
                continue;
            }

            int ours = admitted.GetValueOrDefault(name);
            int denominator = shots.GetValueOrDefault(name);
            compared++;

            double allowed = Math.Max(FoldAgreementFloor, denominator * FoldAgreementShareOfShots);
            double delta = Math.Abs(ours - engine);
            Console.WriteLine(
                $"   {name,-24} engine={engine,6:F0} fold={ours,6} shots={denominator,6} delta={delta,6:F0}");
            if (delta > allowed)
            {
                divergences.Add(
                    $"{name}: engine admitted {engine:F0}, the fold admitted {ours} of {denominator} shots "
                    + $"at the same {shippedTicks}-tick window (allowed {allowed:F1})");
            }
        }

        divergences.ForEach(Console.WriteLine);

        await Assert.That(compared).IsGreaterThan(0)
            .Because("an agreement check that compared no player validates nothing");
        await Assert.That(divergences).IsEmpty()
            .Because("the fold must reproduce the engine at the shipped window before its curve is "
                     + "trusted at any other one");
    }

    /// <summary>
    ///     The same agreement check as <see cref="Fold_AgreesWithTheEngine_AtTheShippedWindow" />, on
    ///     whatever demo this machine has rather than on the fixture corpus.
    ///     <para>
    ///         It exists because the fixture corpus and the demos a working checkout actually carries
    ///         are different sets: the fixture ids come from <c>tests/fixtures/</c>, whose
    ///         <c>.dem</c> files are gitignored, so on most machines the per-fixture case list skips
    ///         in full and the agreement between the fold and the engine goes unchecked at the shipped
    ///         window. This one runs wherever any demo is present, which means the window is pinned by
    ///         a test rather than by someone having compared two logs by hand.
    ///     </para>
    ///     <para>
    ///         Same tolerance and same comparison as the fixture version; the demo is parsed once and
    ///         handed to both sides.
    ///     </para>
    /// </summary>
    /// <returns>A task.</returns>
    [Test]
    [Category("RealDemo")]
    public async Task Fold_AgreesWithTheEngine_OnAnyAvailableDemo()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        AdmissionFoldResult fold = CounterStrafeAdmissionFold.Fold(demo, SweepMaxTicks);
        AimRunResult run = LiveAimStats.Derive(Path.GetFileName(demoPath), demoPath, demo);

        int shippedTicks = fold.TicksFor(AimShotContextEdge.CounterStrafeLookbackSeconds);
        IReadOnlyDictionary<string, int> admitted = fold.AdmittedByName(shippedTicks);
        IReadOnlyDictionary<string, int> shots = fold.ShotsByName();

        Console.WriteLine($"-- fold vs engine at {shippedTicks} ticks: {Path.GetFileName(demoPath)} --");

        List<string> divergences = [];
        int compared = 0;
        foreach ((string name, AimPlayerRow player) in run.Players)
        {
            if (player.Read("cs_attempts") is not { } engine)
            {
                continue;
            }

            int ours = admitted.GetValueOrDefault(name);
            int denominator = shots.GetValueOrDefault(name);
            compared++;

            double allowed = Math.Max(FoldAgreementFloor, denominator * FoldAgreementShareOfShots);
            double delta = Math.Abs(ours - engine);
            Console.WriteLine(
                $"   {name,-24} engine={engine,6:F0} fold={ours,6} shots={denominator,6} delta={delta,6:F0}");
            if (delta > allowed)
            {
                divergences.Add(
                    $"{name}: engine admitted {engine:F0}, the fold admitted {ours} of {denominator} shots "
                    + $"at the same {shippedTicks}-tick window (allowed {allowed:F1})");
            }
        }

        divergences.ForEach(Console.WriteLine);

        await Assert.That(compared).IsGreaterThan(0)
            .Because("an agreement check that compared no player validates nothing");
        await Assert.That(divergences).IsEmpty()
            .Because("the fold must reproduce the engine at the shipped window before its curve is "
                     + "trusted at any other one");
    }

    /// <summary>
    ///     Runs the fold on whatever demo this machine has and reports the admitted share at every
    ///     window.
    ///     <para>
    ///         Code-path coverage: the benchmark corpus is gitignored, so without this the entire
    ///         fold (entity replay, position differencing, the movement-cap read) would ship with
    ///         nothing having executed it. The assertions are the invariants any admission curve
    ///         must satisfy, so they hold on any source.
    ///     </para>
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task WindowSweep_RunsOnAnyAvailableDemo_AndReportsTheAdmittedShare()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        AdmissionFoldResult fold = CounterStrafeAdmissionFold.Fold(demo, SweepMaxTicks);

        if (fold.Shots.Count == 0)
        {
            throw new SkipTestException(
                "This demo carries no bullet weapon_fire inside a live round, so there is no "
                + "counter-strafing population to sweep.");
        }

        Console.WriteLine($"── admission curve: {Path.GetFileName(demoPath)}, {demo.MapName} ──");
        Console.WriteLine(
            $"   tickRate={fold.TickRate:F2} shots={fold.Shots.Count} unmeasurable={fold.UnmeasurableShots} "
            + $"outside-live-rounds={fold.ShotsOutsideLiveRounds}");

        List<string> violations = [];
        int previous = -1;
        for (int ticks = 0; ticks <= SweepMaxTicks; ticks += 4)
        {
            int admitted = fold.Shots.Count(shot => shot.AdmittedAt(ticks));
            int clean = fold.Shots.Count(shot => shot.AdmittedAt(ticks) && shot.CleanAtShot);
            double share = admitted / (double)fold.Shots.Count;
            Console.WriteLine(
                $"   {ticks,3} ticks ({ticks / fold.TickRate,5:F3} s): admitted={admitted,6} ({share,6:P1})"
                + $"  clean={clean,6}  bad={admitted - clean,6}");

            // Peak speed over a window cannot fall as the window widens, so neither can admission.
            // A curve that dipped would mean the history is being read wrong, not that the metric
            // moved.
            if (admitted < previous)
            {
                violations.Add($"admission fell from {previous} to {admitted} as the window widened to {ticks} ticks");
            }

            previous = admitted;
        }

        violations.ForEach(Console.WriteLine);

        await Assert.That(violations).IsEmpty();
        await Assert.That(fold.UnmeasurableShots).IsLessThan(fold.Shots.Count)
            .Because("every shot lacking a movement cap means the max-speed column is not decoding at all");
    }

    /// <summary>One case per demo in the benchmark fixture set.</summary>
    /// <returns>The demo ids.</returns>
    public static IEnumerable<string> DemoIds() => LiveAimStats.BenchmarkDemoIds();

    private static void Compare(List<string> drift, string what, double actual, double expected)
    {
        if (Math.Abs(actual - expected) > 1e-6)
        {
            drift.Add($"{what}: the fold uses {actual}, the engine uses {expected}");
        }
    }

    private static AdmissionFoldResult FoldFor(string demoId)
    {
        string demoPath = DemoTestHelper.RequireDemo(demoId + ".dem");
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        return CounterStrafeAdmissionFold.Fold(demo, SweepMaxTicks);
    }
}
