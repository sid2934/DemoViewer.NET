#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Cameras;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <c>StackedLayout</c> reproducing the pre-v2 bands, and <c>PaneSet</c> carrying camera identity
///     across a rebuild by level id rather than array position (design risk 5).
/// </summary>
public class PaneLayoutTests
{
    private static readonly WorldBounds _extent = new(-1000, -1000, 1000, 1000);

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task Arrange_SplitsTheHostIntoEqualBands_HighestOnTop(int levels)
    {
        MapSpace space = new();
        space.Rebuild(Bands(levels));

        StackedLayout layout = new();
        IReadOnlyList<LevelPane> panes = layout.Arrange(space, LevelDisplayMode.Stacked, new SKSize(800, 600));

        await Assert.That(panes.Count).IsEqualTo(levels);
        float bandHeight = 600f / levels;

        for (int i = 0; i < levels; i++)
        {
            SKRect rect = panes[i].ViewportRect;
            int section = levels - 1 - i; // level 0 is the LOWEST, drawn in the BOTTOM band
            await Assert.That(rect.Top).IsEqualTo(section * bandHeight).Within(0.001f);
            await Assert.That(rect.Height).IsEqualTo(bandHeight).Within(0.001f);
            await Assert.That(rect.Width).IsEqualTo(800f).Within(0.001f);
        }
    }

    /// <summary>
    ///     <c>PaneAt</c> must agree with the pre-v2 <c>SliceIndexAtScreenY</c>: floor the Y by band
    ///     height, clamp, invert. A disagreement means a drag pans a different floor from the one under
    ///     the cursor.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task PaneAt_MatchesThePreV2SliceIndexAtScreenY(int levels)
    {
        PaneSet panes = Build(levels, 800, 600);

        List<double> disagreements = [];
        for (int i = 0; i <= 120; i++)
        {
            float y = i * 5f; // 0 .. 600
            int expected = LegacySliceIndexAtScreenY(y, levels, 600);
            int actual = panes.PaneAt(400, y)!.LevelIndex;
            if (expected != actual)
            {
                disagreements.Add(y);
            }
        }

        Console.WriteLine($"[pane-hit] {levels} levels: {disagreements.Count} disagreements");
        await Assert.That(disagreements).IsEmpty();
    }

