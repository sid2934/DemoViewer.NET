#region

using DemoViewer.NET.Playback2D.Core;
using System.Globalization;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Real-demo probe for the floor thresholds: reads the map's networked
///     <c>m_MinimapVerticalSectionHeights</c> via <see cref="Playback2DTabViewModel.SectionHeights" /> and
///     confirms it is in the SAME world-Z space as <see cref="PlayerMarker.WorldZ" /> (a wrong Z space
///     would silently mis-split floors). On a single-floor map the field
///     publishes ≤1 usable value → null, so the histogram fallback owns the split; the probe SKIPS rather
///     than asserting in that case (it can't manufacture a Nuke/Vertigo demo). When the field IS present it
///     asserts the boundaries are ascending and bracket the observed player-Z range.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DFloorThresholdProbeTests
{
    [Test]
    public async Task SectionHeights_WhenPresent_AreAscending_AndInPlayerZSpace()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int target = Math.Clamp(frames.Count / 2, 0, frames.Count - 1);
        EntityTracker tracker = new();
        tracker.ReplayToIndex(target, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        controller.SyncPositionFromShell(target);
        controller.PublishTracker(tracker);

        ModuleContext context = new(controller, () => path);
        context.SetRoster(demo.Players.Values.Select(p =>
            new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));

        Playback2DTabViewModel vm = new();
        vm.OnActivated(context);

        IReadOnlyList<double>? heights = vm.SectionHeights;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (PlayerMarker m in vm.Markers)
        {
            minZ = Math.Min(minZ, m.WorldZ);
            maxZ = Math.Max(maxZ, m.WorldZ);
        }

        Console.WriteLine($"[floor-threshold] {Path.GetFileName(path)} frame={target}/{frames.Count} " +
                          $"markers={vm.Markers.Count} playerZ=[{minZ:F0}..{maxZ:F0}] " +
                          $"sectionHeights={(heights is null ? "null (histogram fallback)" : string.Join(", ", heights.Select(h => h.ToString("F2", CultureInfo.InvariantCulture))))}");

        if (heights is null)
        {
            // Single-floor map (the common case / this demo): the histogram heuristic owns the split.
            throw new SkipTestException(
                "demo map publishes no multi-floor m_MinimapVerticalSectionHeights — histogram fallback in use");
        }

        // Present → must be a real multi-section map: ascending boundaries, sane world-Z magnitudes that
        // overlap the observed player-Z band (proves the same coordinate space).
        await Assert.That(heights.Count).IsGreaterThanOrEqualTo(2);
        for (int i = 1; i < heights.Count; i++)
        {
            await Assert.That(heights[i]).IsGreaterThan(heights[i - 1]);
        }

        // The boundaries are world-Z, same as the players — they must lie within a credible band around the
        // observed player Z (not, say, in cell-space ~16k or normalized 0..1).
        await Assert.That(heights[0]).IsGreaterThan(minZ - 5000);
        await Assert.That(heights[^1]).IsLessThan(maxZ + 5000);
    }

    // DIAGNOSTIC (not an assertion gate): dumps how the networked section heights distribute players across
    // sections over many frames. A radar floor-switch threshold that cuts THROUGH a continuous single-floor
    // Z distribution (boundaries only tens of units apart) fragments players into flickering bands with empty
    // sections — the histogram heuristic (180u gap + hysteresis) avoids that. This logs the evidence so the
    // section-height-vs-histogram decision is data-driven.
    [Test]
    public async Task SectionHeights_PlayerDistribution_Diagnostic()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        // Read the section heights once (mid-demo, fully decoded).
        Playback2DTabViewModel probeVm = new();
        {
            int t = frames.Count / 2;
            EntityTracker tk = new();
            tk.ReplayToIndex(t, frames);
            PlaybackController c = new();
            c.LoadDemo(frames, 64);
            c.SyncPositionFromShell(t);
            c.PublishTracker(tk);
            ModuleContext ctx = new(c, () => path);
            ctx.SetRoster(demo.Players.Values.Select(p =>
                new PlayerRosterEntry
                {
                    Slot = p.Slot,
                    SteamId = p.SteamId64,
                    Name = p.Name
                }));
            probeVm.OnActivated(ctx);
        }

        IReadOnlyList<double>? heights = probeVm.SectionHeights;
        if (heights is null)
        {
            throw new SkipTestException("single-floor map — histogram fallback; no distribution to diagnose");
        }

        // Build a FloorSplitter exactly as the viewport does: feed observed Z into the histogram AND supply
        // the section heights, so the adoption GATE decides whether the heights are used or rejected. Walk
        // ~12 frames spanning the demo, counting how many alive-player Z values land in each section.
        FloorSplitter splitter = new();
        splitter.SetSectionHeights(heights);
        // Control: identical histogram, but the section heights are NOT supplied. If the heights were
        // adopted, the two splitters would diverge; equal slice counts prove the heights have no effect
        // (gated off — the histogram owns the split). Robust regardless of how many floors the map has.
        FloorSplitter splitterNoHeights = new();

        int sampled = 0;
        int finalSectionCount = 0;
        bool adopted = false;
        // Speedup P3: sample frames are monotonically increasing, so ONE tracker advances
        // forward between samples instead of a fresh replay from frame 0 per sample (the
        // quadratic-replay pattern this suite's speed audit flagged; ~99s → ~10s).
        EntityTracker sampleTracker = new();
        int cursor = -1;
        for (int f = frames.Count / 8; f < frames.Count; f += frames.Count / 12)
        {
            if (cursor < 0)
            {
                sampleTracker.ReplayToIndex(f, frames);
            }
            else
            {
                for (int ff = cursor + 1; ff <= f; ff++)
                {
                    sampleTracker.AdvanceOneFrame(frames[ff]);
                }
            }

            cursor = f;
            PlaybackController c = new();
            c.LoadDemo(frames, 64);
            c.SyncPositionFromShell(f);
            c.PublishTracker(sampleTracker);
            ModuleContext ctx = new(c, () => path);
            ctx.SetRoster(demo.Players.Values.Select(p =>
                new PlayerRosterEntry
                {
                    Slot = p.Slot,
                    SteamId = p.SteamId64,
                    Name = p.Name
                }));
            Playback2DTabViewModel vm = new();
            vm.OnActivated(ctx);

            foreach (PlayerMarker m in vm.Markers)
            {
                splitter.Observe(m.WorldZ);
                splitterNoHeights.Observe(m.WorldZ);
            }

            int sectionCount = Math.Max(1, splitter.Slices.Count);
            finalSectionCount = sectionCount;
            // Heights are adopted iff supplying them CHANGES the split vs the no-heights control. (The old
            // "sliceCount == heights.Count" proxy is invalid: the histogram can legitimately find that many
            // floors on a real multi-floor map, which is not the heights being adopted.)
            adopted = splitter.Slices.Count != splitterNoHeights.Slices.Count;

            int[] perSection = new int[sectionCount];
            foreach (PlayerMarker m in vm.Markers)
            {
                perSection[Math.Clamp(splitter.SliceIndexFor(m.WorldZ), 0, sectionCount - 1)]++;
            }

            sampled++;
            Console.WriteLine($"[floor-dist] frame={f,6} sections=[{string.Join(",", perSection)}] " +
                              $"sliceCount={sectionCount} adoptedHeights={adopted}");
        }

        Console.WriteLine($"[floor-dist] SUMMARY finalSliceCount={finalSectionCount} adoptedHeights={adopted} " +
                          $"sampled={sampled} " +
                          $"heights=[{string.Join(", ", heights.Select(h => h.ToString("F1", CultureInfo.InvariantCulture)))}] " +
                          $"=> the single-floor radar sections were {(adopted ? "ADOPTED (unexpected!)" : "GATED OFF (correct — histogram floor kept)")}");

        await Assert.That(sampled).IsGreaterThan(0);

        // The regression gate: the networked radar section heights are GATED OFF — supplying them does not
        // change the split (the density-valley histogram owns it). Proven by the no-heights control matching.
        await Assert.That(adopted).IsFalse();
        await Assert.That(finalSectionCount).IsEqualTo(splitterNoHeights.Slices.Count);
    }
}
