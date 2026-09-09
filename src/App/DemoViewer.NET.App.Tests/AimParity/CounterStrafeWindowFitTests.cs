#region

using System.Collections.Concurrent;
using System.Globalization;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     Fits the counter-strafe admission window against Leetify's own denominator, and holds the
///     shipped constant to that fit.
///     <para>
///         <b>The thing being calibrated.</b> Leetify never published which shots count as a
///         counter-strafing attempt, and their numbers prove the population is narrower than all
///         shots: on the first benchmark demo <c>counterStrafingShotsAll</c> is 108 against 307
///         <c>shotsFired</c>, and across the five demos the admitted share runs 22 to 29 percent.
///         Our gate admits a shot when the shooter exceeded the movement-inaccuracy threshold
///         inside a lookback window, so the window IS the denominator, and
///         <see cref="AimShotContextEdge.CounterStrafeLookbackSeconds" /> is a free parameter with
///         a number in it. This class is what turns that number from an assertion into a
///         measurement.
///     </para>
///     <para>
///         <b>Order of trust.</b> The fold this fits with is an independent replay, so it is
///         checked against the engine before it is believed:
///         <see cref="Fold_AgreesWithTheEngine_AtTheShippedWindow" /> compares the two at the
///         shipped window per player, and only then does
///         <see cref="ShippedLookback_IsTheBestFitAcrossTheBenchmarkSet" /> read the curve at every
///         other window. Calibrating against an oracle nobody validated would move the constant to
///         wherever the oracle happens to be wrong.
///     </para>
///     <para>
///         <b>Unverified as committed.</b> None of the five benchmark demos is in this checkout
///         (<c>demos/**/*.dem</c> is gitignored), so every case that needs a reference skips and the
///         constant has NOT been fitted by a run of this harness. Restore the corpus and the fit
///         either confirms 0.5 s or fails with the window that does fit printed beside it.
///     </para>
///     <para>
///         <b>What the reference-free sweep already says.</b> Run on a Valve matchmaking demo with
///         no Leetify partner (2,937 bullet shots on de_mirage), the curve reads 22.0 percent
///         admitted at a zero window, 43.9 percent at 0.25 s and 61.5 percent at the shipped 0.5 s.
///         Leetify's own admitted share on their five demos is 21.9 to 29.4 percent. A different
///         match is not a fit, but a factor of two on a corpus of the same kind and vintage is not
///         cross-demo variation either, and 0.5 s is the number that has to survive it.
///     </para>
///     <para>
///         <b>And what no window can fix.</b> The BAD count (admitted, and still above the threshold
///         when the trigger came) does not move with the window at all: a shot taken while moving is
///         admitted by its own tick, so widening the window only adds CLEAN shots. That sweep reads
///         645 bad out of 2,937 shots, 22 percent, against Leetify's 3.3 to 7.6 percent. The window
///         is therefore not the only free parameter in play, and a fit that drove the denominator
///         onto theirs would still leave the good/bad split wrong. The likely next suspect is the
///         standstill test's input rather than its threshold: the engine prefers the server's own
///         <c>bullet_damage.InaccuracyMove</c> where it exists, which is only on shots that LANDED,
///         and falls back to a position-differenced speed everywhere else.
///     </para>
/// </summary>
[Category("RealDemo")]
[NotInParallel]
public class CounterStrafeWindowFitTests
{
    /// <summary>
    ///     Widest window the sweep considers, in ticks. Capped at the width of the engine's own
    ///     per-slot speed ring, because a window wider than the ring reads only the part that
    ///     survived the wrap: fitting past it would name a window the engine cannot implement
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

    // One fold per demo per process. The parse cache holds a single demo at a time, so without this
    // the aggregate fit would re-read and re-parse every 300 MB demo the per-demo cases already did.
    private static readonly ConcurrentDictionary<string, AdmissionFoldResult> _folds = new();

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

    private static void Compare(List<string> drift, string what, double actual, double expected)
    {
        if (Math.Abs(actual - expected) > 1e-6)
        {
            drift.Add($"{what}: the fold uses {expected}, the engine uses {actual}");
        }
    }

    /// <summary>
    ///     Prints the whole admitted-count curve for one demo and names the window that best matches
    ///     Leetify's <c>counterStrafingShotsAll</c>. Diagnostic: it asserts that the comparison
    ///     happened, not which window won, because one demo is not the fit.
    /// </summary>
    /// <param name="demoId">The fixture directory name.</param>
    [Test]
    [MethodDataSource(nameof(DemoIds))]
    public async Task WindowSweep_ReportsTheFitForOneDemo(string demoId)
    {
        LeetifyAimReference reference = RequireReference(demoId);
        AdmissionFoldResult fold = FoldFor(demoId);

        Console.WriteLine($"── counter-strafe window sweep: {demoId} ──");
        Console.WriteLine(
            $"   tickRate={fold.TickRate:F2} shots={fold.Shots.Count} unmeasurable={fold.UnmeasurableShots} "
            + $"outside-live-rounds={fold.ShotsOutsideLiveRounds}");
        Console.WriteLine(
            $"   {"window",-14}{"admitted",9}{"csAll",9}{"abs err",9}{"clean",9}{"csGood",9}{"clean err",10}{"mean|rel|",11}");

        WindowFit best = SweepAndPrint([(reference, fold)]);
        Console.WriteLine(
            $"   best fit: {best.Seconds:F3} s ({best.Ticks} ticks), absolute error {best.AbsoluteError}");

        await Assert.That(best.ComparedPlayers).IsGreaterThan(0)
            .Because("a sweep that matched no player to a reference row has fitted nothing");
    }

