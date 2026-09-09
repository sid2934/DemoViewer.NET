#region

using System.Globalization;
using CS2DemoKit.Analysis.GoldenStats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     The aim board's parity gate: runs <c>rules/aim_rating.rules.yaml</c> over each benchmark
///     demo and compares every column two ways, against a pinned reference at zero tolerance and
///     against Leetify's own published numbers as a calibration report.
///     <para>
///         <b>Two references, two postures.</b> The pinned
///         <c>tests/fixtures/&lt;demo-id&gt;/aim.expected.golden.json</c> is the assertion: it says the
///         engine still produces what it produced when a human pinned it, and it is compared at zero
///         tolerance because two runs over the same bytes are deterministic. Leetify is a
///         calibration reference, not an assertion: their populations are theirs, several of our
///         columns are deliberately defined differently (see <see cref="AimStatCatalogue" />), and a
///         tolerance nobody has fitted is either a false alarm or worthless. So the Leetify arm
///         asserts only what no calibration can excuse (a column that reads zero against a
///         reference that says hundreds, a join that failed wholesale, a count that breaks its own
///         nesting) and reports the rest as a table for the person doing the fitting.
///     </para>
///     <para>
///         <b>Never edit the pin to absorb a diff.</b> Fix the engine, or hand-verify the value and
///         re-pin deliberately, recording it by moving the file's <c>provider_version</c> off the
///         machine-generated marker. <c>tests/fixtures/README.md</c> carries the posture in full.
///     </para>
///     <para>
///         <b>Skips are the honest answer.</b> The five benchmark demos are gitignored, so on a
///         checkout without them these cases skip rather than passing on nothing. The two tests at
///         the bottom of this class need no demo at all and run everywhere: they are what keeps the
///         Leetify field inventory honest while the corpus is absent.
///     </para>
/// </summary>
[Category("RealDemo")]
[NotInParallel]
public class AimStatParityTests
{
    /// <summary>
    ///     Fixed so a re-pin diffs only on values. The converter stamps <c>DateTime.UtcNow</c>
    ///     otherwise, which makes every re-pin a diff even when nothing moved.
    /// </summary>
    private const string PinnedTimestamp = "2026-09-09T00:00:00.0000000Z";

    /// <summary>The pinned file's name inside a demo's fixture directory.</summary>
    private const string PinFileName = "aim.expected.golden.json";

    /// <summary>
    ///     Set <c>PIN_EXPECTED_AIM=1</c> with the demos present to write the pin. Deliberate and
    ///     reviewed only; the fixture is the assertion.
    /// </summary>
    private const string PinEnvironmentVariable = "PIN_EXPECTED_AIM";

    /// <summary>
    ///     How far our value may sit from Leetify's before the coarse gate fires, as a ratio. Wide
    ///     on purpose: this is not a calibration tolerance, it is the bound that separates "our
    ///     population is defined differently" from "this column is not being computed at all". A
    ///     column that reads 0.0 against a reference of 108 fails it; a 30 percent definitional gap
    ///     does not.
    /// </summary>
    private const double CoarseRatioBound = 3.0;

