#region

using System.Text.Json;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Demo Provenance Labels over synthetic cache rows: every branch of the heuristic as a fixture, the
///     same branches through the source with Team Identity resolving our side, the source kind fallback
///     for rows written before the field, the override round trip through <c>teams.json</c> and its
///     key upgrade, the hash-keyed lookups, and the Library card's chip. No demo files.
/// </summary>
[NotInParallel]
public class DemoProvenanceTests
{
    private const string ValveServer = "Valve Counter-Strike 2 eu_west Server (srcds1234)";
    private const string FaceitServer = "FACEIT.com register to play here";

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-prov-{Guid.NewGuid():N}");

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static long Day(int n) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(n).Ticks;

    // Opponents that never recur, so only the side under test clusters.
    private static int _stranger = 9000;

    private static string[] Strangers() => Ids(Interlocked.Add(ref _stranger, 5) - 4, _stranger - 3, _stranger - 2, _stranger - 1, _stranger);

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct,
        string? server = ValveServer, string? sourceKind = null, string? tClan = null, string? ctClan = null, string? sha = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = Day(day),
            Sha256 = sha,
            Map = "de_nuke",
            Server = server,
            SourceKind = sourceKind,
            TClan = tClan,
            CtClan = ctClan
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    private static (DemoCacheStore Cache, TeamIdentityService Teams, DemoProvenanceSource Source) Fresh(string? root = null)
    {
        DemoCacheStore cache = new(root is null ? null : Path.Combine(root, "cache"));
        TeamIdentityService teams = new(root, cache, run: _inline);
        return (cache, teams, new DemoProvenanceSource(cache, teams));
    }

    private static async Task Seed(DemoCacheStore cache, TeamIdentityService teams, params DemoCacheRecord[] records)
    {
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            foreach (DemoCacheRecord record in records)
            {
                cache.Upsert(record);
            }
        }