    /// <summary>
    ///     The fit itself, across every benchmark demo at once, and the gate on the shipped
    ///     constant. Needs all five demos: a window fitted on one map is a window fitted to that
    ///     map's movement, and the point of the benchmark set is that it is five.
    /// </summary>
    [Test]
    public async Task ShippedLookback_IsTheBestFitAcrossTheBenchmarkSet()
    {
        List<(LeetifyAimReference Reference, AdmissionFoldResult Fold)> cases = [];
        List<string> missing = [];
        foreach (string demoId in DemoIds())
        {
            if (LeetifyAimReference.TryLoad(demoId) is not { } reference)
            {
                continue;
            }

            if (DemoTestHelper.FindDemoPath(demoId + ".dem") is null)
            {
                missing.Add(demoId);
                continue;
            }

            cases.Add((reference, FoldFor(demoId)));
        }

        if (missing.Count > 0 || cases.Count == 0)
        {
            throw new SkipTestException(
                $"The window fit needs the whole benchmark corpus; {missing.Count} demo(s) are absent "
                + $"({string.Join(", ", missing)}). Until they are restored the shipped lookback of "
                + $"{AimShotContextEdge.CounterStrafeLookbackSeconds:F2} s is UNFITTED by this harness.");
        }

        Console.WriteLine($"── counter-strafe window fit across {cases.Count} demos ──");
        Console.WriteLine(
            $"   {"window",-14}{"admitted",9}{"csAll",9}{"abs err",9}{"clean",9}{"csGood",9}{"clean err",10}{"mean|rel|",11}");
        WindowFit best = SweepAndPrint(cases);

        double shipped = AimShotContextEdge.CounterStrafeLookbackSeconds;
        int shippedTicks = cases[0].Fold.TicksFor(shipped);
        Console.WriteLine(
            $"   best fit: {best.Seconds:F3} s ({best.Ticks} ticks) | shipped: {shipped:F3} s ({shippedTicks} ticks)");

        // One tick of slack, and no more. The sweep steps in ticks, the demos need not share a tick
        // rate, and a curve with a flat bottom can put the argmin either side of the true value; two
        // ticks of drift is 30 ms, which is a different rule rather than a rounding difference.
        await Assert.That(Math.Abs(best.Ticks - shippedTicks)).IsLessThanOrEqualTo(1)
            .Because(
                $"the shipped lookback must BE the fitted one: {best.Seconds:F3} s fits this corpus and "
                + $"{shipped:F3} s is what AimShotContextEdge.CounterStrafeLookbackSeconds ships. "
                + "Move the constant to the fitted value, or record why the fit is wrong; do not "
                + "adjust it to make a ratio look better.");
    }

    /// <summary>
    ///     Checks the fold against the engine at the shipped window, per player. This is what earns
    ///     the fold the right to be read at any other window.
    /// </summary>
    /// <param name="demoId">The fixture directory name.</param>
    [Test]
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
    ///     Runs the fold on whatever demo this machine has and reports the admitted share at every
    ///     window, with no reference involved.
    ///     <para>
    ///         Code-path coverage rather than calibration: the benchmark corpus is gitignored, so
    ///         without this the entire fold (entity replay, position differencing, the movement-cap
    ///         read) would ship with nothing having executed it. The two Valve matchmaking demos
    ///         carry the movement columns and no Leetify partner, which is exactly what this case is
    ///         shaped for. The assertions are the invariants any admission curve must satisfy, so
    ///         they hold on any source.
    ///     </para>
    /// </summary>
    [Test]
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

        Console.WriteLine($"── admission curve (no reference): {Path.GetFileName(demoPath)}, {demo.MapName} ──");
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

    /// <summary>One case per demo with a committed Leetify reference.</summary>
    /// <returns>The demo ids.</returns>
    public static IEnumerable<string> DemoIds() => LeetifyAimReference.AllDemoIds();