    /// <summary>
    ///     One case per demo, because the engine run is the expensive part: every stat of every
    ///     player comes out of one evaluation.
    /// </summary>
    /// <param name="demoId">The fixture directory name.</param>
    [Test]
    [MethodDataSource(nameof(DemoIds))]
    public async Task OursVsExpected_AimStatParity(string demoId)
    {
        bool pinning = string.Equals(
            Environment.GetEnvironmentVariable(PinEnvironmentVariable), "1", StringComparison.Ordinal);
        string fixtureDir = FixtureDirectory(demoId);
        string pinPath = Path.Combine(fixtureDir, PinFileName);

        // Checked before parsing: a fixture directory with no aim pin is not an aim parity fixture,
        // and discovering that must not cost a multi-gigabyte parse.
        if (!pinning && !File.Exists(pinPath))
        {
            throw new SkipTestException(
                $"No aim parity pin for '{demoId}' ({pinPath}). "
                + $"Create one with {PinEnvironmentVariable}=1 while the demo is available.");
        }

        AimRunResult run = LiveAimStats.Derive(demoId);

        if (pinning)
        {
            Directory.CreateDirectory(fixtureDir);
            GoldenStatsSerializer.WriteToFile(ToDocument(run), pinPath);

            // Skip, not return. A re-pin asserts nothing, and reporting it as passed is exactly the
            // green-but-empty result this gate exists to remove.
            throw new SkipTestException($"Re-pinned {pinPath}. Review the diff before committing.");
        }

        GoldenStatsDocument pinned = GoldenStatsSerializer.ReadFromFile(pinPath);
        RequireSameDemo(pinned, run);
        RequireSameCapability(pinned, run);

        List<string> divergences = [];
        int compared = 0;
        int nullSkipped = 0;

        foreach ((string player, PlayerStatsRecord expected) in pinned.Players)
        {
            if (!run.Players.TryGetValue(player, out AimPlayerRow? ours))
            {
                divergences.Add($"  {player,-28} missing from the live run entirely");
                continue;
            }

            foreach (AimStatDefinition stat in AimStatCatalogue.Produced())
            {
                double? want = expected.Stats.TryGetValue(stat.Canonical, out double? pinnedValue)
                    ? pinnedValue
                    : null;
                double? got = ours.Read(stat.Canonical);
                if (want is null || got is null)
                {
                    nullSkipped++;
                    continue;
                }

                compared++;
                double delta = got.Value - want.Value;
                if (delta != 0.0)
                {
                    divergences.Add(
                        $"  {player,-24} {stat.Canonical,-22} ours={got,10:F3}  pinned={want,10:F3}  "
                        + $"delta={(delta >= 0 ? "+" : "")}{delta:F3}");
                }
            }
        }

        Console.WriteLine(
            $"{demoId} | aim: live vs pinned | compared={compared} divergences={divergences.Count} "
            + $"null-skipped={nullSkipped} visibility={(run.VisibilityAvailable ? "on" : "off")}");
        divergences.ForEach(Console.WriteLine);

        await Assert.That(compared).IsGreaterThan(0)
            .Because("a run that compares nothing is the failure mode this gate exists to remove");
        await Assert.That(divergences).IsEmpty();
    }

