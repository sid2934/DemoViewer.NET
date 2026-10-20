#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.MatchOverview;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The user's match time: set on a demo it wins over every other source, survives the pass running
///     again and a second path of the same bytes joining the row, and clearing it dates the demo from its own
///     sources again. No demo or info file is touched.
/// </summary>
public class MatchTimeOverrideTests
{
    private const string Sha = "sha-override";
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "dv-override");
    private static readonly DateTime Downloaded = new(2026, 6, 18, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Chosen = new(2026, 5, 2, 19, 30, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static DemoCacheRecord Record(string path, string? sha = Sha) => new()
    {
        Path = path,
        Size = 100,
        ModifiedTicks = Downloaded.ToLocalTime().Ticks,
        Sha256 = sha
    };

    [Test]
    public async Task ASetTime_WinsOverTheWriteTime_AndTheRowMirrorsIt()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "a.dem");
        store.Upsert(Record(demo));

        MatchTimeOverrides.Set(store, demo, Chosen);

        DemoMatchMeta meta = store.TryLoadRecord(demo, false)!.MatchMeta!;
        DemoCacheIndexEntry row = store.TryGetIndex(demo)!;
        using (Assert.Multiple())
        {
            await Assert.That(meta.MatchTimeSource).IsEqualTo(MatchTimeSource.UserOverride);
            await Assert.That(meta.MatchTimeUtcTicks).IsEqualTo(Chosen.Ticks);
            await Assert.That(meta.IsApproximate).IsFalse();
            await Assert.That(row.MatchTimeSource).IsEqualTo(MatchTimeSource.UserOverride);
            await Assert.That(row.MatchTimeUtcTicks).IsEqualTo(Chosen.Ticks);
        }
    }

    [Test]
    public async Task ThePassRunningAfterwards_KeepsTheSetTime()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "b.dem");
        store.Upsert(Record(demo));
        MatchTimeOverrides.Set(store, demo, Chosen);

        new MatchMetaPass(store, _ => null, _ => Downloaded).Run(store.TryLoadRecord(demo, false)!, CancellationToken.None);

        DemoMatchMeta meta = store.TryLoadRecord(demo, false)!.MatchMeta!;
        using (Assert.Multiple())
        {
            await Assert.That(meta.MatchTimeSource).IsEqualTo(MatchTimeSource.UserOverride);
            await Assert.That(meta.MatchTimeUtcTicks).IsEqualTo(Chosen.Ticks);
        }
    }

    [Test]
    public async Task ASecondPathOfTheSameBytes_ReadsTheSetTime()
    {
        DemoCacheStore store = new(null);
        string first = Path.Combine(Dir, "c1.dem");
        string second = Path.Combine(Dir, "c2.dem");
        store.Upsert(Record(first));
        MatchTimeOverrides.Set(store, first, Chosen);
        DemoCacheRecord copy = Record(second);
        copy.MatchMeta = MatchMetaPass.Resolve(null, null, null, Downloaded);

        store.Upsert(copy);

        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetIndex(second)!.MatchTimeSource).IsEqualTo(MatchTimeSource.UserOverride);
            await Assert.That(store.TryGetIndex(second)!.MatchTimeUtcTicks).IsEqualTo(Chosen.Ticks);
        }
    }

    [Test]
    public async Task Clear_DatesTheDemoFromItsOwnSourcesAgain()
    {
        DemoCacheStore store = new(null);
        string demo = Path.Combine(Dir, "d.dem");
        store.Upsert(Record(demo));
        MatchTimeOverrides.Set(store, demo, Chosen);

        MatchTimeOverrides.Clear(store, demo);

        DemoMatchMeta meta = store.TryLoadRecord(demo, false)!.MatchMeta!;
        using (Assert.Multiple())
        {
            await Assert.That(meta.MatchTimeSource).IsNotEqualTo(MatchTimeSource.UserOverride);
            await Assert.That(meta.MatchTimeUtcTicks).IsNotEqualTo(Chosen.Ticks);
            await Assert.That(store.TryGetIndex(demo)!.MatchTimeSource).IsEqualTo(meta.MatchTimeSource);
        }
    }

    [Test]
    public async Task ASetTime_ReordersTheHistory()
    {
        DemoCacheStore store = new(null);
        string older = Path.Combine(Dir, "e1.dem");
        string newer = Path.Combine(Dir, "e2.dem");
        DemoCacheRecord a = Record(older, "sha-e1");
        a.MatchMeta = MatchMetaPass.Resolve(null, null, null, Downloaded.AddDays(-5));
        DemoCacheRecord b = Record(newer, "sha-e2");
        b.MatchMeta = MatchMetaPass.Resolve(null, null, null, Downloaded);
        store.Upsert(a);
        store.Upsert(b);

        MatchTimeOverrides.Set(store, older, Downloaded.AddDays(1));

        List<DemoCacheIndexEntry> rows = [store.TryGetIndex(newer)!, store.TryGetIndex(older)!];
        rows.Sort(NewestMatchFirst.Instance);
        await Assert.That(rows[0].Path).IsEqualTo(older);
    }

    [Test]
    public async Task TheHeroField_ParsesLocalTime_AndRejectsGarbage()
    {
        DateTime local = new(2026, 5, 2, 21, 30, 0, DateTimeKind.Local);
        using (Assert.Multiple())
        {
            await Assert.That(MatchTimeOverrides.TryParse("2026-05-02 21:30", out DateTime utc)).IsTrue();
            await Assert.That(utc).IsEqualTo(local.ToUniversalTime());
            await Assert.That(MatchTimeOverrides.Format(utc.Ticks)).IsEqualTo("2026-05-02 21:30");
            await Assert.That(MatchTimeOverrides.TryParse("last tuesday", out _)).IsFalse();
        }
    }

    [Test]
    public async Task TheHero_SetsAndClearsThroughItsCallback()
    {
        List<(string Key, DateTime? Utc)> calls = [];
        MatchOverviewTabViewModel tab = new(setMatchTime: (key, utc) => calls.Add((key, utc)));
        tab.BeginOpening("x.dem", null, null, "/d/x.dem");
        tab.MatchDateInput = "nope";
        tab.SetMatchDateCommand.Execute(null);
        string rejected = tab.MatchDateError;
        tab.MatchDateInput = "2026-05-02 21:30";
        tab.SetMatchDateCommand.Execute(null);
        tab.ClearMatchDateCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(tab.CanSetMatchDate).IsTrue();
            await Assert.That(rejected).IsNotEmpty();
            await Assert.That(calls.Count).IsEqualTo(2);
            await Assert.That(calls[0].Key).IsEqualTo("/d/x.dem");
            await Assert.That(calls[0].Utc).IsNotNull();
            await Assert.That(calls[1].Utc).IsNull();
        }
    }

    [Test]
    public async Task TheHero_ShowsTheTimeItIsGiven()
    {
        MatchOverviewTabViewModel tab = new(setMatchTime: (_, _) => { });
        tab.SetMatchTime(new DemoMatchMeta { MatchTimeUtcTicks = Chosen.Ticks, MatchTimeSource = MatchTimeSource.UserOverride });
        string set = tab.MatchDateButtonText;
        bool overridden = tab.HasMatchDateOverride;
        tab.SetMatchTime(MatchMetaPass.Resolve(null, null, null, Downloaded));
        string guess = tab.MatchDateButtonText;
        tab.SetMatchTime(null);

        using (Assert.Multiple())
        {
            await Assert.That(set).IsEqualTo(MatchTimeOverrides.Format(Chosen.Ticks));
            await Assert.That(overridden).IsTrue();
            await Assert.That(guess).StartsWith("~ ");
            await Assert.That(tab.MatchDateButtonText).IsEqualTo("Set match date");
        }
    }

    [Test]
    public async Task V1Schema_MatchesTheCheckedInOverrideSample()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        DemoMatchMeta meta = MatchMetaPass.Resolve(
            new DemoMatchMeta
            {
                MatchTimeUtcTicks = new DateTime(2026, 6, 18, 20, 0, 0, DateTimeKind.Utc).Ticks,
                MatchTimeSource = MatchTimeSource.UserOverride
            },
            MatchMetaPass.ParseName(MatchInfoReaderTests.CommittedName), MatchInfoReader.Read(MatchInfoReaderTests.CommittedInfo()), Downloaded);
        string json = JsonSerializer.Serialize(meta, Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        string path = Path.Combine(repo, "tests", "fixtures", "match-meta", "match-meta.override.sample.json");
        if (Environment.GetEnvironmentVariable("MATCH_META_GOLDEN_UPDATE") == "1")
        {
            File.WriteAllText(path, json);
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException("missing the override sample; regenerate with MATCH_META_GOLDEN_UPDATE=1");
        }

        string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        using (Assert.Multiple())
        {
            await Assert.That(json).IsEqualTo(committed);
            await Assert.That(committed).Contains("\"MatchTimeSource\": \"UserOverride\"");
        }
    }
}
