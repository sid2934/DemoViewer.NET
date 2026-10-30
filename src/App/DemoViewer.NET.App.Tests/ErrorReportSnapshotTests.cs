#region

using DemoViewer.NET.Diagnostics;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.ErrorReports;
using DemoViewer.NET.ViewModels.Diagnostics;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The diagnostic snapshot that travels with an error report. Two properties matter and neither is
///     visible from the UI: it must not throw while describing a broken state, and it must not carry
///     anything off the user's machine that nobody asked it to. The allowlist gate below is the one that
///     would be a security defect rather than a bug.
/// </summary>
// Keyless, not keyed: the remembered exclusion set is one process-wide slot that every analysis in the
// assembly writes, and a keyed exclusion still runs beside the unannotated tests that compose.
[NotInParallel]
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

                // DEMO_SOURCES_TOKEN is a live credential on a developer machine, so it is READ and never
                // written: another test in this process may be using it. Unset in CI, where the assertion
                // above carries the gate.
                if (Environment.GetEnvironmentVariable("DEMO_SOURCES_TOKEN") is { Length: > 0 } live)
                {
                    await Assert.That(ErrorReportSnapshotBuilder.ToText(snapshot, "notes"))
                        .DoesNotContain(live);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DV_ERROR_REPORT_SECRET", null);
        }
    }

    /// <summary>
    ///     The gate above asserts the snapshot's keys ARE the allowlist, which would pass a secret somebody
    ///     added to the allowlist itself. This is the gate on the list's own contents, and it is the one that
    ///     holds when the next runtime knob gets added next to a token.
    /// </summary>
    [Test]
    public async Task TheEnvironmentAllowlist_NamesNothingShapedLikeACredential()
    {
        string[] shapes =
            ["TOKEN", "SECRET", "PASS", "KEY", "AUTH", "CRED", "COOKIE", "SESSION", "STEAM", "LICENSE"];
        List<string> suspect =
        [
            .. RuntimeEnvInfo.EnvAllowlist.Where(name =>
                shapes.Any(shape => name.Replace("_", "", StringComparison.Ordinal)
                    .Contains(shape, StringComparison.OrdinalIgnoreCase)))
        ];

        Console.WriteLine($"[error-report] allowlist entries: {RuntimeEnvInfo.EnvAllowlist.Length}");

        using (Assert.Multiple())
        {
            await Assert.That(suspect).IsEmpty()
                .Because("these reports are built to be sent to developers, and the allowlist is the only "
                         + "thing between them and a credential");
            await Assert.That(RuntimeEnvInfo.EnvAllowlist).DoesNotContain("PATH")
                .Because("the whole search path names every tool the user has installed");
        }
    }

    // The one row the three-state semantics are read off.
    private static string ExclusionRow(DemoErrorReportSnapshot snapshot) =>
        ErrorReportSnapshotBuilder.Groups(snapshot)
            .First(g => g.Title == "Rules").Rows
            .First(r => r.Key == "excluded rulesets").Value;

    /// <summary>
    ///     An empty exclusion list must never read as "nothing was excluded" when nothing composed. The field
    ///     shipped declared, rendered and never assigned, so every report asserted a clean bill of health on
    ///     the one signal most likely to explain it.
    /// </summary>
    [Test]
    public async Task ExcludedRulesets_SayUnknownUntilSomethingHasComposed()
    {
        RulesetExclusionReport.ForgetForTest();

        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build("/demos/excl.dem", Record(), null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsFalse()
                .Because("nothing has composed, so the list is not a statement about anything");
            await Assert.That(ExclusionRow(snapshot)).Contains("unknown");
            await Assert.That(ExclusionRow(snapshot)).DoesNotContain("none")
                .Because("\"none\" is a claim the snapshot has not earned the right to make");
        }
    }

    /// <summary>
    ///     The other side of the same field, and the one that was broken: a composition of this demo that
    ///     dropped nothing is the only thing entitled to read as "none". Without the known flag being
    ///     ASSIGNED, this case and the one above are indistinguishable.
    /// </summary>
    [Test]
    public async Task ExcludedRulesets_ReadAsNoneOnlyAfterACompositionOfThisDemoDroppedNothing()
    {
        DemoCacheRecord record = Record();
        RulesetExclusionReport.ForgetForTest();
        RulesetExclusionReport.Remember(record.Path, []);

        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsTrue();
            await Assert.That(snapshot.ExcludedRulesets).IsEmpty();
            await Assert.That(ExclusionRow(snapshot)).IsEqualTo("none");
        }
    }

    /// <summary>What a composition of this demo actually dropped reaches the report by name.</summary>
    [Test]
    public async Task ExcludedRulesets_AreCarriedByName()
    {
        DemoCacheRecord record = Record();
        RulesetExclusionReport.ForgetForTest();
        RulesetExclusionReport.Remember(record.Path,
            ["ruleset 'community.entry-trades' was excluded: [RS201] unresolvable column"]);

        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsTrue();
            await Assert.That(snapshot.ExcludedRulesets).Count().IsEqualTo(1);
            await Assert.That(ExclusionRow(snapshot)).Contains("community.entry-trades");
        }
    }

    /// <summary>
    ///     The exclusion set is keyed by demo. A process composes many, and handing demo A's exclusions to a
    ///     report about demo B is the same cross-demo attribution the snapshot refuses for parse health.
    /// </summary>
    [Test]
    public async Task ExcludedRulesets_AreNotTakenFromAnotherDemosComposition()
    {
        RulesetExclusionReport.ForgetForTest();
        RulesetExclusionReport.Remember("/demos/some_other_demo.dem",
            ["ruleset 'community.utility-value' was excluded: [RS201] unresolvable column"]);

        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build("/demos/the_reported_demo.dem", null, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsFalse()
                .Because("no composition named this demo, so it has no exclusion list of its own");
            await Assert.That(snapshot.ExcludedRulesets).IsEmpty();
            await Assert.That(ExclusionRow(snapshot)).DoesNotContain("community.utility-value");
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

        // Composed under the path the pipeline held; reported through the second location.
        RulesetExclusionReport.ForgetForTest();
        RulesetExclusionReport.Remember(record.Locations[0].Path,
            ["ruleset 'community.clutch' was excluded: [RS201] unresolvable column"]);

        DemoErrorReportSnapshot snapshot = ErrorReportSnapshotBuilder.Build(
            record.Path, record, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsTrue()
                .Because("the lookup matches every path the record lists, not only the one it was asked for");
            await Assert.That(ExclusionRow(snapshot)).Contains("community.clutch");
        }
    }

    /// <summary>
    ///     A bare cut composes with no demo to name, and one runs routinely right after an analysis. It must
    ///     leave the slot alone: clearing it would throw away the set the reported demo just produced.
    /// </summary>
    [Test]
    public async Task ABareCutWithNoDemoToName_DoesNotThrowAwayTheReportedDemosExclusions()
    {
        DemoCacheRecord record = Record();
        RulesetExclusionReport.ForgetForTest();
        RulesetExclusionReport.Remember(record.Path,
            ["ruleset 'community.entry-trades' was excluded: [RS201] unresolvable column"]);
        RulesetExclusionReport.Remember(null, []);

        DemoErrorReportSnapshot snapshot =
            ErrorReportSnapshotBuilder.Build(record.Path, record, null, null, null, Now);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.ExcludedRulesetsKnown).IsTrue();
            await Assert.That(ExclusionRow(snapshot)).Contains("community.entry-trades");
        }
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