    /// <summary>
    ///     The calibration arm: prints every column against Leetify's own value and fires only on
    ///     the failures no definitional difference can excuse.
    /// </summary>
    /// <param name="demoId">The fixture directory name.</param>
    [Test]
    [MethodDataSource(nameof(DemoIds))]
    public async Task OursVsLeetify_AimStatDivergenceReport(string demoId)
    {
        LeetifyAimReference reference = LeetifyAimReference.TryLoad(demoId)
                                        ?? throw new SkipTestException(
                                            $"No Leetify reference committed for '{demoId}'.");

        AimRunResult run = LiveAimStats.Derive(demoId);

        List<string> joined = [];
        List<string> unjoined = [];
        foreach (string name in reference.Players.Keys)
        {
            (run.Players.ContainsKey(name) ? joined : unjoined).Add(name);
        }

        Console.WriteLine(
            $"{demoId} | aim: live vs Leetify | map={run.MapName} visibility={(run.VisibilityAvailable ? "on" : "off")} "
            + $"joined={joined.Count}/{reference.Players.Count}");
        unjoined.ForEach(name => Console.WriteLine($"  unjoined: {name}"));

        List<string> failures = [];
        foreach (AimStatDefinition stat in AimStatCatalogue.Stats)
        {
            ReportOneStat(stat, reference, run, joined, failures);
        }

        // Internal nesting, which holds whatever either side calls a counter-strafe: a clean
        // attempt is an attempt, and an attempt is a bullet. This one needs no reference at all,
        // so it fires even on a demo Leetify never saw.
        foreach (string name in joined)
        {
            AimPlayerRow ours = run.Players[name];
            double clean = ours.Read("cs_clean") ?? 0;
            double attempts = ours.Read("cs_attempts") ?? 0;
            double shots = ours.Read("shots_fired") ?? 0;
            if (clean > attempts || attempts > shots)
            {
                failures.Add($"{name}: cs_clean={clean} cs_attempts={attempts} shots={shots} does not nest");
            }
        }

        failures.ForEach(Console.WriteLine);

        await Assert.That(joined.Count * 2).IsGreaterThanOrEqualTo(reference.Players.Count)
            .Because("a join that fails for most players compares nothing and must not read green");
        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    ///     Schema-present is not data-populated. Leetify declares <c>recoilShots</c> and
    ///     <c>recoilShotsHit</c>, the pair a spray-control comparison would be built on, and both
    ///     are null in every row of every demo we hold; <c>shotsHitFoeHead</c> is present and zero
    ///     everywhere. This is the evidence behind
    ///     <see cref="AimComparisonMode.NoReference" /> on the spray columns, and it is asserted
    ///     rather than written in a comment so that a payload which starts carrying them is a red
    ///     test rather than a discovery nobody makes.
    ///     <para>
    ///         Needs no demo, so it runs on a bare checkout: while the corpus is absent this is the
    ///         only part of the aim parity work that actually executes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task LeetifyReference_DeclaresAimFieldsItNeverPopulates()
    {
        IReadOnlyList<string> demoIds = LeetifyAimReference.AllDemoIds();
        if (demoIds.Count == 0)
        {
            throw new SkipTestException("No Leetify references committed under demos/benchmarks/.");
        }

        List<string> populated = [];
        int rows = 0;
        foreach (string demoId in demoIds)
        {
            LeetifyAimReference? reference = LeetifyAimReference.TryLoad(demoId);
            if (reference is null)
            {
                continue;
            }

            foreach (LeetifyAimRow row in reference.Players.Values)
            {
                rows++;
                foreach (string field in LeetifyAimReference.KnownUnpopulatedFields)
                {
                    // Zero counts as unpopulated here on purpose: shotsHitFoeHead is present and 0
                    // on every row, which as a head-hit numerator is indistinguishable from absent.
                    if (row.Read(field) is { } value && value != 0.0)
                    {
                        populated.Add($"{demoId}/{row.Name}: {field}={value}");
                    }
                }
            }
        }

        Console.WriteLine(
            $"aim reference inventory | demos={demoIds.Count} rows={rows} "
            + $"never-populated={string.Join(", ", LeetifyAimReference.KnownUnpopulatedFields)}");
        populated.ForEach(Console.WriteLine);

        await Assert.That(rows).IsGreaterThan(0);
        await Assert.That(populated).IsEmpty()
            .Because("a field that starts carrying data changes what the spray columns can be validated against");
    }

    /// <summary>
    ///     Every aim-relevant field the reference actually carries must appear in
    ///     <see cref="AimStatCatalogue" />, with an explicit verdict. A reference column nobody
    ///     mapped is a comparison silently not made, which is the same green-on-nothing failure the
    ///     rest of this class is built against.
    /// </summary>
    [Test]
    public async Task AimStatCatalogue_AccountsForEveryReferenceField()
    {
        string[] referenceFields =
        [
            "shotsFired", "shotsHitFoe", "accuracy", "accuracyHead", "shotsFiredEnemySpotted",
            "shotsHitEnemySpotted", "accuracyEnemySpotted", "sprayAccuracy", "preaim", "reactionTime",
            "counterStrafingShotsAll", "counterStrafingShotsGood", "counterStrafingShotsBad",
            "counterStrafingShotsGoodRatio"
        ];

        HashSet<string> mapped = new(
            AimStatCatalogue.Stats.Select(s => s.LeetifyField).OfType<string>(), StringComparer.Ordinal);

        List<string> unmapped = referenceFields.Where(f => !mapped.Contains(f)).ToList();
        unmapped.ForEach(f => Console.WriteLine($"unmapped Leetify field: {f}"));

        await Assert.That(unmapped).IsEmpty();

        // And the reverse: a table row naming a field the payload does not carry is a typo that
        // would silently never compare.
        LeetifyAimReference? any = LeetifyAimReference.AllDemoIds()
            .Select(LeetifyAimReference.TryLoad)
            .FirstOrDefault(r => r is not null);
        if (any is null)
        {
            throw new SkipTestException("No Leetify references committed under demos/benchmarks/.");
        }

        LeetifyAimRow sample = any.Players.Values.First();
        foreach (AimStatDefinition stat in AimStatCatalogue.Stats)
        {
            if (stat.LeetifyField is { } field)
            {
                _ = sample.Read(field); // throws when the name is not one the row carries
            }
        }
    }

    /// <summary>One case per demo with a committed Leetify reference.</summary>
    /// <returns>The demo ids.</returns>
    public static IEnumerable<string> DemoIds() => LeetifyAimReference.AllDemoIds();

    private static void ReportOneStat(
        AimStatDefinition stat,
        LeetifyAimReference reference,
        AimRunResult run,
        IReadOnlyList<string> joined,
        List<string> failures)
    {
        if (stat.Mode == AimComparisonMode.NotImplemented)
        {
            Console.WriteLine($"  {stat.Canonical,-22} NOT COMPUTED: {stat.Note}");
            return;
        }

        if (stat.LeetifyField is not { } field)
        {
            Console.WriteLine($"  {stat.Canonical,-22} no reference: {stat.Note}");
            return;
        }

        bool tierSupported = run.Supports(stat.Tier);
        int pairs = 0;
        double absoluteRelativeErrorSum = 0;
        List<string> perPlayer = [];

        foreach (string name in joined)
        {
            double? theirs = reference.Players[name].Read(field) * stat.LeetifyScale;
            double? ours = run.Players[name].Read(stat.Canonical);
            if (theirs is null || ours is null)
            {
                continue;
            }

            pairs++;
            double denominator = Math.Abs(theirs.Value);
            if (denominator > 0)
            {
                absoluteRelativeErrorSum += Math.Abs(ours.Value - theirs.Value) / denominator;
            }

            perPlayer.Add($"      {name,-24} ours={ours,9:F2}  leetify={theirs,9:F2}");

            if (!tierSupported || stat.Mode != AimComparisonMode.Comparable || denominator <= 0)
            {
                continue;
            }

            // The coarse gate. Only fires on a column that is not being computed, or one whose
            // units are wrong by an order of magnitude; a definitional gap sits well inside it.
            double ratio = ours.Value / theirs.Value;
            if (ratio > CoarseRatioBound || ratio < 1.0 / CoarseRatioBound)
            {
                failures.Add(
                    $"{name}: {stat.Canonical} ours={ours:F2} leetify={theirs:F2} is outside "
                    + $"[1/{CoarseRatioBound:F0}x, {CoarseRatioBound:F0}x], which no definitional difference explains");
            }
        }

        string tierNote = tierSupported ? "" : $"  (tier {(int)stat.Tier} UNAVAILABLE on this run)";
        string mean = pairs > 0
            ? (absoluteRelativeErrorSum / pairs).ToString("P1", CultureInfo.InvariantCulture)
            : "n/a";
        Console.WriteLine(
            $"  {stat.Canonical,-22} vs {field,-30} pairs={pairs,3} mean|rel err|={mean,7}  "
            + $"[{stat.Mode}]{tierNote}");
        perPlayer.ForEach(Console.WriteLine);
    }

    private static GoldenStatsDocument ToDocument(AimRunResult run)
    {
        Dictionary<string, PlayerStatsRecord> players = new(StringComparer.Ordinal);
        foreach (AimPlayerRow row in run.Players.Values.OrderBy(p => p.Slot))
        {
            players[row.Name] = new PlayerStatsRecord(
                row.Team, row.Slot, null, new Dictionary<string, double?>(row.Stats, StringComparer.Ordinal));
        }

        return new GoldenStatsDocument(
            GoldenStatsDocument.CurrentSchemaVersion,
            run.DemoFileName,
            run.DemoSha256,
            "expected-aim",
            CapabilityMarker(run.VisibilityAvailable),
            PinnedTimestamp,
            new MatchMetadata(run.MapName),
            players);
    }

    // The pin records whether Tier 3 was measurable, because it is a property of the MACHINE (does
    // a collision bake for this map exist here) and not of the demo. Comparing a run without
    // geometry against a pin taken with it produces a wall of divergences on columns that are
    // simply not being measured, which reads exactly like an engine regression.
    private static string CapabilityMarker(bool visibility) =>
        "aim-v1;visibility=" + (visibility ? "on" : "off");

    private static void RequireSameCapability(GoldenStatsDocument pinned, AimRunResult run)
    {
        string want = CapabilityMarker(run.VisibilityAvailable);
        if (string.Equals(pinned.ProviderVersion, want, StringComparison.Ordinal))
        {
            return;
        }

        throw new SkipTestException(
            $"The pin was taken with '{pinned.ProviderVersion}' and this run is '{want}'. "
            + "Tier 3 columns read 0.0 without baked map geometry, so the two describe different "
            + "measurements. Provide the bake for this map, or re-pin on this machine deliberately.");
    }

    private static void RequireSameDemo(GoldenStatsDocument pinned, AimRunResult run)
    {
        if (pinned.DemoSha256 is not { } expected
            || string.Equals(expected, run.DemoSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A demo from outside the repository is simply not the one the reference describes, which
        // is the same situation as not having it at all. None of these demos is committed, so this
        // is always a skip rather than a repo inconsistency.
        throw new SkipTestException(
            $"'{run.DemoFileName}' hashes to {run.DemoSha256}, but the pin was taken from {expected}. "
            + "The reference does not describe this file: supply the demo it was pinned from, or re-pin.");
    }

    private static string FixtureDirectory(string demoId)
    {
        string root = DemoTestHelper.FindRepoRoot()
                      ?? throw new SkipTestException(
                          "Repo root not located from the test assembly (looked for the solution file).");

        return Path.Combine(root, "tests", "fixtures", demoId);
    }
}
