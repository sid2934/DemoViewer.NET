#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     J / K in 2D playback: the keymap rows, the tab's dispatch onto the walk seam (unhandled without
///     one, and the seam's own answer otherwise), and the shipped seam resolving the tab lazily.
/// </summary>
public class SituationResultWalkTests
{
    [Test]
    public async Task JAndK_AreTheResultWalk_AlwaysScoped_AndNotReserved()
    {
        using (Assert.Multiple())
        {
            await Assert.That(Playback2DKeymap.TryResolve(Key.J, KeyModifiers.None, false, out Playback2DAction next)).IsTrue();
            await Assert.That(next).IsEqualTo(Playback2DAction.NextSituationResult);
            await Assert.That(Playback2DKeymap.TryResolve(Key.K, KeyModifiers.None, false, out Playback2DAction prev)).IsTrue();
            await Assert.That(prev).IsEqualTo(Playback2DAction.PrevSituationResult);

            // A drawing tool does not take the keys away: the rows are Always-scoped.
            await Assert.That(Playback2DKeymap.TryResolve(Key.J, KeyModifiers.None, true, out Playback2DAction tool)).IsTrue();
            await Assert.That(tool).IsEqualTo(Playback2DAction.NextSituationResult);

            await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.NextSituationResult)).IsEqualTo("J");
            await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.PrevSituationResult)).IsEqualTo("K");
            await Assert.That(Playback2DKeymap.ReservedGestures(true)).DoesNotContain((Key.J, KeyModifiers.None));
            await Assert.That(Playback2DKeymap.ReservedGestures(true)).DoesNotContain((Key.K, KeyModifiers.None));
        }
    }

    [Test]
    public async Task ThePlaybackTab_HandsTheDirectionToTheSeam_AndLeavesTheKeyUnhandledWithoutOne()
    {
        Playback2DTabViewModel vm = new();
        Playback2DFakeContext ctx = new();
        vm.OnActivated(ctx);
        RecordingWalk walk = new();
        vm.SituationResults = walk;

        using (Assert.Multiple())
        {
            await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsTrue();
            await Assert.That(vm.ExecuteAction(Playback2DAction.PrevSituationResult)).IsTrue();
            await Assert.That(walk.Directions).IsEquivalentTo([1, -1]);
        }

        // The seam says there is nothing to walk to: the key stays unhandled, exactly as the seam said.
        walk.Answer = false;
        await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsFalse();
        await Assert.That(walk.Directions.Count).IsEqualTo(3);

        // No seam (a host without the Situations module): unhandled without asking anything.
        vm.SituationResults = null;
        await Assert.That(vm.ExecuteAction(Playback2DAction.NextSituationResult)).IsFalse();
        await Assert.That(vm.ExecuteAction(Playback2DAction.PrevSituationResult)).IsFalse();

        // The walk seeks through the seam's own funnel, never through the tab's context.
        await Assert.That(ctx.SeekTicks).IsEmpty();
        await Assert.That(ctx.SeekFrames).IsEmpty();
    }

    [Test]
    public async Task TheShippedSeam_ResolvesTheTabOnFirstUse_AndWalksItsCards()
    {
        int resolved = 0;
        SituationsTabViewModel? tab = null;
        SituationResultWalk seam = new(() =>
        {
            resolved++;
            return tab ?? throw new InvalidOperationException("resolved before the tab existed");
        });

        DemoViewer.NET.Services.DemoCache.DemoCacheStore cache = new(null);
        using DemoViewer.NET.Services.RoundIndex.RoundIndexStore store = new(null, cache);
        DemoViewer.NET.Services.RoundIndex.RoundIndexPlaceSources sources = new(() => DemoViewer.NET.Services.RoundIndex.RoundIndexTokenSource.Pawn);
        using DemoViewer.NET.Services.RoundIndex.SituationIndex index = new(cache, store, sources);
        index.Load();
        ResultCardTests.RecordingPlayback playback = new();
        ResultCardsViewModel results = new(cache, store, sources, () => playback, post: a => a(), decode: _ => null,
            renderer: () => new SituationThumbnailRenderer(_ => null));
        using SituationsTabViewModel built = new(index, null, cache, sources, () => DemoViewer.NET.Services.RoundIndex.RoundIndexTokenSource.Pawn,
            false, results: results);
        tab = built;

        await Assert.That(resolved).IsEqualTo(0).Because("lazy: nothing resolved at construction");
        await Assert.That(seam.Walk(+1)).IsFalse().Because("no result set yet");

        results.Load([new DemoViewer.NET.Services.RoundIndex.SituationHit("/d/a.dem", "k", null, "de_nuke", 1, 1000, 4008, 4100, 2)]);
        await results.BatchTask;
        await Assert.That(seam.Walk(+1)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(resolved).IsEqualTo(2);
            await Assert.That(results.SelectedIndex).IsEqualTo(1);
            await Assert.That(playback.Seeks).IsEquivalentTo([("/d/a.dem", 3368)]);
        }
    }

    private sealed class RecordingWalk : ISituationResultWalk
    {
        public List<int> Directions { get; } = [];

        public bool Answer { get; set; } = true;

        public bool Walk(int direction)
        {
            Directions.Add(direction);
            return Answer;
        }
    }
}
