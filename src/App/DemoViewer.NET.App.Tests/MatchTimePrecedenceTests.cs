#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.MatchMeta;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Which match time a demo gets: its <c>.dem.info</c> when one is beside it, else the file's write time
///     flagged approximate; a time captured from an info file survives the demo moving away from it; rows sort
///     newest match first with the reservation id as the tie-break. The file system is faked: no demo or info
///     file is touched.
/// </summary>
public class MatchTimePrecedenceTests
{
    private const string Sha = "sha-matchmeta";
    private static readonly string Replays = Path.Combine(Path.GetTempPath(), "dv-replays");
    private static readonly string Library = Path.Combine(Path.GetTempPath(), "dv-library");
    private static readonly DateTime Played = new(2026, 6, 17, 23, 17, 26, DateTimeKind.Utc);
    private static readonly DateTime Downloaded = new(2026, 6, 18, 8, 0, 0, DateTimeKind.Utc);

    private static DemoCacheRecord Record(string path, string? sha = Sha) => new()
    {
        Path = path,
        Size = 100,
        ModifiedTicks = Downloaded.ToLocalTime().Ticks,
        Sha256 = sha
    };

    private sealed class Disk
    {
        public Dictionary<string, byte[]> Infos { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> Demos { get; } = new(StringComparer.OrdinalIgnoreCase);

        public MatchMetaPass Pass(DemoCacheStore store) =>
            new(store, p => Infos.GetValueOrDefault(p), p => Demos.TryGetValue(p, out DateTime t) ? t : null);
    }

    private static void Run(DemoCacheStore store, MatchMetaPass pass, string path)
    {
        DemoCacheRecord record = store.TryLoadRecord(path, false)!;
        if (store.TryGetIndex(path) is { } row && pass.Wants(row))
        {
            pass.Run(record, CancellationToken.None);
        }
    }

    [Test]
    public async Task TheInfoFile_WinsOverTheWriteTime()
    {
        DemoCacheStore store = new(null);
        Disk disk = new();
        string demo = Path.Combine(Replays, MatchInfoReaderTests.CommittedName);
        disk.Infos[demo + ".info"] = MatchInfoReaderTests.CommittedInfo();
        disk.Demos[demo] = Downloaded;
        store.Upsert(Record(demo));

        Run(store, disk.Pass(store), demo);

        DemoMatchMeta meta = store.TryLoadRecord(demo, false)!.MatchMeta!;
        DemoCacheIndexEntry row = store.TryGetIndex(demo)!;
        using (Assert.Multiple())
        {
            await Assert.That(meta.MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo);
            await Assert.That(meta.MatchTimeUtcTicks).IsEqualTo(Played.Ticks);
            await Assert.That(meta.IsApproximate).IsFalse();
            await Assert.That(meta.ReservationId).IsEqualTo("3826256877184877003");
            await Assert.That(meta.OutcomeId).IsEqualTo(981591541);
            await Assert.That(meta.Token).IsEqualTo(410);
            await Assert.That(row.MatchTimeUtcTicks).IsEqualTo(Played.Ticks).Because("the row mirrors the record");
            await Assert.That(row.MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo);
            await Assert.That(row.IsPackCurrent(MatchMetaPass.StampId, DemoMatchMeta.CurrentSchema, MatchInfoReader.Version)).IsTrue();
            await Assert.That(new MatchMetaPass(store).Wants(row)).IsFalse().Because("a stamped demo is not read again");
        }
    }

    [Test]
    public async Task NoInfoFile_TakesTheWriteTime_FlaggedApproximate()
    {
        DemoCacheStore store = new(null);
        Disk disk = new();
        string demo = Path.Combine(Library, "faceit-scrim.dem");
        disk.Demos[demo] = Downloaded;
        store.Upsert(Record(demo));

        Run(store, disk.Pass(store), demo);

        DemoMatchMeta meta = store.TryLoadRecord(demo, false)!.MatchMeta!;
        using (Assert.Multiple())
        {
            await Assert.That(meta.MatchTimeSource).IsEqualTo(MatchTimeSource.FileWriteTime);
            await Assert.That(meta.MatchTimeUtcTicks).IsEqualTo(Downloaded.Ticks);
            await Assert.That(meta.IsApproximate).IsTrue();
            await Assert.That(meta.ReservationId).IsNull().Because("the name is not a Valve name");
        }
    }

    [Test]
    public async Task AGoneFile_FallsBackToTheRecordsWriteTime_AndAMalformedInfoIsNoInfo()
    {
        DemoCacheStore store = new(null);
        Disk disk = new();
        string demo = Path.Combine(Replays, MatchInfoReaderTests.CommittedName);
        disk.Infos[demo + ".info"] = [0x10, 0x80];
        store.Upsert(Record(demo));

        Run(store, disk.Pass(store), demo);

        DemoMatchMeta meta = store.TryLoadRecord(demo, false)!.MatchMeta!;
        using (Assert.Multiple())
        {
            await Assert.That(meta.MatchTimeSource).IsEqualTo(MatchTimeSource.FileWriteTime);
            await Assert.That(meta.MatchTimeUtcTicks).IsEqualTo(Downloaded.Ticks).Because("the record's write time is local ticks");
            await Assert.That(meta.ReservationId).IsEqualTo("3826256877184877003").Because("the name still carries it");
        }
    }