        await teams.Idle;
    }

    private static ProvenanceInputs Inputs(bool tags, DemoSourceKind kind, OurSideSource source, bool opponent) =>
        new(tags, kind, source, opponent);

    // ── 1. The heuristic, branch by branch ──────────────────────────────────────────────────────

    [Test]
    public async Task Heuristic_TagsOnBothSides_IsOfficial_WhoeverWeAre()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(true, DemoSourceKind.HltvPro, OurSideSource.None, false))).IsEqualTo("official");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(true, DemoSourceKind.GotvMatchmaking, OurSideSource.Me, false))).IsEqualTo("official")
                .Because("a tagged match our account played is still an official");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(true, DemoSourceKind.Unknown, OurSideSource.Team, true))).IsEqualTo("official")
                .Because("tags beat the us team against a named team: an official of ours is an official, not a scrim");
        }
    }

    [Test]
    public async Task Heuristic_MeResolved_OnMatchmaking_IsMatchmaking()
    {
        await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.GotvMatchmaking, OurSideSource.Me, false))).IsEqualTo("matchmaking");
    }

    [Test]
    public async Task Heuristic_MeResolved_Tagless_NotMatchmaking_IsScrim()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.Me, false))).IsEqualTo("scrim");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Faceit, OurSideSource.Me, false))).IsEqualTo("scrim");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.HltvPro, OurSideSource.Me, false))).IsEqualTo("scrim")
                .Because("an untagged broadcast our account played is a scrim until tags say otherwise");
        }
    }

    [Test]
    public async Task Heuristic_UsTeam_AgainstANamedTeam_IsOurScrim()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.Team, true))).IsEqualTo("our scrim");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.Override, true))).IsEqualTo("our scrim")
                .Because("an override naming the us team is the us team");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.GotvMatchmaking, OurSideSource.Team, true))).IsEqualTo("our scrim")
                .Because("two recurring fives that met in matchmaking are a scrim between teams");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.Me, true))).IsEqualTo("scrim")
                .Because("me on a side is not a team of ours, even against a named team");
        }
    }

    [Test]
    public async Task Heuristic_UsTeam_AgainstNobody_ReadsLikeMe()
    {
        // The owner's own roster recurs, so its demos resolve through Team rather than Me; in matchmaking
        // the opponent never recurs. Reading "me-resolved" literally would leave every one of them unlabeled.
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.GotvMatchmaking, OurSideSource.Team, false))).IsEqualTo("matchmaking");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.Team, false))).IsEqualTo("scrim");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.Override, false))).IsEqualTo("scrim");
        }
    }

    [Test]
    public async Task Heuristic_NoOurSide_NoTags_IsUnlabeled()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.GotvMatchmaking, OurSideSource.None, false))).IsNull()
                .Because("a matchmaking demo nobody of ours played says nothing about us");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.HltvPro, OurSideSource.None, false))).IsNull()
                .Because("an untagged broadcast is not called a scrim by default");
            await Assert.That(DemoProvenanceHeuristic.Default(Inputs(false, DemoSourceKind.Unknown, OurSideSource.None, true))).IsNull()
                .Because("a named team on one side without ours on the other is still nobody's scrim");
        }
    }

    // ── 2. The same branches through the source, with Team Identity resolving our side ──────────

    [Test]
    public async Task TheSource_LabelsEveryBranch_FromTheCacheRowAndTheAssignment()
    {
        (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh();
        using (teams)
        using (source)
        {
            string[] us = Ids(1, 2, 3, 4, 5);
            string[] them = Ids(11, 12, 13, 14, 15);
            await Seed(cache, teams,
                Record("/d/official.dem", 1, Ids(21, 22, 23, 24, 25), Ids(31, 32, 33, 34, 35), server: "hltv", sourceKind: "HltvPro", tClan: "FURIA", ctClan: "Vitality"),
                Record("/d/us1.dem", 2, us, them, server: FaceitServer, sourceKind: "Unknown"),
                Record("/d/us2.dem", 3, us, them, server: FaceitServer, sourceKind: "Unknown"),
                Record("/d/mm-team.dem", 4, us, Strangers(), sourceKind: "GotvMatchmaking"),
                Record("/d/mm-me.dem", 5, Ids(1, 41, 42, 43, 44), Strangers(), sourceKind: "GotvMatchmaking"),
                Record("/d/scrim-me.dem", 6, Ids(1, 51, 52, 53, 54), Strangers(), server: FaceitServer, sourceKind: "Unknown"),
                Record("/d/nobody.dem", 7, Strangers(), Strangers(), sourceKind: "GotvMatchmaking"),
                Record("/d/one-tag.dem", 8, Strangers(), Strangers(), server: "hltv", sourceKind: "HltvPro", tClan: "FURIA"));

            Guid usId = teams.TeamOnSide("/d/us1.dem", 2)!.Id;
            teams.SetUs(usId);
            teams.SetMyAccounts([Ids(1)[0]]);

            using (Assert.Multiple())
            {
                await Assert.That(source.Resolve("/d/official.dem")!.Label).IsEqualTo("official");
                await Assert.That(source.Resolve("/d/us1.dem")!.Label).IsEqualTo("our scrim").Because("the us roster against a team the library knows");
                await Assert.That(source.Resolve("/d/mm-team.dem")!.Label).IsEqualTo("matchmaking").Because("the us roster in matchmaking against strangers");
                await Assert.That(source.Resolve("/d/mm-me.dem")!.Label).IsEqualTo("matchmaking").Because("me on a side, Valve header");
                await Assert.That(source.Resolve("/d/scrim-me.dem")!.Label).IsEqualTo("scrim").Because("me on a side, no tags, not matchmaking");
                await Assert.That(source.Resolve("/d/nobody.dem")!.Label).IsNull();
                await Assert.That(source.Resolve("/d/one-tag.dem")!.Label).IsNull().Because("one tag is not both");
                await Assert.That(source.Resolve("/d/nobody.dem")!.Source).IsEqualTo(ProvenanceOrigin.None);
                await Assert.That(source.Resolve("/d/us1.dem")!.Source).IsEqualTo(ProvenanceOrigin.Heuristic);
                await Assert.That(source.Resolve("/d/missing.dem")).IsNull().Because("the cache does not know it");
            }
        }
    }

    [Test]
    public async Task TheSource_ReadsTheAssignmentSource_AndFollowsTheUsMark()
    {
        (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh();
        using (teams)
        using (source)
        {
            string[] a = Ids(1, 2, 3, 4, 5);
            string[] b = Ids(11, 12, 13, 14, 15);
            await Seed(cache, teams,
                Record("/d/m1.dem", 1, a, b, server: FaceitServer, sourceKind: "Unknown"),
                Record("/d/m2.dem", 2, a, b, server: FaceitServer, sourceKind: "Unknown"));
            int raised = 0;
            source.Changed += () => raised++;

            await Assert.That(source.Resolve("/d/m1.dem")!.Label).IsNull().Because("two teams, neither is us");

            teams.SetUs(teams.TeamOnSide("/d/m1.dem", 3)!.Id);
            using (Assert.Multiple())
            {
                await Assert.That(source.Resolve("/d/m1.dem")!.Label).IsEqualTo("our scrim");
                await Assert.That(raised).IsGreaterThan(0).Because("a team change reaches the chips");
            }

            teams.SetUs(null);
            await Assert.That(source.Resolve("/d/m1.dem")!.Label).IsNull();
        }
    }

    // ── 3. The source kind ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task SourceKind_PrefersTheStoredVerdict_AndFallsBackToTheServerName()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceSource.SourceKindOf(new DemoCacheIndexEntry { Server = FaceitServer, SourceKind = "GotvMatchmaking" }))
                .IsEqualTo(DemoSourceKind.GotvMatchmaking).Because("tier 2 saw the client name; the server name alone would not say so");
            await Assert.That(DemoProvenanceSource.SourceKindOf(new DemoCacheIndexEntry { Server = ValveServer, SourceKind = null }))
                .IsEqualTo(DemoSourceKind.GotvMatchmaking).Because("a row written before the field: the classifier's own server-name fallback");
            await Assert.That(DemoProvenanceSource.SourceKindOf(new DemoCacheIndexEntry { Server = FaceitServer, SourceKind = null }))
                .IsEqualTo(DemoSourceKind.Unknown);
            await Assert.That(DemoProvenanceSource.SourceKindOf(new DemoCacheIndexEntry { Server = null, SourceKind = "not a kind" }))
                .IsEqualTo(DemoSourceKind.Unknown);
        }
    }

    [Test]
    public async Task SourceKind_ReachesTheIndexRow_FromTheRecord()
    {
        DemoCacheRecord record = Record("/d/k.dem", 1, Ids(1, 2, 3, 4, 5), Strangers(), sourceKind: "HltvPro");
        await Assert.That(record.ToIndexEntry().SourceKind).IsEqualTo("HltvPro");
    }

    // ── 4. The override round trip ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Override_RoundTrips_ThroughTeamsJson_AndClearingRestoresTheDefault()
    {
        string root = TempRoot();
        try
        {
            (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh(root);
            await Seed(cache, teams, Record("/d/mm.dem", 1, Ids(1, 41, 42, 43, 44), Strangers(), sourceKind: "GotvMatchmaking", sha: "ab12"));
            teams.SetMyAccounts([Ids(1)[0]]);
            await Assert.That(source.Resolve("/d/mm.dem")!.Label).IsEqualTo("matchmaking");

            int raised = 0;
            source.Changed += () => raised++;
            teams.SetProvenanceOverride("/d/mm.dem", "our scrim");
            DemoProvenance pinned = source.Resolve("/d/mm.dem")!;
            using (Assert.Multiple())
            {
                await Assert.That(pinned.Label).IsEqualTo("our scrim");
                await Assert.That(pinned.Default).IsEqualTo("matchmaking").Because("the heuristic's answer is kept beside the pin");
                await Assert.That(pinned.Source).IsEqualTo(ProvenanceOrigin.Override);
                await Assert.That(pinned.IsOverride).IsTrue();
                await Assert.That(raised).IsEqualTo(1);
            }

            TeamsFile file = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(Path.Combine(root, "teams.json")), TeamsFile.JsonOptions)!;
            ProvenanceOverride stored = file.Provenance.Overrides.Single();
            using (Assert.Multiple())
            {
                await Assert.That(stored.DemoSha256).IsEqualTo("ab12").Because("user truth keys by hash when the demo has one");
                await Assert.That(stored.DemoStableKey).IsNull();
                await Assert.That(stored.Label).IsEqualTo("our scrim");
            }

            cache.SaveIndex();
            teams.Dispose();
            source.Dispose();
            (DemoCacheStore cache2, TeamIdentityService teams2, DemoProvenanceSource source2) = Fresh(root);
            using (teams2)
            using (source2)
            {
                await teams2.StartAsync();
                using (Assert.Multiple())
                {
                    await Assert.That(source2.Resolve("/d/mm.dem")!.Label).IsEqualTo("our scrim").Because("the pin survives a restart");
                    await Assert.That(source2.LabelFor("ab12")).IsEqualTo("our scrim");
                    await Assert.That(cache2.TryGetIndex("/d/mm.dem")).IsNotNull();
                }

                teams2.SetProvenanceOverride("/d/mm.dem", null);
                using (Assert.Multiple())
                {
                    await Assert.That(source2.Resolve("/d/mm.dem")!.Label).IsEqualTo("matchmaking").Because("automatic again");
                    await Assert.That(source2.Resolve("/d/mm.dem")!.IsOverride).IsFalse();
                    await Assert.That(teams2.ProvenanceOverrides).IsEmpty();
                }

                TeamsFile after = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(Path.Combine(root, "teams.json")), TeamsFile.JsonOptions)!;
                await Assert.That(after.Provenance.Overrides).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Override_OnAnUnhashedDemo_KeysByStableKey_AndUpgradesToTheHash()
    {
        (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh();
        using (teams)
        using (source)
        {
            await Seed(cache, teams, Record("/d/u.dem", 1, Ids(1, 2, 3, 4, 5), Strangers(), sourceKind: "GotvMatchmaking"));
            teams.SetProvenanceOverride("/d/u.dem", "official");
            ProvenanceOverride stored = teams.ProvenanceOverrides.Single();
            using (Assert.Multiple())
            {
                await Assert.That(stored.DemoSha256).IsNull();
                await Assert.That(stored.DemoStableKey).IsEqualTo(DemoCacheStore.StableKey("/d/u.dem"));
                await Assert.That(source.Resolve("/d/u.dem")!.Label).IsEqualTo("official");
            }

            // Tier 2 hashes the demo later: the pin moves onto the hash on the next replay.
            cache.Upsert(Record("/d/u.dem", 1, Ids(1, 2, 3, 4, 5), Ids(61, 62, 63, 64, 65), sourceKind: "GotvMatchmaking", sha: "cd34"));
            await teams.Idle;
            stored = teams.ProvenanceOverrides.Single();
            using (Assert.Multiple())
            {
                await Assert.That(stored.DemoSha256).IsEqualTo("cd34");
                await Assert.That(stored.DemoStableKey).IsNull();
                await Assert.That(source.Resolve("/d/u.dem")!.Label).IsEqualTo("official");
                await Assert.That(source.LabelFor("cd34")).IsEqualTo("official");
            }
        }
    }

    [Test]
    public async Task Override_RefusesAWordOutsideTheVocabulary()
    {
        (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh();
        using (teams)
        using (source)
        {
            await Seed(cache, teams, Record("/d/v.dem", 1, Ids(1, 2, 3, 4, 5), Strangers()));
            Assert.Throws<ArgumentException>(() => teams.SetProvenanceOverride("/d/v.dem", "Official"));
            await Assert.That(teams.ProvenanceOverrides).IsEmpty();
        }
    }

    // ── 5. The hash-keyed lookups ───────────────────────────────────────────────────────────────

    [Test]
    public async Task LabelFor_AndTheBatch_AnswerByHash_AndKeepAPinAfterTheDemoLeaves()
    {
        (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh();
        using (teams)
        using (source)
        {
            await Seed(cache, teams,
                Record("/d/a.dem", 1, Ids(1, 41, 42, 43, 44), Strangers(), sourceKind: "GotvMatchmaking", sha: "aaaa"),
                Record("/d/b.dem", 2, Strangers(), Strangers(), sourceKind: "GotvMatchmaking", sha: "bbbb"),
                Record("/d/c.dem", 3, Ids(21, 22, 23, 24, 25), Ids(31, 32, 33, 34, 35), server: "hltv", sourceKind: "HltvPro", tClan: "NaVi", ctClan: "G2", sha: "cccc"));
            teams.SetMyAccounts([Ids(1)[0]]);
            teams.SetProvenanceOverride("/d/b.dem", "scrim");

            IReadOnlyDictionary<string, string?> batch = source.LabelsFor(["aaaa", "bbbb", "cccc", "dddd", "aaaa", ""]);
            using (Assert.Multiple())
            {
                await Assert.That(source.LabelFor("aaaa")).IsEqualTo("matchmaking");
                await Assert.That(source.LabelFor("bbbb")).IsEqualTo("scrim");
                await Assert.That(source.LabelFor("cccc")).IsEqualTo("official");
                await Assert.That(source.LabelFor("dddd")).IsNull().Because("unknown hash");
                await Assert.That(source.LabelFor("")).IsNull();
                await Assert.That(batch.Count).IsEqualTo(4).Because("one entry per distinct non-empty hash");
                await Assert.That(batch["aaaa"]).IsEqualTo("matchmaking");
                await Assert.That(batch["bbbb"]).IsEqualTo("scrim");
                await Assert.That(batch["cccc"]).IsEqualTo("official");
                await Assert.That(batch["dddd"]).IsNull();
            }

            cache.Remove("/d/b.dem");
            await teams.Idle;
            using (Assert.Multiple())
            {
                await Assert.That(source.Resolve("/d/b.dem")).IsNull().Because("the cache forgot the file");
                await Assert.That(source.LabelFor("bbbb")).IsEqualTo("scrim").Because("the pin is user truth on the hash, not on the file");
                await Assert.That(source.LabelsFor(["bbbb"])["bbbb"]).IsEqualTo("scrim");
            }
        }
    }

    // ── 6. The Library card ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheLibraryCard_ShowsTheLabel_AndPinsThroughTheChip()
    {
        (DemoCacheStore cache, TeamIdentityService teams, DemoProvenanceSource source) = Fresh();
        using DemoLibraryService library = new(a => a(), Path.Combine(Path.GetTempPath(), "dvlibprov_" + Guid.NewGuid().ToString("N") + ".json"));
        using (teams)
        using (source)
        {
            await Seed(cache, teams,
                Record("/d/mm.dem", 1, Ids(1, 41, 42, 43, 44), Strangers(), sourceKind: "GotvMatchmaking", sha: "ab12"),
                Record("/d/nobody.dem", 2, Strangers(), Strangers(), sourceKind: "GotvMatchmaking"));
            teams.SetMyAccounts([Ids(1)[0]]);
            foreach (string path in (string[]) ["/d/mm.dem", "/d/nobody.dem", "/d/unknown.dem"])
            {
                library.Entries.Add(new DemoEntry
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Directory = "/d",
                    FileSizeBytes = 1000,
                    Modified = new DateTime(2026, 3, 1),
                    State = DemoIndexState.Indexed
                });
            }

            LibraryTabViewModel vm = new(library, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]),
                teams: teams, provenance: source);
            DemoEntry mm = library.Entries[0];
            DemoEntry nobody = library.Entries[1];
            DemoEntry unknown = library.Entries[2];
            using (Assert.Multiple())
            {
                await Assert.That(vm.HasProvenance).IsTrue();
                await Assert.That(mm.ProvenanceLabel).IsEqualTo("matchmaking");
                await Assert.That(mm.ProvenanceDisplay).IsEqualTo("matchmaking");
                await Assert.That(mm.ProvenanceIsOverride).IsFalse();
                await Assert.That(nobody.ProvenanceLabel).IsNull();
                await Assert.That(nobody.ProvenanceDisplay).IsEqualTo("unlabeled");
                await Assert.That(unknown.ProvenanceLabel).IsNull().Because("not in the cache yet");
            }

            vm.SetProvenance(mm, "our scrim");
            using (Assert.Multiple())
            {
                await Assert.That(mm.ProvenanceLabel).IsEqualTo("our scrim").Because("the chip re-reads on the source's Changed");
                await Assert.That(mm.ProvenanceIsOverride).IsTrue();
                await Assert.That(mm.ProvenanceTooltip).Contains("set by you");
            }

            vm.SetProvenance(mm, null);
            using (Assert.Multiple())
            {
                await Assert.That(mm.ProvenanceLabel).IsEqualTo("matchmaking");
                await Assert.That(mm.ProvenanceIsOverride).IsFalse();
                await Assert.That(mm.ProvenanceTooltip).Contains("automatic");
            }

            LibraryTabViewModel bare = new(library, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]));
            await Assert.That(bare.HasProvenance).IsFalse().Because("no source, no chip");
        }
    }

    [Test]
    public async Task TheVocabulary_IsTheFourValues()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoProvenanceLabel.All).IsEquivalentTo(["official", "scrim", "our scrim", "matchmaking"]);
            await Assert.That(DemoProvenanceLabel.IsKnown("our scrim")).IsTrue();
            await Assert.That(DemoProvenanceLabel.IsKnown("Official")).IsFalse();
            await Assert.That(DemoProvenanceLabel.IsKnown("unlabeled")).IsFalse().Because("unlabeled is the absence of a value");
        }
    }
}
