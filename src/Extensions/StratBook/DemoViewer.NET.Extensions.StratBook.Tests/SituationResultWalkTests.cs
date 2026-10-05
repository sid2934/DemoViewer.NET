#region

using Avalonia.Input;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     J / K in 2D playback: the keymap rows and the shipped seam resolving the tab lazily. The dispatch
///     onto the walk seam (unhandled without one, the seam's own answer otherwise, the Situations tab's
///     feature gate) is <c>SituationsPlaybackContributionTests</c>.
/// </summary>
public class SituationResultWalkTests
{
    [Test]
    public async Task JAndK_AreTheResultWalk_AlwaysScoped_AndNotReserved()
    {
        // NextSituationResult/PrevSituationResult are Strat Book extension commands: the profile a tab
        // routes through resolves them, the bare core table (Playback2DKeymap) does not.
        Playback2DKeymapProfile keymap = Playback2DKeymapProfile.Default;
        using (Assert.Multiple())
        {
            await Assert.That(keymap.TryResolve(Key.J, KeyModifiers.None, false, out string? next)).IsTrue();
            await Assert.That(next).IsEqualTo(StratBookActions.NextSituationResult);
            await Assert.That(keymap.TryResolve(Key.K, KeyModifiers.None, false, out string? prev)).IsTrue();
            await Assert.That(prev).IsEqualTo(StratBookActions.PrevSituationResult);

            // A drawing tool does not take the keys away: the rows are Always-scoped.
            await Assert.That(keymap.TryResolve(Key.J, KeyModifiers.None, true, out string? tool)).IsTrue();
            await Assert.That(tool).IsEqualTo(StratBookActions.NextSituationResult);

            await Assert.That(keymap.GestureText(StratBookActions.NextSituationResult)).IsEqualTo("J");
            await Assert.That(keymap.GestureText(StratBookActions.PrevSituationResult)).IsEqualTo("K");
            await Assert.That(Playback2DKeymap.ReservedGestures(true)).DoesNotContain((Key.J, KeyModifiers.None));
            await Assert.That(Playback2DKeymap.ReservedGestures(true)).DoesNotContain((Key.K, KeyModifiers.None));
        }
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
}