    [Test]
    public async Task ADemoMovedAwayFromItsInfo_KeepsTheCapturedTime()
    {
        DemoCacheStore store = new(null);
        Disk disk = new();
        MatchMetaPass pass = disk.Pass(store);
        string original = Path.Combine(Replays, MatchInfoReaderTests.CommittedName);
        string moved = Path.Combine(Library, "renamed.dem");
        disk.Infos[original + ".info"] = MatchInfoReaderTests.CommittedInfo();
        store.Upsert(Record(original));
        Run(store, pass, original);

        disk.Infos.Clear();
        disk.Demos[moved] = DateTime.UtcNow;
        store.Upsert(Record(moved));
        store.Remove(original);
        // A reader change re-runs the pass everywhere: the info file is gone by then.
        store.UpdateExisting(moved, r => r.PackStamps.RemoveAll(s => s.Id == MatchMetaPass.StampId));
        Run(store, pass, moved);

        DemoMatchMeta meta = store.TryLoadRecord(moved, false)!.MatchMeta!;
        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetIndex(original)).IsNull();
            await Assert.That(store.TryGetIndex(moved)!.Stamp(MatchMetaPass.StampId)).IsNotNull().Because("the pass ran again");
            await Assert.That(meta.MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo);
            await Assert.That(meta.MatchTimeUtcTicks).IsEqualTo(Played.Ticks);
            await Assert.That(meta.ReservationId).IsEqualTo("3826256877184877003").Because("the new name is not a Valve name");
            await Assert.That(store.TryGetIndex(moved)!.MatchTimeUtcTicks).IsEqualTo(Played.Ticks);
        }
    }

    [Test]
    public async Task Resolve_TakesTheFirstSourcePresent()
    {
        MatchInfo info = new(Played, 7, [], null);
        DemoMatchMeta written = MatchMetaPass.Resolve(null, null, null, Downloaded);
        DemoMatchMeta reserved = new() { MatchTimeUtcTicks = 5, MatchTimeSource = MatchTimeSource.UserOverride };
        using (Assert.Multiple())
        {
            await Assert.That(MatchMetaPass.Resolve(null, null, null, null).MatchTimeSource).IsEqualTo(MatchTimeSource.Unknown);
            await Assert.That(MatchMetaPass.Resolve(null, null, null, null).MatchTimeUtcTicks).IsNull();
            await Assert.That(MatchMetaPass.Resolve(written, null, info, Downloaded).MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo)
                .Because("an info file found later beats the write time");
            await Assert.That(MatchMetaPass.Resolve(written, null, null, null).MatchTimeUtcTicks).IsEqualTo(Downloaded.Ticks)
                .Because("a file gone since keeps the time it was seen with");
            await Assert.That(MatchMetaPass.Resolve(reserved, null, info, Downloaded).MatchTimeUtcTicks).IsEqualTo(5)
                .Because("a reserved higher source is never overwritten");
            await Assert.That(MatchMetaPass.Resolve(null, (9, 1, 2), info, null).ReservationId).IsEqualTo("7")
                .Because("the info file's reservation id wins over the name's");
        }
    }

    [Test]
    public async Task Rows_SortNewestMatchFirst_ReservationIdBreaksTies()
    {
        long day = TimeSpan.TicksPerDay;
        DemoCacheIndexEntry Row(string path, long? time, string? reservation, long modified = 0) =>
            new() { Path = path, MatchTimeUtcTicks = time, ReservationId = reservation, ModifiedTicks = modified };
        List<DemoCacheIndexEntry> rows =
        [
            Row("/a.dem", 10 * day, "3826256877184877003"),
            Row("/b.dem", 10 * day, "998"),
            Row("/c.dem", 12 * day, null),
            Row("/d.dem", null, null, 11 * day),
            Row("/e.dem", 10 * day, "3826256877184877004"),
            Row("/f.dem", 10 * day, "998")
        ];

        List<string> order = [.. rows.Order(NewestMatchFirst.Instance).Select(r => r.Path)];
        rows.Reverse();
        List<string> reversed = [.. rows.Order(NewestMatchFirst.Instance).Select(r => r.Path)];
        using (Assert.Multiple())
        {
            await Assert.That(order).IsEquivalentTo(["/c.dem", "/d.dem", "/e.dem", "/a.dem", "/b.dem", "/f.dem"], CollectionOrdering.Matching)
                .Because("newest first; a row with no match time sorts by its write time; the higher reservation id first; then the path");
            await Assert.That(reversed).IsEquivalentTo(order, CollectionOrdering.Matching).Because("the order does not depend on arrival");
        }
    }

    [Test]
    public async Task TheRunner_StampsEveryDemoOnce()
    {
        DemoCacheStore store = new(null);
        Disk disk = new();
        string valve = Path.Combine(Replays, MatchInfoReaderTests.CommittedName);
        string other = Path.Combine(Library, "other.dem");
        disk.Infos[valve + ".info"] = MatchInfoReaderTests.CommittedInfo();
        store.Upsert(Record(valve));
        store.Upsert(Record(other, "sha-other"));
        using RecordPassRunner runner = new(() => [disk.Pass(store)], store, null);

        runner.RecheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Contents.Any(r => r.MatchTimeSource is null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        List<string> order = [.. store.Contents.Order(NewestMatchFirst.Instance).Select(r => r.Path)];
        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetIndex(valve)!.MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo);
            await Assert.That(store.TryGetIndex(other)!.MatchTimeSource).IsEqualTo(MatchTimeSource.FileWriteTime);
            await Assert.That(order).IsEquivalentTo([other, valve], CollectionOrdering.Matching).Because("downloaded after it was played");
        }
    }
}
