#region

using System.Globalization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>The risk-5 gate, on real data.</b> <c>FloorSplitter</c> keeps learning for the whole demo: the
///     floor count grows and, more importantly, the <i>boundaries keep moving</i> as the histogram
///     accumulates. A level identity that is re-derived from Z on every rebuild therefore churns, and
///     everything keyed on it, including pane cameras, picture caches, and annotation anchors, churns with it.
///     <para>
///         Replayed against the committed <c>assets/tour/sample-de_nuke.dem</c> (three rounds of a pro
///         de_nuke GOTV demo, two floors), so this runs in every checkout rather than waiting on a demo
///         somebody has to stage.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DLevelStabilityTests
{
    private const int ObserveStride = 128; // ~2/sec at 64-tick, matching FloorSplitterMultiFloorTests
    private const string NukeSample = "assets/tour/sample-de_nuke.dem";

    /// <summary>
    ///     <b>The drift case, on real data.</b> Replays Nuke with the histogram alone, no baked bundle,
    ///     so the band boundaries move for the whole demo as more player Z accumulates. Every identity
    ///     must survive that: an id whose band no longer overlaps the one it was minted on has silently
    ///     become a different floor, taking its camera and (from B2) its annotations with it.
    ///     <para>
    ///         The assertion that the bands actually <i>did</i> move is part of the test: without it this
    ///         would pass vacuously on a demo whose histogram happened to settle immediately.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Nuke_LevelIds_AreStable_AcrossTheWholeDemo()
    {
        string path = ResolveNuke();

        MapSpace space = new();
        Dictionary<MapLevelId, (double MinZ, double MaxZ)> seen = [];
        List<string> violations = [];
        List<string> trail = [];
        int rebuilds = 0;
        int driftedWhileRetained = 0;

        foreach (IReadOnlyList<FloorSlice> bands in Checkpoints(path, null))
        {
            LevelSetChange change = space.Rebuild(bands);
            if (!change.Changed)
            {
                continue;
            }

            rebuilds++;
            trail.Add(space.Levels.Count.ToString(CultureInfo.InvariantCulture));

            foreach (MapLevel level in space.Levels)
            {
                if (seen.TryGetValue(level.Id, out (double MinZ, double MaxZ) before))
                {
                    // A surviving identity must still describe the SAME floor. A carried id whose band
                    // no longer overlaps the one it was minted on is exactly the silent weld risk 5 names.
                    double score = MapSpace.OverlapScore(before.MinZ, before.MaxZ, level.ZMin, level.ZMax);
                    if (score <= 0)
                    {
                        violations.Add($"{level.Id} moved from [{before.MinZ:F0}..{before.MaxZ:F0}] " +
                                       $"to [{level.ZMin:F0}..{level.ZMax:F0}]");
                    }

                    if (Math.Abs(before.MinZ - level.ZMin) > 1e-3 ||
                        Math.Abs(before.MaxZ - level.ZMax) > 1e-3)
                    {
                        driftedWhileRetained++;
                    }
                }

                seen[level.Id] = (level.ZMin, level.ZMax);
            }
        }

        Console.WriteLine($"[levels-nuke] {rebuilds} rebuilds, counts={string.Join(",", trail)}, " +
                          $"ids={string.Join(",", seen.Keys)}, drifted-while-retained={driftedWhileRetained}");

        await Assert.That(rebuilds).IsGreaterThan(0);
        await Assert.That(driftedWhileRetained).IsGreaterThan(0)
            .Because("the histogram must actually move a boundary, or nothing is being tested");
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     The same demo the way the app sees it: with de_nuke's baked nav floors bound, which is what
    ///     makes it a two-floor map. Ids must hold and the level containing a fixed reference Z must keep
    ///     the same identity for the whole replay.
    /// </summary>
    [Test]
    public async Task Nuke_WithBakedFloors_HasTwoStableLevels()
    {
        string path = ResolveNuke();
        IReadOnlyList<FloorSlice> baked = BakedNukeFloors();

        MapSpace space = new();
        List<string> violations = [];
        MapLevelId reference = MapLevelId.None;
        double referenceZ = double.NaN;

        foreach (IReadOnlyList<FloorSlice> bands in Checkpoints(path, baked))
        {
            space.Rebuild(bands);

            if (reference.IsNone && space.Levels.Count > 1)
            {
                referenceZ = -420; // the floor every player on this capture stands on
                reference = space.LevelFor(referenceZ)!.Id;
            }
            else if (!reference.IsNone && space.LevelFor(referenceZ) is { } current &&
                     current.Id != reference)
            {
                violations.Add($"z={referenceZ:F0} moved from {reference} to {current.Id}");
            }
        }

        Console.WriteLine($"[levels-nuke-baked] levels={space.Levels.Count} " +
                          $"ids={string.Join(",", space.Levels.Select(l => l.Id.ToString()))} " +
                          $"referenceZ={referenceZ:F0} → {reference}");

        await Assert.That(space.Levels.Count).IsEqualTo(2)
            .Because("de_nuke's baked bundle publishes two nav floors");
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     AutoFollow, on a real player's Z track. Without the hysteresis a stairwell or a boundary that
    ///     drifts through a standing player produces hundreds of switches; with it, only genuine
    ///     traversals count.
    /// </summary>
    [Test]
    public async Task Nuke_AutoFollow_SwitchCount_IsBounded()
    {
        string path = ResolveNuke();

        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int cap = Math.Min(frames.Count, 60_000);

        FloorSplitter splitter = new();
        splitter.SetAuthoritativeFloors(BakedNukeFloors());

        MapSpace space = new();
        EntityTracker tracker = new();
        LevelHysteresis hysteresis = new();

        // One observation stride of scene time per sample, the dwell is scene-time based, so a replay
        // driving it with the demo's own clock is what the interactive path does too.
        SceneTime time = new(0, 0, 0, ObserveStride / 128.0, false);

        int followedSlot = -1;
        int switches = 0;
        int observations = 0;
        int naiveSwitches = 0;
        MapLevelId naive = MapLevelId.None;
        MapLevelId last = MapLevelId.None;

        for (int f = 0; f < cap; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);
            if (f % ObserveStride != 0)
            {
                continue;
            }

            double? followedZ = null;
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                if (PositionUtil.CellToWorld(pawn) is not { } p)
                {
                    return;
                }

                splitter.Observe(p.Z);
                if (followedSlot < 0)
                {
                    followedSlot = slot;
                }

                if (slot == followedSlot)
                {
                    followedZ = p.Z;
                }
            });

            space.Rebuild(splitter.Slices);
            if (space.Levels.Count < 2 || followedZ is not { } z)
            {
                continue;
            }

            observations++;

            MapLevelId chosen = hysteresis.Update(in time, z, space);
            if (!last.IsNone && chosen != last)
            {
                switches++;
            }

            last = chosen;

            // The same track with no hysteresis at all, for scale.
            MapLevelId raw = space.LevelFor(z)!.Id;
            if (!naive.IsNone && raw != naive)
            {
                naiveSwitches++;
            }

            naive = raw;
        }

        Console.WriteLine($"[autofollow-nuke] slot={followedSlot} observations={observations} " +
                          $"switches={switches} (no hysteresis: {naiveSwitches})");

        await Assert.That(observations).IsGreaterThan(0)
            .Because("the tour sample must reach two floors with a live player to follow");

        // Three rounds. A dither regression shows up as hundreds; a genuine traversal is one.
        await Assert.That(switches).IsLessThanOrEqualTo(60);
        await Assert.That(switches).IsLessThanOrEqualTo(naiveSwitches);
    }

    /// <summary>
    ///     <b>The positive half of AutoFollow, on real data.</b>
    ///     <see cref="Nuke_AutoFollow_SwitchCount_IsBounded" /> follows the first live slot it meets and
    ///     bounds the switch count from above, on this capture that slot never clears the spatial band,
    ///     so it observes zero switches and cannot tell a working chooser from one that never switches at
    ///     all. This drives every player's own Z track through the chooser and asserts the other
    ///     direction: floors are genuinely traversed, each switch lands on a level that <i>contains</i>
    ///     the player at the frame it happened, and two independent replays of the same track agree
    ///     exactly (the dwell is scene-time driven, so it must be reproducible).
    /// </summary>
    [Test]
    public async Task Nuke_AutoFollow_SwitchesToTheFloorThePlayerIsOn_Deterministically()
    {
        MapSpace space = new();
        space.Rebuild(BakedNukeFloors());
        await Assert.That(space.Levels).HasCount().EqualTo(2);

        Dictionary<int, List<(int Frame, double Z)>> tracks = ZTracks(ResolveNuke());
        int traversed = 0;
        int transitions = 0;

        foreach (int slot in tracks.Keys.Order())
        {
            List<(int Frame, double Z)> track = tracks[slot];
            List<(int Frame, MapLevelId Id)> first = ChooseAlong(track, space);
            List<(int Frame, MapLevelId Id)> second = ChooseAlong(track, space);

            await Assert.That(first.SequenceEqual(second)).IsTrue()
                .Because($"slot {slot}'s level track must be reproducible");

            if (first.Count > 0)
            {
                traversed++;
                transitions += first.Count;
            }

            foreach ((int frame, MapLevelId id) in first)
            {
                MapLevel? landed = space.ById(id);
                await Assert.That(landed).IsNotNull();

                double z = track.First(t => t.Frame == frame).Z;
                await Assert.That(landed!.Contains(z)).IsTrue()
                    .Because($"slot {slot} switched to {id} at f{frame} with z={z:F0}");
            }
        }

        Console.WriteLine($"[autofollow-nuke-positive] players={tracks.Count} " +
                          $"traversed={traversed} transitions={transitions}");

        await Assert.That(traversed).IsGreaterThan(0)
            .Because("this capture takes players to both floors — a chooser that never switches is broken");
    }

    // The chooser's answer along one player's Z track, as (frame, new level) transitions.
    private static List<(int Frame, MapLevelId Id)> ChooseAlong(List<(int Frame, double Z)> track,
        MapSpace space)
    {
        LevelHysteresis hysteresis = new();
        SceneTime time = new(0, 0, 0, ObserveStride / 64.0, false);
        List<(int, MapLevelId)> switches = [];
        MapLevelId last = MapLevelId.None;

        foreach ((int frame, double z) in track)
        {
            MapLevelId chosen = hysteresis.Update(in time, z, space);
            if (!last.IsNone && chosen != last)
            {
                switches.Add((frame, chosen));
            }

            last = chosen;
        }

        return switches;
    }

    // Per-slot world-Z samples at the observation stride, keyed by demo frame index.
    private static Dictionary<int, List<(int Frame, double Z)>> ZTracks(string path)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        Dictionary<int, List<(int, double)>> tracks = [];
        EntityTracker tracker = new();

        int cap = Math.Min(frames.Count, 130_000);
        for (int f = 0; f < cap; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);
            if (f % ObserveStride != 0)
            {
                continue;
            }

            int frameIndex = f;
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                if (PositionUtil.CellToWorld(pawn) is not { } p)
                {
                    return;
                }

                if (!tracks.TryGetValue(slot, out List<(int, double)>? list))
                {
                    list = [];
                    tracks[slot] = list;
                }

                list.Add((frameIndex, p.Z));
            });
        }

        return tracks;
    }

    /// <summary>A single-floor map must produce no level chrome at all (plan D9).</summary>
    [Test]
    public async Task Dust2_StaysSingleLevel_StripHidden()
    {
        string? dust2 = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem");
        if (dust2 is null)
        {
            throw new SkipTestException("no dust2 demo");
        }

        MapSpace space = new();
        int maxLevels = 0;
        foreach (IReadOnlyList<FloorSlice> bands in Checkpoints(dust2, null))
        {
            space.Rebuild(bands);
            maxLevels = Math.Max(maxLevels, space.Levels.Count);
        }

        Console.WriteLine($"[levels-dust2] max levels={maxLevels}");
        await Assert.That(maxLevels).IsEqualTo(1);
    }

    // Steps one tracker forward, folding live-player Z into a FloorSplitter, and yields the band list at
    // evenly spaced checkpoints. Mirrors FloorSplitterMultiFloorTests.AccumulateFloorCounts.
    private static IEnumerable<IReadOnlyList<FloorSlice>> Checkpoints(string path,
        IReadOnlyList<FloorSlice>? authoritativeFloors)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int cap = Math.Min(frames.Count, 130_000);
        int checkpointStride = Math.Max(1, cap / 12);

        FloorSplitter splitter = new();
        splitter.SetAuthoritativeFloors(authoritativeFloors);
        EntityTracker tracker = new();

        for (int f = 0; f < cap; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);

            if (f % ObserveStride == 0)
            {
                PawnLookup.ForEachLivePawn(tracker, (_, pawn) =>
                {
                    if (PositionUtil.CellToWorld(pawn) is { } p)
                    {
                        splitter.Observe(p.Z);
                    }
                });
            }

            if (f > 0 && f % checkpointStride == 0)
            {
                yield return splitter.Slices;
            }
        }

        yield return splitter.Slices;
    }

    // de_nuke's committed bundle: the two baked nav floors the app binds through MapAssetLoader. They
    // are what makes Nuke a two-floor map in the viewport. The three-round tour sample's own Z
    // histogram never accumulates enough lower-floor traffic to split on its own.
    private static IReadOnlyList<FloorSlice> BakedNukeFloors()
    {
        using LoadedMapAsset? asset = MapAssetPipeline.TryLoad("de_nuke");
        if (asset is null)
        {
            throw new SkipTestException("no de_nuke bundle under assets/");
        }

        return [.. asset.Floors];
    }

    // The bundled sample is repo-relative, so it is present in every checkout and on CI. A staged
    // full-length nuke demo wins when one is available.
    private static string ResolveNuke()
    {
        string? staged = DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem")
                         ?? DemoTestHelper.FindDemoPath("furia-vs-vitality-m3-nuke.dem");
        if (staged is not null)
        {
            return staged;
        }

        string bundled = Path.Combine(RepoRoot(), NukeSample.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(bundled))
        {
            return bundled;
        }

        throw new SkipTestException($"no nuke demo (tried the staged names and {NukeSample})");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new SkipTestException("could not locate the repository root");
    }
}
