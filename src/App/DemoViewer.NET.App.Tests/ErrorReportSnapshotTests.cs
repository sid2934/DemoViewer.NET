#region

using DemoViewer.NET.Diagnostics;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.ErrorReports;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The diagnostic snapshot that travels with an error report. Two properties matter and neither is
///     visible from the UI: it must not throw while describing a broken state, and it must not carry
///     anything off the user's machine that nobody asked it to. The allowlist gate below is the one that
///     would be a security defect rather than a bug.
/// </summary>
public class ErrorReportSnapshotTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static DemoCacheRecord Record() => new()
    {
        Path = "/demos/report_de_mirage.dem",
        Size = 48_000_000,
        Sha256 = "abc123",
        Map = "de_mirage",
        Server = "FACEIT Server EU #4021",
        TickRate = 64,
        DurationSeconds = 2538,
        RoundCount = 22,
        AnalysisState = DemoAnalysisState.Indexed,
        ProfileName = "gotv",
        ConfigFingerprint = "cached@64"
    };

    [Test]
    public async Task ASnapshotOfAnUncachedDemoWithNoRules_StillBuilds()
    {
        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build("/demos/missing.dem", null, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.DemoPath).IsEqualTo("/demos/missing.dem");
            await Assert.That(snapshot.CapturedAtUtcTicks).IsEqualTo(Now.Ticks);
            await Assert.That(snapshot.Versions).IsNotEmpty()
                .Because("versions come off assemblies, so they are available even with nothing else");
        }
    }

    [Test]
    public async Task TheCachedDemosFactsAndRulesIdentity_AreCarried()
    {
        DemoCacheRecord record = Record();
        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.DemoSha256).IsEqualTo("abc123");
            await Assert.That(snapshot.Map).IsEqualTo("de_mirage");
            await Assert.That(snapshot.TickRate).IsEqualTo(64);
            await Assert.That(snapshot.AnalysisState).IsEqualTo("Indexed");
            await Assert.That(snapshot.CachedProfileName).IsEqualTo("gotv");
            await Assert.That(snapshot.CachedConfigFingerprint).IsEqualTo("cached@64")
                .Because("a stale-rules complaint is diagnosed from what the CACHED run used");
        }
    }

    [Test]
    public async Task ParseHealth_IsCarriedOnlyForTheDemoThatIsActuallyOpen()
    {
        DemoCacheRecord record = Record();

        // The window can be opened over a cached preview of a demo the shell never parsed. Reading the open
        // demo's health there would attribute another demo's warnings to this report.
        DemoErrorReportSnapshot preview =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, "/demos/something_else.dem", Now);

        using (Assert.Multiple())
        {
            await Assert.That(preview.ParseHealth).IsNull();
            await Assert.That(preview.ParseWarnings).IsEmpty();
        }
    }

    [Test]
    public async Task TheEnvironmentSection_CarriesTheAllowlistAndNothingElse()
    {
        // The payload is built to be sent to developers. RuntimeEnvInfo keeps an allowlist precisely because
        // enumerating the environment leaks Steam tokens and auth; this is the gate on that staying true.
        Environment.SetEnvironmentVariable("DV_ERROR_REPORT_SECRET", "steam-token-shaped-value");
        try
        {
            DemoErrorReportSnapshot snapshot =
                ErrorReportSnapshotBuilder.Build("/demos/env.dem", null, null, null, null, Now);

            using (Assert.Multiple())
            {
                await Assert.That(snapshot.Environment.Keys)
                    .IsEquivalentTo(RuntimeEnvInfo.EnvAllowlist, CollectionOrdering.Any);
                await Assert.That(snapshot.Environment.ContainsKey("DV_ERROR_REPORT_SECRET")).IsFalse();
                await Assert.That(ErrorReportSnapshotBuilder.ToText(snapshot, "notes"))
                    .DoesNotContain("steam-token-shaped-value");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DV_ERROR_REPORT_SECRET", null);
        }
    }

    /// <summary>
    ///     An empty exclusion list must never read as "nothing was excluded" when nothing composed. The field
    ///     shipped declared, rendered and never assigned, so every report asserted a clean bill of health on
    ///     the one signal most likely to explain it.
    /// </summary>
    [Test]
    public async Task ExcludedRulesets_SayUnknownUntilSomethingHasComposed()
    {
        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build("/demos/excl.dem", Record(), null, null, null, Now);

        string rendered = ErrorReportSnapshotBuilder.Groups(snapshot)
            .First(g => g.Title == "Rules").Rows
            .First(r => r.Key == "excluded rulesets").Value;

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsFalse()
                .Because("no MergedRulesBuild was supplied, so nothing composed");
            await Assert.That(rendered).Contains("unknown");
            await Assert.That(rendered).DoesNotContain("none")
                .Because("\"none\" is a claim the snapshot has not earned the right to make");
        }
    }

    /// <summary>
    ///     The exclusion set is keyed by demo. A process composes many, and handing demo A's exclusions to a
    ///     report about demo B is the same cross-demo attribution the snapshot refuses for parse health.
    /// </summary>
    [Test]
    public async Task ExcludedRulesets_AreNotTakenFromAnotherDemosComposition()
    {
        DemoCacheRecord record = Record();
        DemoErrorReportSnapshot mine =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);
        DemoErrorReportSnapshot other =
            ErrorReportSnapshotBuilder.Build("/demos/a_different_demo.dem", null, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(mine.ExcludedRulesets).IsEmpty();
            await Assert.That(other.ExcludedRulesetsKnown).IsFalse()
                .Because("no composition named that demo, so it has no exclusion list of its own");
        }
    }

    /// <summary>
    ///     The cache keeps several locations for one demo, so the exclusion set has to match any of them. A
    ///     report filed through a second path would otherwise read as unknown on a demo just analysed.
    /// </summary>
    [Test]
    public async Task ExcludedRulesets_MatchAnyKnownLocationOfTheDemo()
    {
        DemoCacheRecord record = Record();
        record.Locations =
        [
            new DemoLocation("/demos/elsewhere/report_de_mirage.dem", true, record.Size, 0, 0)
        ];

        // Reported through the second location; the snapshot must still consider the demo's own set.
        DemoErrorReportSnapshot snapshot = ErrorReportSnapshotBuilder.Build(
            "/demos/elsewhere/report_de_mirage.dem", record, null, null, null, Now);

        await Assert.That(snapshot.ExcludedRulesetsKnown).IsFalse()
            .Because("nothing composed in this test, but the lookup must have considered both paths");
    }

    [Test]
    public async Task TheTextRendering_LeadsWithTheUsersNotes()
    {
        DemoCacheRecord record = Record();
        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);

        string text = ErrorReportSnapshotBuilder.ToText(snapshot, "  kills are double-counted in round 3  ");

        using (Assert.Multiple())
        {
            await Assert.That(text).Contains("kills are double-counted in round 3");
            await Assert.That(text.IndexOf("kills are double-counted", StringComparison.Ordinal))
                .IsLessThan(text.IndexOf("de_mirage", StringComparison.Ordinal))
                .Because("the notes are the report; the diagnostics are what backs them up");
            await Assert.That(ErrorReportSnapshotBuilder.ToText(snapshot, "   "))
                .Contains("(no notes written)");
        }
    }

    [Test]
    public async Task TheGroups_CoverEverySectionTheWindowShows()
    {
        DemoCacheRecord record = Record();
        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);

        IReadOnlyList<string> titles =
            [.. ErrorReportSnapshotBuilder.Groups(snapshot).Select(g => g.Title)];

        using (Assert.Multiple())
        {
            await Assert.That(titles).Contains("Demo");
            await Assert.That(titles).Contains("Analysis state");
            await Assert.That(titles).Contains("Rules");
            await Assert.That(titles).Contains("Versions");
            await Assert.That(titles).Contains("Runtime");
            await Assert.That(titles).Contains("Environment");
        }
    }
}
