#region

using System.Numerics;
using System.Runtime.CompilerServices;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using TUnit.Assertions.Enums;
using DemoViewer.NET.Extensions.StratBook.Services;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The projectile walk Suggested Tags and the grenade walk share on a visit, over a counting walk seam
///     and synthetic samples. No demo is parsed.
/// </summary>
public class SharedSamplerWalkTests
{
    private const string Path = "demo.dem";
    private static readonly int[] OneStride1Walk = [1];
    private static readonly int[] OneStride8Walk = [SharedSamplerWalk.RemovedStride];
    private static readonly int[] Stride1ThenStride8 = [1, SharedSamplerWalk.RemovedStride];

    // Two projectiles over twelve frames: one removed at frame 5, one created at 6 and removed at 11.
    private static readonly ProjectileSample[] Stride1 =
    [
        .. Enumerable.Range(0, 5).Select(f => Sample(f, 100, created: f == 0)),
        Sample(5, 100, removed: true),
        .. Enumerable.Range(6, 5).Select(f => Sample(f, 101, created: f == 6)),
        Sample(11, 101, removed: true)
    ];

    [Test]
    public async Task SuggestedTagsFirst_DecodesOnceAtStride1_AndHandsTheGrenadeWalkTheSameStream()
    {
        CountingWalk walk = new();
        SharedSamplerWalk shared = new((_, _) => true, _ => true, walk.Walk);
        ParsedDemo demo = SyntheticParsedDemo.Create();

        List<ProjectileSample> removed = shared.Removed(Path, demo);
        await Assert.That(shared.Held).IsEqualTo(1);
        List<ProjectileSample> all = [.. shared.All(Path, demo)];

        using (Assert.Multiple())
        {
            await Assert.That(walk.Strides).IsEquivalentTo(OneStride1Walk);
            await Assert.That(removed).IsEquivalentTo(Stride1.Where(s => s.Removed), CollectionOrdering.Matching);
            await Assert.That(all).IsEquivalentTo(Stride1, CollectionOrdering.Matching);
            await Assert.That(shared.Held).IsEqualTo(0).Because("each consumer's share goes as it is taken");
        }
    }

    [Test]
    public async Task GrenadesFirst_StreamsOnce_AndKeepsOnlyTheRemovedSamplesForSuggestedTags()
    {
        CountingWalk walk = new();
        SharedSamplerWalk shared = new((_, _) => true, _ => true, walk.Walk);
        ParsedDemo demo = SyntheticParsedDemo.Create();

        List<ProjectileSample> all = [.. shared.All(Path, demo)];
        await Assert.That(shared.Held).IsEqualTo(1);
        List<ProjectileSample> removed = shared.Removed(Path, demo);

        using (Assert.Multiple())
        {
            await Assert.That(walk.Strides).IsEquivalentTo(OneStride1Walk);
            await Assert.That(all).IsEquivalentTo(Stride1, CollectionOrdering.Matching);
            await Assert.That(removed).IsEquivalentTo(Stride1.Where(s => s.Removed), CollectionOrdering.Matching);
            await Assert.That(shared.Held).IsEqualTo(0);
        }
    }

    [Test]
    public async Task WithNoGrenadeWalkComing_TheFixUpWalksAtStride8_AndHoldsNothing()
    {
        CountingWalk walk = new();
        SharedSamplerWalk shared = new((_, _) => false, _ => true, walk.Walk);

        List<ProjectileSample> removed = shared.Removed(Path, SyntheticParsedDemo.Create());

        using (Assert.Multiple())
        {
            await Assert.That(walk.Strides).IsEquivalentTo(OneStride8Walk);
            await Assert.That(removed).IsEquivalentTo(Stride1.Where(s => s.Removed), CollectionOrdering.Matching);
            await Assert.That(shared.Held).IsEqualTo(0);
        }
    }

    [Test]
    public async Task WithNoSuggestedTagsComing_TheGrenadeWalkHoldsNothing()
    {
        SharedSamplerWalk shared = new((_, _) => true, _ => false, new CountingWalk().Walk);

        List<ProjectileSample> all = [.. shared.All(Path, SyntheticParsedDemo.Create())];

        await Assert.That(all.Count).IsEqualTo(Stride1.Length);
        await Assert.That(shared.Held).IsEqualTo(0);
    }

    [Test]
    public async Task AnAbandonedGrenadeWalk_LeavesNoPartialRemovedSet()
    {
        CountingWalk walk = new();
        SharedSamplerWalk shared = new((_, _) => false, _ => true, walk.Walk);
        ParsedDemo demo = SyntheticParsedDemo.Create();

        _ = shared.All(Path, demo).Take(7).ToList();
        List<ProjectileSample> removed = shared.Removed(Path, demo);

        using (Assert.Multiple())
        {
            await Assert.That(walk.Strides).IsEquivalentTo(Stride1ThenStride8, CollectionOrdering.Matching);
            await Assert.That(removed.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ABoundedGrenadeWalk_TakesTheSharedStreamCutAtTheBound()
    {
        CountingWalk walk = new();
        SharedSamplerWalk shared = new((_, _) => true, _ => true, walk.Walk);
        ParsedDemo demo = SyntheticParsedDemo.Create();

        _ = shared.Removed(Path, demo);
        List<ProjectileSample> bounded = [.. shared.All(Path, demo, maxFrames: 6)];

        using (Assert.Multiple())
        {
            await Assert.That(walk.Strides.Count).IsEqualTo(1);
            await Assert.That(bounded).IsEquivalentTo(Stride1.Where(s => s.FrameIndex < 6), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ABoundedGrenadeWalk_IsNotKeptForTheFixUp()
    {
        CountingWalk walk = new();
        SharedSamplerWalk shared = new((_, _) => false, _ => true, walk.Walk);
        ParsedDemo demo = SyntheticParsedDemo.Create();

        _ = shared.All(Path, demo, maxFrames: 6).ToList();

        await Assert.That(shared.Held).IsEqualTo(0);
    }

    [Test]
    public async Task TheMemoLetsGoWithTheParse()
    {
        SharedSamplerWalk shared = new((_, _) => true, _ => true, new CountingWalk().Walk);
        WeakReference parse = HoldForGrenades(shared);

        for (int i = 0; i < 5 && parse.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        using (Assert.Multiple())
        {
            await Assert.That(parse.IsAlive).IsFalse();
            await Assert.That(shared.Held).IsEqualTo(0);
        }
    }

    // Leaves the stride-1 share on a parse nothing else references.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference HoldForGrenades(SharedSamplerWalk shared)
    {
        ParsedDemo demo = SyntheticParsedDemo.Create();
        _ = shared.Removed(Path, demo);
        return new WeakReference(demo);
    }

    private static ProjectileSample Sample(int frame, int index, bool created = false, bool removed = false) =>
        new(frame, frame * 2, index, 1, "CSmokeGrenadeProjectile", 3, new Vector3(frame, 0, 0), Vector3.Zero, Vector3.One, 0,
            created, removed);

    private sealed class CountingWalk
    {
        public List<int> Strides { get; } = [];

        // The sampler's contract: a strided walk is the stride-1 walk filtered to the stride, Created and Removed.
        public IEnumerable<ProjectileSample> Walk(ParsedDemo demo, int stride, int maxFrames)
        {
            Strides.Add(stride);
            return Stride1.Where(s => s.FrameIndex < maxFrames && (s.FrameIndex % stride == 0 || s.Created || s.Removed));
        }
    }
}
