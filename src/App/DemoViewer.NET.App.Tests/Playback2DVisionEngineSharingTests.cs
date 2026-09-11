#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Playback2D vision overlay is the third consumer of <see cref="VisibilityEngineCache.Shared" />.
///     It used to call <see cref="VisibilityEngine.Load" /> itself, so opening the 2D tab on a map the
///     analysis run or the Stats replay had already built rebuilt the whole BVH (up to a second on
///     de_ancient) into a second instance. Real bake, de_nuke, resolved the way each consumer resolves
///     it: the Stats side through <see cref="CollisionAssetLocator" />, the overlay through its map
///     bundle. Sharing is only real if the two name the same file, so the test asks through one and
///     reads through the other. Skips, saying what is missing, without the assets.
/// </summary>
[NotInParallel]
public sealed class Playback2DVisionEngineSharingTests
{
    private static string RequireStatsBake()
    {
        string? tris = CollisionAssetLocator.FindCollisionTris("de_nuke");
        if (tris is null)
        {
            throw new SkipTestException(
                $"no de_nuke collision.tris: set {CollisionAssetLocator.EnvVar} to the assets directory");
        }

        return tris;
    }

    /// <summary>Activates a fresh tab on de_nuke with no demo, which is enough for the bundle to load.</summary>
    private static (Playback2DTabViewModel Vm, string OverlayTris) ActivateOnNuke()
    {
        Playback2DTabViewModel vm = new();
        ModuleContext context = new(new PlaybackController(), () => null);
        context.SetMapName("de_nuke");
        vm.OnActivated(context);

        string? tris = vm.MapAsset?.CollisionTrisPath;
        if (tris is null)
        {
            vm.Dispose();
            throw new SkipTestException("no de_nuke bundle with a collision mesh reachable from the test binaries");
        }

        return (vm, tris);
    }

    /// <summary>
    ///     The synchronous seam the render tests build through hands back the very instance the Stats
    ///     resolver's ask built, and starts no build of its own.
    /// </summary>
    [Test]
    public async Task SyncSeam_ReturnsTheInstanceTheStatsResolverBuilt()
    {
        string statsTris = RequireStatsBake();
        (Playback2DTabViewModel vm, string overlayTris) = ActivateOnNuke();
        try
        {
            Console.WriteLine($"[vision-cache] stats={statsTris} overlay={overlayTris}");
            VisibilityEngine viaStats = await VisibilityEngineCache.Shared.GetOrLoadAsync(statsTris);
            long builds = VisibilityEngineCache.Shared.Builds;

            vm.LoadVisionEngineSyncForTest();

            // The reference check is the one that discriminates. A seam reverted to VisibilityEngine.Load
            // bypasses the cache, so Builds stays put either way; only a second instance reveals it.
            await Assert.That(vm.VisionEngine).IsSameReferenceAs(viaStats);
            await Assert.That(VisibilityEngineCache.Shared.Builds).IsEqualTo(builds);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>
    ///     The production path (switching the overlay on) asks the shared cache from its pool thread:
    ///     the ask registers as a hit against the engine already built and no second build starts.
    ///     The dispatcher hop that lands the engine on the view model is not awaited here; under
    ///     headless Avalonia that pump is exactly why the sync seam exists.
    /// </summary>
    [Test]
    public async Task EnablingVision_AsksTheSharedCache_AndStartsNoSecondBuild()
    {
        string statsTris = RequireStatsBake();
        (Playback2DTabViewModel vm, string _) = ActivateOnNuke();
        try
        {
            await VisibilityEngineCache.Shared.GetOrLoadAsync(statsTris);
            long builds = VisibilityEngineCache.Shared.Builds;
            long hits = VisibilityEngineCache.Shared.Hits;

            vm.ShowVision = true;

            // The wait for Hits is the discriminating step, not the Builds check after it: a production
            // path reverted to VisibilityEngine.Load never touches the cache, so Builds would still be
            // unchanged, and this loop is what times out instead.
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (VisibilityEngineCache.Shared.Hits == hits)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new System.TimeoutException("the overlay never asked the shared cache");
                }

                await Task.Delay(10);
            }

            await Assert.That(VisibilityEngineCache.Shared.Builds).IsEqualTo(builds);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
