#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <c>SingleLayout</c>, and the pane-state retention that makes flicking between floors free
///     (correction 4: this is behaviour on <c>PaneSet</c>, not a second pane store).
/// </summary>
public class PaneSetLevelRetentionTests
{
    private static readonly WorldBounds _extent = new(-1000, -1000, 1000, 1000);

    [Test]
    public async Task Arrange_ReturnsExactlyOnePane_CoveringHost()
    {
        MapSpace space = Three();
        SingleLayout layout = new()
        {
            ActiveLevelId = space.Levels[1].Id
        };

        IReadOnlyList<LevelPane> panes = layout.Arrange(space, LevelDisplayMode.Single, new SKSize(800, 600));

        await Assert.That(panes).HasCount().EqualTo(1);
        await Assert.That(panes[0].LevelId).IsEqualTo(space.Levels[1].Id);
        await Assert.That(panes[0].LevelIndex).IsEqualTo(1);
        await Assert.That(panes[0].ViewportRect).IsEqualTo(new SKRect(0, 0, 800, 600));
    }

    [Test]
    public async Task UnknownActiveId_FallsBackToTopMost()
    {
        MapSpace space = Three();
        SingleLayout layout = new()
        {
            ActiveLevelId = new MapLevelId(31337)
        };

        IReadOnlyList<LevelPane> panes = layout.Arrange(space, LevelDisplayMode.Single, new SKSize(800, 600));

        await Assert.That(panes).HasCount().EqualTo(1);
        await Assert.That(panes[0].LevelId).IsEqualTo(space.Levels[^1].Id);
    }

    [Test]
    public async Task EmptySpace_ArrangesNothing()
    {
        SingleLayout layout = new();
        await Assert.That(layout.Arrange(new MapSpace(), LevelDisplayMode.Single, new SKSize(800, 600)))
            .IsEmpty();
    }

