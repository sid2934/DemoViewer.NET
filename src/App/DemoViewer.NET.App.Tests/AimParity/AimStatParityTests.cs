#region

using CS2DemoKit.Analysis.GoldenStats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     The aim board's regression gate: runs <c>rules/aim_rating.rules.yaml</c> over each benchmark
///     demo and compares every column against a pinned reference at zero tolerance.
///     <para>
///         <b>Why zero tolerance.</b> The pinned
///         <c>tests/fixtures/&lt;demo-id&gt;/aim.expected.golden.json</c> says the engine still
///         produces what it produced when a human pinned it, and two runs over the same bytes are
///         deterministic, so any drift at all is a finding rather than noise.
///     </para>
///     <para>
///         <b>Never edit the pin to absorb a diff.</b> Fix the engine, or hand-verify the value and
///         re-pin deliberately, recording it by moving the file's <c>provider_version</c> off the
///         machine-generated marker. <c>tests/fixtures/README.md</c> carries the posture in full.
///     </para>
///     <para>
///         <b>Skips are the honest answer.</b> The benchmark demos are gitignored, so on a checkout
///         without them these cases skip rather than passing on nothing.
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

            foreach (AimStatDefinition stat in AimStatCatalogue.Stats)
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

    /// <summary>One case per demo in the benchmark fixture set.</summary>
    /// <returns>The demo ids.</returns>
    public static IEnumerable<string> DemoIds() => LiveAimStats.BenchmarkDemoIds();

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
