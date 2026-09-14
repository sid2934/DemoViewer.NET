#region

using System.Runtime.CompilerServices;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Services;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="VisibilityEngineCache" />: one build per bake identity, shared across concurrent
///     asks; a re-baked file is a miss on length alone and on write time alone; the LRU cap bounds
///     retention and never evicts a build in flight; an evicted engine is unreachable once its
///     holders drop it; a failed build is not cached; a waiter's cancellation, even one signalled
///     before the ask, does not abandon the build. No real bake is read: the loader is a counting
///     stand-in over a one-triangle mesh, and the keyed files are temp files whose only job is to
///     have a length and a write time.
/// </summary>
public sealed class VisibilityEngineCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dv-viscache-{Guid.NewGuid():N}");

    public VisibilityEngineCacheTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // best effort: a temp directory that lingers is not a test failure
        }
    }

    private static VisibilityEngine OneTriangle()
    {
        float[] soup = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f];
        return VisibilityEngine.FromTriangles(soup, 1);
    }

    private string WriteBake(string name, int bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new System.TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Full blocking compacting collection, repeated so finalizer-queue resurrections go too.</summary>
    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }
    }

    [Test]
    public async Task SameBake_TwoConcurrentAsks_BuildOnce_AndShareTheInstance()
    {
        string bake = WriteBake("collision.tris", 64);
        using ManualResetEventSlim gate = new(false);
        int entered = 0;
        VisibilityEngineCache cache = new(loader: _ =>
        {
            Interlocked.Increment(ref entered);
            gate.Wait();
            return OneTriangle();
        });

        Task<VisibilityEngine> first = cache.GetOrLoadAsync(bake);
        await WaitForAsync(() => Volatile.Read(ref entered) >= 1, "the first build to start");
        Task<VisibilityEngine> second = cache.GetOrLoadAsync(bake);
        // Give a second build every chance to start before the gate opens: if the cache keys wrong,
        // the loader is entered twice here and the count below reads 2.
        await Task.Delay(100);
        gate.Set();

        VisibilityEngine a = await first;
        VisibilityEngine b = await second;

        await Assert.That(b).IsSameReferenceAs(a);
        await Assert.That(entered).IsEqualTo(1).Because("both asks must share one build");
        await Assert.That(cache.Builds).IsEqualTo(1);

        VisibilityEngine c = await cache.GetOrLoadAsync(bake);
        await Assert.That(c).IsSameReferenceAs(a).Because("a later ask after completion is a hit too");
        await Assert.That(entered).IsEqualTo(1);
    }

    /// <summary>
    ///     The realistic re-bake of one map version writes the same triangle count, so the length is
    ///     unchanged and the write time is the only dimension that can catch it.
    /// </summary>
    [Test]
    public async Task RebakedFile_SameLength_NewWriteTime_IsNotServedFromCache()
    {
        string bake = WriteBake("collision.tris", 64);
        VisibilityEngineCache cache = new(loader: _ => OneTriangle());

        VisibilityEngine before = await cache.GetOrLoadAsync(bake);
        DateTime originalWrite = File.GetLastWriteTimeUtc(bake);
        File.WriteAllBytes(bake, new byte[64]);
        File.SetLastWriteTimeUtc(bake, originalWrite.AddMinutes(1));
        VisibilityEngine after = await cache.GetOrLoadAsync(bake);

        await Assert.That(after).IsNotSameReferenceAs(before);
        await Assert.That(cache.Builds).IsEqualTo(2);
        await Assert.That(cache.Count).IsEqualTo(2).Because("the old identity is a distinct key until evicted");
    }

    /// <summary>A copy that lands with its source's timestamp preserved still misses when its size differs.</summary>
    [Test]
    public async Task RebakedFile_SameWriteTime_NewLength_IsNotServedFromCache()
    {
        string bake = WriteBake("collision.tris", 64);
        VisibilityEngineCache cache = new(loader: _ => OneTriangle());

        VisibilityEngine before = await cache.GetOrLoadAsync(bake);
        DateTime originalWrite = File.GetLastWriteTimeUtc(bake);
        File.WriteAllBytes(bake, new byte[128]);
        File.SetLastWriteTimeUtc(bake, originalWrite);
        VisibilityEngine after = await cache.GetOrLoadAsync(bake);

        await Assert.That(after).IsNotSameReferenceAs(before);
        await Assert.That(cache.Builds).IsEqualTo(2);
    }

    /// <summary>
    ///     Windows resolves both spellings to one file, and the two locators in the app (the
    ///     collision env override and the bundle walk-up) can spell the same path differently.
    /// </summary>
    [Test]
    public async Task SamePath_DifferentCase_IsOneIdentity_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("case folding of the key is a Windows-only rule");
        }

        string bake = WriteBake("collision.tris", 64);
        string shouted = bake.ToUpperInvariant();
        await Assert.That(shouted).IsNotEqualTo(bake).Because("the temp path must have letters to fold");
        VisibilityEngineCache cache = new(loader: _ => OneTriangle());

        VisibilityEngine lower = await cache.GetOrLoadAsync(bake);
        VisibilityEngine upper = await cache.GetOrLoadAsync(shouted);

        await Assert.That(upper).IsSameReferenceAs(lower);
        await Assert.That(cache.Builds).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OverCapacity_EvictsLeastRecentlyUsed_AndNeverHoldsMore()
    {
        string a = WriteBake("a.tris", 8);
        string b = WriteBake("b.tris", 16);
        string c = WriteBake("c.tris", 24);
        VisibilityEngineCache cache = new(capacity: 2, loader: _ => OneTriangle());

        VisibilityEngine engineA = await cache.GetOrLoadAsync(a);
        await cache.GetOrLoadAsync(b);
        await cache.GetOrLoadAsync(a); // a is now more recent than b
        await cache.GetOrLoadAsync(c); // evicts b
        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(cache.Builds).IsEqualTo(3);

        VisibilityEngine engineA2 = await cache.GetOrLoadAsync(a);
        await Assert.That(engineA2).IsSameReferenceAs(engineA).Because("a survived the eviction");
        await Assert.That(cache.Builds).IsEqualTo(3);

        await cache.GetOrLoadAsync(b);
        await Assert.That(cache.Builds).IsEqualTo(4).Because("b was the LRU entry and had to be rebuilt");
        await Assert.That(cache.Count).IsEqualTo(2);
    }

    /// <summary>
    ///     More distinct bakes in flight than the cap: the LRU sweep must skip entries still
    ///     building, otherwise a repeat ask for the evicted key starts a second concurrent build of
    ///     the same bake. Once both land, the cap applies and the LRU one goes.
    /// </summary>
    [Test]
    public async Task InFlightBuild_IsNeverEvicted_SoARepeatAskSharesIt()
    {
        string a = WriteBake("a.tris", 8);
        string b = WriteBake("b.tris", 16);
        using ManualResetEventSlim gate = new(false);
        int entered = 0;
        VisibilityEngineCache cache = new(capacity: 1, loader: _ =>
        {
            Interlocked.Increment(ref entered);
            gate.Wait();
            return OneTriangle();
        });

        Task<VisibilityEngine> firstA = cache.GetOrLoadAsync(a);
        await WaitForAsync(() => Volatile.Read(ref entered) >= 1, "a's build to start");
        Task<VisibilityEngine> firstB = cache.GetOrLoadAsync(b);
        await WaitForAsync(() => Volatile.Read(ref entered) >= 2, "b's build to start");
        Task<VisibilityEngine> secondA = cache.GetOrLoadAsync(a);
        // The ask registers on the pool (the file stat runs there); wait for it to land either way.
        await WaitForAsync(() => cache.Hits >= 1 || cache.Builds >= 3, "a's repeat ask to register");
        await Assert.That(cache.Builds).IsEqualTo(2).Because("a is still building and must not have been evicted");
        await Assert.That(cache.Hits).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(2);

        gate.Set();
        VisibilityEngine engineA = await firstA;
        await firstB;
        VisibilityEngine engineA2 = await secondA;

        await Assert.That(engineA2).IsSameReferenceAs(engineA);
        await Assert.That(entered).IsEqualTo(2).Because("each bake was built exactly once");
        await Assert.That(cache.Count).IsEqualTo(1).Because("the cap applies once both builds have landed");

        await cache.GetOrLoadAsync(a);
        await Assert.That(cache.Builds).IsEqualTo(2).Because("a was the most recently used and survived");
        await cache.GetOrLoadAsync(b);
        await Assert.That(cache.Builds).IsEqualTo(3).Because("b was the LRU entry and was the one evicted");
    }

    /// <summary>
    ///     Takes the weak reference on a non-async, non-inlined frame so no local of the engine
    ///     outlives the call; the capacity-1 cache then holds only b.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildThenEvict(VisibilityEngineCache cache, string a, string b)
    {
        WeakReference evicted = new(cache.GetOrLoad(a));
        cache.GetOrLoad(b);
        return evicted;
    }

    [Test]
    public async Task EvictedEngine_IsUnreachable_OnceItsCallersDropIt()
    {
        string a = WriteBake("a.tris", 8);
        string b = WriteBake("b.tris", 16);
        VisibilityEngineCache cache = new(capacity: 1, loader: _ => OneTriangle());

        WeakReference evicted = BuildThenEvict(cache, a, b);
        await Assert.That(cache.Count).IsEqualTo(1);
        Collect();

        await Assert.That(evicted.IsAlive).IsFalse().Because("nothing inside the cache may pin an evicted engine");
    }

    [Test]
    public async Task FailedBuild_IsForgotten_SoTheNextAskRetries()
    {
        string bake = WriteBake("collision.tris", 64);
        int calls = 0;
        VisibilityEngineCache cache = new(loader: _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidDataException("truncated bake");
            }

            return OneTriangle();
        });

        try
        {
            await cache.GetOrLoadAsync(bake);
            throw new InvalidOperationException("expected the first build to throw");
        }
        catch (InvalidDataException)
        {
            // expected
        }

        await Assert.That(cache.Count).IsEqualTo(0).Because("a faulted build must not be replayed");
        VisibilityEngine engine = await cache.GetOrLoadAsync(bake);
        await Assert.That(engine).IsNotNull();
        await Assert.That(cache.Builds).IsEqualTo(2);
    }

    [Test]
    public async Task MissingBake_ThrowsFileNotFound_AndCachesNothing()
    {
        VisibilityEngineCache cache = new(loader: _ => OneTriangle());
        try
        {
            await cache.GetOrLoadAsync(Path.Combine(_dir, "absent.tris"));
            throw new InvalidOperationException("expected FileNotFoundException");
        }
        catch (FileNotFoundException)
        {
            // expected
        }

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.Builds).IsEqualTo(0);
    }

    [Test]
    public async Task CancelledWaiter_DoesNotAbandonTheBuild()
    {
        string bake = WriteBake("collision.tris", 64);
        using ManualResetEventSlim gate = new(false);
        int entered = 0;
        VisibilityEngineCache cache = new(loader: _ =>
        {
            Interlocked.Increment(ref entered);
            gate.Wait();
            return OneTriangle();
        });

        using CancellationTokenSource cts = new();
        Task<VisibilityEngine> abandoned = cache.GetOrLoadAsync(bake, cts.Token);
        await WaitForAsync(() => Volatile.Read(ref entered) >= 1, "the build to start");
        cts.Cancel();
        try
        {
            await abandoned;
            throw new InvalidOperationException("expected the cancelled waiter to throw");
        }
        catch (OperationCanceledException)
        {
            // expected: only this waiter gave up
        }

        gate.Set();
        VisibilityEngine engine = await cache.GetOrLoadAsync(bake);
        await Assert.That(engine).IsNotNull();
        await Assert.That(entered).IsEqualTo(1).Because("the cancelled ask's build was reused, not repeated");
    }

    /// <summary>
    ///     The arm that discriminates against <c>Task.Run(() => loader(path), token)</c>: a token
    ///     signalled BEFORE the loader is entered would then never run the delegate, the entry would
    ///     be forgotten as faulted, and the next ask would build again. Here the build must start
    ///     (Builds reads 1 the moment the cancelled ask throws), run, and serve the next ask.
    /// </summary>
    [Test]
    public async Task PreCancelledWaiter_StillLandsTheBuild_ForTheNextAsk()
    {
        string bake = WriteBake("collision.tris", 64);
        using ManualResetEventSlim gate = new(false);
        int entered = 0;
        VisibilityEngineCache cache = new(loader: _ =>
        {
            Interlocked.Increment(ref entered);
            gate.Wait();
            return OneTriangle();
        });

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        try
        {
            await cache.GetOrLoadAsync(bake, cts.Token);
            throw new InvalidOperationException("expected the pre-cancelled waiter to throw");
        }
        catch (OperationCanceledException)
        {
            // expected: the caller gave up before it ever waited
        }

        await Assert.That(cache.Builds).IsEqualTo(1).Because("the ask registered a build before honouring its token");
        await WaitForAsync(() => Volatile.Read(ref entered) >= 1, "the build to be entered despite the cancelled waiter");
        gate.Set();

        VisibilityEngine engine = await cache.GetOrLoadAsync(bake);
        await Assert.That(engine).IsNotNull();
        await Assert.That(cache.Builds).IsEqualTo(1).Because("the next ask is a hit on the build the cancelled ask started");
        await Assert.That(entered).IsEqualTo(1);
    }

    /// <summary>
    ///     The default loader is <see cref="VisibilityEngine.Load" />; every other test substitutes
    ///     it. Real bake, so it needs <c>CS2DEMOKIT_COLLISION_DIR</c> (or the assets walk-up) to
    ///     resolve de_nuke, and skips saying so. A fresh instance, not <see cref="VisibilityEngineCache.Shared" />,
    ///     so the engine does not stay resident in the test process.
    /// </summary>
    [Test]
    public async Task DefaultLoader_BuildsARealBake_OnceForTwoAsks()
    {
        string? tris = CollisionSoup.Find("de_nuke");
        if (tris is null)
        {
            throw new SkipTestException(
                $"no de_nuke bake: set {CollisionAssetLocator.EnvVar} to the assets directory");
        }

        VisibilityEngineCache cache = new();

        VisibilityEngine first = await cache.GetOrLoadAsync(tris);
        VisibilityEngine second = await cache.GetOrLoadAsync(tris);

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(first.TriangleCount).IsGreaterThan(0);
        await Assert.That(cache.Builds).IsEqualTo(1);
        await Assert.That(cache.Hits).IsEqualTo(1);
    }
}