    /// <summary>
    ///     The regression <c>EnsureCameras</c> could not pass: pan one level, then have a NEW LOWER level
    ///     appear. The panned camera must still be on the level it was panned on, and the newcomer must
    ///     be Fit rather than inherit it.
    /// </summary>
    [Test]
    public async Task CameraSurvives_LevelInsertedBelow()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(0, 640)]);

        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        MapLevelId original = panes.Panes[0].LevelId;
        panes.Panes[0].Camera.Current = panes.Panes[0].Camera.Current.WithPanDelta(64, -32);
        panes.Panes[0].Camera.ManualOverride = true;
        double pannedX = panes.Panes[0].Camera.Current.PanX;

        space.Rebuild([new FloorSlice(-1280, -640), new FloorSlice(0, 640)]);
        panes.RetainUnarranged(space.LastChange);
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        await Assert.That(panes.FindById(original)!.Camera.Current.PanX).IsEqualTo(pannedX).Within(1e-9);
        LevelPane inserted = panes.Panes[0];
        await Assert.That(inserted.LevelId).IsNotEqualTo(original);
        await Assert.That(inserted.Camera.ManualOverride).IsFalse();
        await Assert.That(inserted.Camera.Current.PanX).IsEqualTo(0d).Within(1e-9);
    }

    /// <summary>
    ///     Stacked → Single → Stacked. The floor that is not on screen keeps its pan and its manual
    ///     override, because "not arranged" and "gone" are different things.
    /// </summary>
    [Test]
    public async Task ManualOverride_SurvivesStackedToSingleAndBack()
    {
        MapSpace space = Three();
        StackedLayout stacked = new();
        SingleLayout single = new();
        PaneSet panes = new(stacked);
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        MapLevelId hidden = space.Levels[0].Id;
        LevelPane lower = panes.FindById(hidden)!;
        lower.Camera.Current = lower.Camera.Current.WithPanDelta(123, 45);
        lower.Camera.ManualOverride = true;
        double pannedX = lower.Camera.Current.PanX;

        single.ActiveLevelId = space.Levels[^1].Id;
        panes.Policy = single;
        panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);

        await Assert.That(panes.Panes).HasCount().EqualTo(1);
        await Assert.That(panes.FindById(hidden)).IsNull();
        await Assert.That(panes.TryGetCamera(hidden, out SliceCamera held)).IsTrue();
        await Assert.That(held.ManualOverride).IsTrue();
        await Assert.That(held.Current.PanX).IsEqualTo(pannedX).Within(1e-9);

        panes.Policy = stacked;
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        LevelPane restored = panes.FindById(hidden)!;
        await Assert.That(ReferenceEquals(restored, lower)).IsTrue();
        await Assert.That(restored.Camera.ManualOverride).IsTrue();
        await Assert.That(restored.Camera.Current.PanX).IsEqualTo(pannedX).Within(1e-9);
    }

    [Test]
    public async Task RemovedLevel_DropsState()
    {
        MapSpace space = Three();
        PaneSet panes = new(new StackedLayout());
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        MapLevelId doomed = space.Levels[0].Id;
        panes.FindById(doomed)!.Camera.ManualOverride = true;

        // Off screen under a single pane: retained, not dropped.
        SingleLayout single = new()
        {
            ActiveLevelId = space.Levels[^1].Id
        };
        panes.Policy = single;
        panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);
        await Assert.That(panes.RetainedCount).IsEqualTo(2);
        await Assert.That(panes.TryGetCamera(doomed, out SliceCamera _)).IsTrue();

        // Now the level is genuinely gone. THAT drops it.
        space.Rebuild([new FloorSlice(-640, 0), new FloorSlice(0, 640)]);
        panes.RetainUnarranged(space.LastChange);
        panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);

        await Assert.That(space.LastChange.Removed).Contains(doomed);
        await Assert.That(panes.RetainedCount).IsEqualTo(1);
        await Assert.That(panes.TryGetCamera(doomed, out SliceCamera _)).IsFalse();
    }

    /// <summary>
    ///     A layout policy whose own state changed must be asked again — <c>PaneSet</c> early-outs on the
    ///     level-set version, the mode and the host size, none of which move when the strip picks another
    ///     floor.
    /// </summary>
    [Test]
    public async Task PolicyRevision_ForcesReArrange_WhenTheActiveLevelChanges()
    {
        MapSpace space = Three();
        SingleLayout single = new()
        {
            ActiveLevelId = space.Levels[2].Id
        };
        PaneSet panes = new(single);
        panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);
        await Assert.That(panes.Panes[0].LevelId).IsEqualTo(space.Levels[2].Id);

        single.ActiveLevelId = space.Levels[0].Id;
        bool changed = panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);

        await Assert.That(changed).IsTrue();
        await Assert.That(panes.Panes[0].LevelId).IsEqualTo(space.Levels[0].Id);
    }

    [Test]
    [Category("Budget")]
    public async Task SingleMode_SteadyStateReconcile_IsAllocationFree()
    {
        MapSpace space = Three();
        SingleLayout single = new()
        {
            ActiveLevelId = space.Levels[1].Id
        };
        PaneSet panes = new(single);
        for (int i = 0; i < 16; i++)
        {
            panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = false;
        for (int i = 0; i < 512; i++)
        {
            changed |= panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[alloc] 512 single-mode no-op reconciles: {delta} bytes");
        await Assert.That(changed).IsFalse();
        await Assert.That(delta).IsEqualTo(0);
    }

    [Test]
    public async Task ResetAll_ClearsOverridesOnRetainedLevelsToo()
    {
        MapSpace space = Three();
        StackedLayout stacked = new();
        PaneSet panes = new(stacked);
        panes.Reconcile(space, LevelDisplayMode.Stacked, new SKSize(800, 600), _extent);

        MapLevelId hidden = space.Levels[0].Id;
        panes.FindById(hidden)!.Camera.ManualOverride = true;

        SingleLayout single = new()
        {
            ActiveLevelId = space.Levels[^1].Id
        };
        panes.Policy = single;
        panes.Reconcile(space, LevelDisplayMode.Single, new SKSize(800, 600), _extent);

        panes.ResetAll();

        await Assert.That(panes.TryGetCamera(hidden, out SliceCamera held)).IsTrue();
        await Assert.That(held.ManualOverride).IsFalse();
    }

    [Test]
    public async Task LevelLayouts_For_ReturnsThePolicyAndRefusesTheReservedMode()
    {
        await Assert.That(LevelLayouts.For(LevelDisplayMode.Stacked) is StackedLayout).IsTrue();
        await Assert.That(LevelLayouts.For(LevelDisplayMode.Single) is SingleLayout).IsTrue();

        bool refused = false;
        try
        {
            LevelLayouts.For(LevelDisplayMode.SideBySide);
        }
        catch (NotSupportedException)
        {
            refused = true;
        }

        await Assert.That(refused).IsTrue();

        await Assert.That(LevelLayouts.Parse("Single")).IsEqualTo(LevelDisplayMode.Single);
        await Assert.That(LevelLayouts.Parse("stacked")).IsEqualTo(LevelDisplayMode.Stacked);
        await Assert.That(LevelLayouts.Parse("nonsense")).IsEqualTo(LevelDisplayMode.Stacked);
        await Assert.That(LevelLayouts.Parse(null)).IsEqualTo(LevelDisplayMode.Stacked);
        await Assert.That(LevelLayouts.Parse("SideBySide")).IsEqualTo(LevelDisplayMode.Stacked);

        // Enum.TryParse accepts any NUMBER inside the underlying type's range, so a hand-edited
        // settings file saying "7" would otherwise yield an undefined LevelDisplayMode that
        // LevelLayouts.For throws on — the exact "a typo must not stop the tab opening" case this
        // method exists for.
        await Assert.That(LevelLayouts.Parse("7")).IsEqualTo(LevelDisplayMode.Stacked);
        await Assert.That(LevelLayouts.Parse("-1")).IsEqualTo(LevelDisplayMode.Stacked);
        await Assert.That(LevelLayouts.Parse("2")).IsEqualTo(LevelDisplayMode.Stacked);
    }

    private static MapSpace Three()
    {
        MapSpace space = new();
        space.Rebuild([new FloorSlice(-1280, -640), new FloorSlice(-640, 0), new FloorSlice(0, 640)]);
        return space;
    }
}