    [Test]
    public async Task Reconcile_InsertingALowerLevel_KeepsTheUpperPanesCameraAndOverride()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-384, -128)]);

        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        LevelPane upper = panes.Panes[0];
        MapLevelId upperId = upper.LevelId;
        upper.Camera.Current = upper.Camera.Current.WithPanDelta(37, -19);
        upper.Camera.ManualOverride = true;
        upper.Rig = FitAliveRig.Instance;
        double panX = upper.Camera.Current.PanX;

        // A lower floor appears. Under the pre-v2 index-keyed reconcile this handed the upper floor the
        // NEW pane and silently discarded the user's pan.
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        LevelPane? carried = panes.FindById(upperId);
        await Assert.That(carried).IsNotNull();
        await Assert.That(ReferenceEquals(carried, upper)).IsTrue();
        await Assert.That(carried!.Camera.Current.PanX).IsEqualTo(panX).Within(1e-9);
        await Assert.That(carried.Camera.ManualOverride).IsTrue();
        await Assert.That(carried.Rig.Id).IsEqualTo("fit-alive");
        await Assert.That(carried.LevelIndex).IsEqualTo(1);
        await Assert.That(carried.ViewportRect.Top).IsEqualTo(0f).Within(0.001f);
    }

    [Test]
    public async Task Reconcile_ANewLevel_IsFitToTheExtent()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-384, -128)]);
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        LevelPane fresh = panes.Panes[0];
        ViewportTransform expected = ViewportTransform.Fit(fresh.ViewportRect.Width, fresh.ViewportRect.Height,
            _extent.MinX, _extent.MinY, _extent.MaxX, _extent.MaxY);

        await Assert.That(fresh.Camera.Current.BaseScale).IsEqualTo(expected.BaseScale).Within(1e-9);
        await Assert.That(fresh.Camera.ManualOverride).IsFalse();
    }

    [Test]
    public async Task Reconcile_ARemovedLevel_DropsItsPane()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-448, -384), new FloorSlice(-384, -128)]);
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);
        MapLevelId lower = panes.Panes[0].LevelId;

        space.Rebuild([new FloorSlice(-384, -128)]);
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        await Assert.That(panes.Panes.Count).IsEqualTo(1);
        await Assert.That(panes.FindById(lower)).IsNull();
    }

    /// <summary>
    ///     The steady-state frame — level set unchanged, same host size — must not reach the layout
    ///     policy at all, or the §6 zero-allocation budget is unreachable before a single layer is
    ///     written.
    /// </summary>
    [Test]
    public async Task Reconcile_WithNothingChanged_IsAllocationFreeAndReturnsFalse()
    {
        PaneSet panes = Build(2, 800, 600);
        MapSpace space = panes.Panes[0].Space!;

        for (int i = 0; i < 16; i++)
        {
            panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = false;
        for (int i = 0; i < 512; i++)
        {
            changed |= panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 no-op reconciles: {delta} bytes");
        await Assert.That(changed).IsFalse();
        await Assert.That(delta).IsEqualTo(0);
    }

    [Test]
    public async Task CopySnapshots_IsAllocationFreeAfterTheListHasGrown()
    {
        PaneSet panes = Build(2, 800, 600);
        List<LevelPaneSnapshot> into = [];
        for (int i = 0; i < 8; i++)
        {
            panes.CopySnapshots(into);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            panes.CopySnapshots(into);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 snapshot copies: {delta} bytes");
        await Assert.That(into.Count).IsEqualTo(2);
        await Assert.That(delta).IsEqualTo(0);
    }

    [Test]
    public async Task FitAll_ClearsOverridesAndReframes()
    {
        PaneSet panes = Build(2, 800, 600);
        panes.Panes[0].Camera.ManualOverride = true;
        panes.Panes[0].Camera.Current = panes.Panes[0].Camera.Current.WithPanDelta(100, 100);

        panes.FitAll(_extent);

        await Assert.That(panes.Panes[0].Camera.ManualOverride).IsFalse();
        await Assert.That(panes.Panes[0].Camera.Current.PanX).IsEqualTo(0d).Within(1e-9);
    }

    [Test]
    public async Task CameraEpoch_BumpsOnAMaterialMoveAndOnResize()
    {
        PaneSet panes = Build(2, 800, 600);
        LevelPane pane = panes.Panes[0];
        int start = pane.CameraEpoch;

        pane.Camera.Current = pane.Camera.Current.WithPanDelta(0.0001, 0);
        await Assert.That(pane.SyncCameraEpoch()).IsFalse();
        await Assert.That(pane.CameraEpoch).IsEqualTo(start);

        pane.Camera.Current = pane.Camera.Current.WithPanDelta(40, 0);
        await Assert.That(pane.SyncCameraEpoch()).IsTrue();
        await Assert.That(pane.CameraEpoch).IsEqualTo(start + 1);

        pane.ViewportRect = new SKRect(0, 0, 800, 200);
        await Assert.That(pane.CameraEpoch).IsEqualTo(start + 2);
    }

    private static FloorSlice[] Bands(int count)
    {
        FloorSlice[] bands = new FloorSlice[count];
        for (int i = 0; i < count; i++)
        {
            bands[i] = new FloorSlice(i * 256, i * 256 + 192);
        }

        return bands;
    }

    private static PaneSet Build(int levels, float width, float height)
    {
        MapSpace space = new();
        space.Rebuild(Bands(levels));
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(width, height), _extent);
        return panes;
    }

    // Playback2DViewport.SliceIndexAtScreenY, lines 465-476, transcribed.
    private static int LegacySliceIndexAtScreenY(double screenY, int count, double boundsHeight)
    {
        count = Math.Max(1, count);
        if (count <= 1 || boundsHeight < 1)
        {
            return 0;
        }

        double bandHeight = boundsHeight / count;
        int section = (int)Math.Clamp(Math.Floor(screenY / bandHeight), 0, count - 1);
        return count - 1 - section;
    }
}