    private static WindowFit SweepAndPrint(IReadOnlyList<(LeetifyAimReference Reference, AdmissionFoldResult Fold)> cases)
    {
        WindowFit? best = null;
        for (int ticks = 0; ticks <= SweepMaxTicks; ticks++)
        {
            WindowFit fit = Evaluate(cases, ticks);
            string flat = best is null || fit.AbsoluteError < best.AbsoluteError ? " *" : "";
            Console.WriteLine(
                $"   {fit.Seconds.ToString("F3", CultureInfo.InvariantCulture) + " s",-14}"
                + $"{fit.Admitted,9}{fit.Reference,9}{fit.AbsoluteError,9}"
                + $"{fit.Clean,9}{fit.ReferenceClean,9}{fit.CleanAbsoluteError,10}"
                + $"{fit.MeanRelativeError.ToString("P1", CultureInfo.InvariantCulture),11}{flat}");

            if (best is null || fit.AbsoluteError < best.AbsoluteError)
            {
                best = fit;
            }
        }

        return best ?? throw new InvalidOperationException("the sweep evaluated no window");
    }

    private static WindowFit Evaluate(
        IReadOnlyList<(LeetifyAimReference Reference, AdmissionFoldResult Fold)> cases, int ticks)
    {
        long admitted = 0;
        long referenceTotal = 0;
        long absoluteError = 0;
        long clean = 0;
        long referenceClean = 0;
        long cleanAbsoluteError = 0;
        double relativeErrorSum = 0;
        int players = 0;
        double seconds = 0;

        foreach ((LeetifyAimReference reference, AdmissionFoldResult fold) in cases)
        {
            seconds = ticks / fold.TickRate;
            IReadOnlyDictionary<string, int> admittedByName = fold.AdmittedByName(ticks);
            IReadOnlyDictionary<string, int> cleanByName = fold.CleanByName(ticks);
            foreach ((string name, LeetifyAimRow row) in reference.Players)
            {
                if (row.CounterStrafingShotsAll is not { } theirs)
                {
                    continue;
                }

                // A player the fold never saw is a join failure, not a zero: counting them as zero
                // would drag the fit toward a wider window to make up the shortfall.
                if (!fold.NamesBySlot.Values.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                int ours = admittedByName.GetValueOrDefault(name);
                players++;
                admitted += ours;
                referenceTotal += (long)theirs;
                absoluteError += Math.Abs(ours - (long)theirs);
                if (theirs > 0)
                {
                    relativeErrorSum += Math.Abs(ours - theirs) / theirs;
                }

                // The clean split is reported, not minimised. Admission widens with the window and
                // the standstill test does not, so the two facets pull in opposite directions: a
                // window fitted on the denominator alone can still get the numerator badly wrong,
                // and seeing both columns is what tells a reader which of those happened.
                if (row.CounterStrafingShotsGood is { } theirsClean)
                {
                    int oursClean = cleanByName.GetValueOrDefault(name);
                    clean += oursClean;
                    referenceClean += (long)theirsClean;
                    cleanAbsoluteError += Math.Abs(oursClean - (long)theirsClean);
                }
            }
        }

        return new WindowFit(
            ticks, seconds, admitted, referenceTotal, absoluteError, clean, referenceClean,
            cleanAbsoluteError, players > 0 ? relativeErrorSum / players : 0, players);
    }

    private static AdmissionFoldResult FoldFor(string demoId)
    {
        if (_folds.TryGetValue(demoId, out AdmissionFoldResult? cached))
        {
            return cached;
        }

        string demoPath = DemoTestHelper.RequireDemo(demoId + ".dem");
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        AdmissionFoldResult fold = CounterStrafeAdmissionFold.Fold(demo, SweepMaxTicks);
        _folds[demoId] = fold;
        return fold;
    }

    private static LeetifyAimReference RequireReference(string demoId) =>
        LeetifyAimReference.TryLoad(demoId)
        ?? throw new SkipTestException($"No Leetify reference committed for '{demoId}'.");

    /// <summary>One candidate window and how well it reproduces Leetify's denominator.</summary>
    /// <param name="Ticks">The window in ticks.</param>
    /// <param name="Seconds">The same window in seconds, at the last demo's tick rate.</param>
    /// <param name="Admitted">Shots our gate admits at this window, summed over every player.</param>
    /// <param name="Reference">Shots Leetify admits, summed over the same players.</param>
    /// <param name="AbsoluteError">Sum of per-player absolute differences. The objective being minimised.</param>
    /// <param name="Clean">Admitted shots taken from a standstill, our <c>cs_clean</c>.</param>
    /// <param name="ReferenceClean">Leetify's <c>counterStrafingShotsGood</c> over the same players.</param>
    /// <param name="CleanAbsoluteError">
    ///     Sum of per-player absolute differences on the clean split. Reported, not minimised: the
    ///     stated calibration target is the denominator, and a fit that traded it away to flatter
    ///     the numerator would be fitting a ratio rather than a population.
    /// </param>
    /// <param name="MeanRelativeError">Mean per-player relative error, printed so a fit dominated by one heavy player is visible.</param>
    /// <param name="ComparedPlayers">How many players contributed, so a vacuous sweep is not read as a fit.</param>
    private sealed record WindowFit(
        int Ticks,
        double Seconds,
        long Admitted,
        long Reference,
        long AbsoluteError,
        long Clean,
        long ReferenceClean,
        long CleanAbsoluteError,
        double MeanRelativeError,
        int ComparedPlayers);
}
