#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Synthetic inputs for the Suggested Tags review tests: two live rounds on the detector tests' made-up
///     map, a walk with an execute in the first and a default in the second (see <see cref="Walk" />),
///     Round Facts rows seated by hand, and a harness wiring the service over in-memory stores. No demo
///     file anywhere; the real-demo variant is its own class and waits on CS2DemoKit #54.
/// </summary>
internal sealed class SuggestedTagsReviewHarness : IDisposable
{
    internal const string DemoPath = "/d/review.dem";
    internal const int FreezeEnd = 1000;
    internal const int Seconds = 90;
    internal const int SecondFreezeEnd = FreezeEnd + Seconds * 64 + 640;
    internal const string ExecuteId = "exec|r1|T|BombsiteA|s=10";
    internal const string DefaultId = "default|r2|T";

    internal static readonly string Sha = new('a', 64);
    internal static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    internal SuggestedTagsReviewHarness(string? cacheRoot = null, string? tagsRoot = null)
    {
        Cache = new DemoCacheStore(cacheRoot);
        Proposals = new ProposalStore(cacheRoot, Cache);
        Tags = new TagStore(tagsRoot);
        Regions = new SiteRegionStore(null);
        Regions.Save(SuggestedTagsTestData.Table);
        Service = new SuggestedTagsService(Cache, Proposals, Tags, Regions, () => Profile, () => Enabled,
            () => Background, walk: _ => Walk(), utcNow: () => Now);
    }

    internal DemoCacheStore Cache { get; }
    internal ProposalStore Proposals { get; }
    internal TagStore Tags { get; }
    internal SiteRegionStore Regions { get; }
    internal SuggestedTagsService Service { get; }
    internal DetectorProfile Profile { get; set; } = DetectorProfile.Default;
    internal bool Enabled { get; set; } = true;
    internal bool Background { get; set; } = true;

    public void Dispose() => Proposals.Dispose();

    /// <summary>The parsed record with rows the evaluator wants; <paramref name="round" /> is the first round's number.</summary>
    internal void Seed(int round = 1, string? sha = null) =>
        Cache.Upsert(RoundIndexTestData.ParsedRecord(DemoPath, SuggestedTagsTestData.Map, sha ?? Sha,
            Rows(round)));

    internal static RoundFactsRows Rows(int round = 1) =>
        RoundIndexTestData.Facts(
            RoundIndexTestData.Round(round, FreezeEnd, FreezeEnd + Seconds * 64),
            RoundIndexTestData.Round(round + 1, SecondFreezeEnd, SecondFreezeEnd + Seconds * 64));

    /// <summary>A two-frame parse on the test map; <paramref name="frames" /> adds frames to change the clock.</summary>
    internal static ParsedDemo Parse(int frames = 2)
    {
        List<DemoFrame> list = [RoundIndexTestData.Frame(0, 1)];
        for (int i = 1; i < frames; i++)
        {
            list.Add(RoundIndexTestData.Frame(i, 20000 * i / (frames - 1)));
        }

        return SyntheticParsedDemo.Create(list, mapName: SuggestedTagsTestData.Map, tickCount: 20000);
    }

    /// <summary>Seeds, then builds the proposals through the evaluator.</summary>
    internal void Build(int round = 1, int frames = 2)
    {
        Seed(round);
        Service.Evaluate(DemoPath, Parse(frames));
    }

    /// <summary>
    ///     One sample per slot per second over both rounds. Round one: CTs in spawn, T slots 7 to 9 in
    ///     <c>ALong</c> from 12 and slot 6 through <c>ALong</c> onto the site at 18, slot 10 in spawn, which
    ///     is the execute. Round two: the T side spread over <c>ALong</c>, <c>Middle</c>, <c>BTunnel</c> and
    ///     <c>Connector</c> from 20 with nobody going in, which is a default.
    /// </summary>
    internal static IEnumerable<PositionSample> Walk()
    {
        string[] spread = ["ALong", "Middle", "BTunnel", "Connector"];
        for (int s = 0; s < Seconds; s++)
        {
            int tick = FreezeEnd + s * 64;
            foreach (int slot in RoundIndexTestData.CtSlots)
            {
                yield return RoundIndexTestData.Sample(tick, slot, "CTSpawn");
            }

            yield return RoundIndexTestData.Sample(tick, 6, s < 12 ? "TSpawn" : s < 18 ? "ALong" : "BombsiteA");
            foreach (int slot in (int[])[7, 8, 9])
            {
                yield return RoundIndexTestData.Sample(tick, slot, s < 12 ? "TSpawn" : "ALong");
            }

            yield return RoundIndexTestData.Sample(tick, 10, "TSpawn");
        }

        for (int s = 0; s < Seconds; s++)
        {
            int tick = SecondFreezeEnd + s * 64;
            foreach (int slot in RoundIndexTestData.CtSlots)
            {
                yield return RoundIndexTestData.Sample(tick, slot, "CTSpawn");
            }

            for (int i = 0; i < spread.Length; i++)
            {
                yield return RoundIndexTestData.Sample(tick, 6 + i, s < 20 ? "TSpawn" : spread[i]);
            }

            yield return RoundIndexTestData.Sample(tick, 10, "TSpawn");
        }
    }

    /// <summary>A proposal built by hand for the queue and track tests.</summary>
    internal static TagProposal Proposal(string id, string detector, int round, int from, int to, int trigger,
        double confidence, string? site = "BombsiteA") =>
        new(id, detector, detector, round, 2, from, to, trigger, confidence,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["count"] = confidence },
            site is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["site"] = site },
            [new ProposalEvidence("occupancy", trigger, $"{detector} fired")]);

    /// <summary>Writes proposals straight into the store, as a build would have, with the record's hash.</summary>
    internal void Write(params TagProposal[] proposals)
    {
        Seed();
        Proposals.Write(DemoPath, new ProposalDocument
        {
            Demo = new ProposalDemoHeader { Sha256 = Sha, FileName = "review.dem" },
            Clock = new RoundFactsClock { TickRate = 64, FrameCount = 2, FirstTick = 1, LastTick = 20000 },
            DetectorSet = new ProposalDetectorSet { Fingerprint = "fp", ProfileId = "team-default" },
            Proposals = [.. proposals.Select(p => StoredProposal.From(p, FreezeEnd))]
        });
    }
}
