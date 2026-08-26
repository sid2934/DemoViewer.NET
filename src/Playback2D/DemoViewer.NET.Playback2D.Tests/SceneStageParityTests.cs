#region

using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <b>D6 G-3, this project's half.</b> <see cref="SceneStage" />'s own doc says its layers are
///     "wired exactly as <c>Scene2DHost</c> wires them… so none of them can quietly test a different
///     layer stack from the one that ships". It is a hand-copied array, so that guarantee ran the wrong
///     way: nothing compared it to anything. Goldens, determinism, allocation and both budget gates
///     build one of these, so a stage that drifted from the catalog would move every one of those
///     numbers without moving a single assertion.
///     <para>
///         The counterpart assertion for <c>Scene2DHost</c> lives in <c>SceneLayerListParityTests</c>
///         (App.Tests, the only project that can see the control). Between them the two remaining
///         hand-written layer lists in the repository are pinned to
///         <see cref="SceneLayerCatalog.SceneStackIds" />.
///     </para>
///     <para>
///         <b>Not equality with the whole table.</b> The four <see cref="SceneLayerIds.OptIn" /> ids are
///         legitimately absent: the ink needs an <c>AnnotationSession</c> and the three HUD layers need
///         an <c>IHudDataSource</c>, neither of which a stage has — which is exactly why
///         <c>SceneStage</c> takes <c>params ISceneLayer[] extra</c> for B2's ink rather than pretending
///         it is one of the fixed seven.
///     </para>
/// </summary>
public class SceneStageParityTests
{
    [Test]
    public async Task SceneStage_BuildsExactlyTheCatalogsNonOptInLayers()
    {
        string[] expected =
        [
            .. SceneLayerCatalog.SceneStackIds.Where(id => !SceneLayerIds.OptIn.Contains(id)).Order()
        ];

        using SceneStage stage = new(new SKSizeI(320, 180));
        string[] built = [.. stage.Compositor.Layers.Select(l => l.Id).Order()];

        Console.WriteLine($"[layers] SceneStage: {string.Join(", ", built)}");
        await Assert.That(built).IsEquivalentTo(expected);
        await Assert.That(built.Length).IsEqualTo(7);
    }

    /// <summary>
    ///     Registration order is not draw order, and the stage's <c>reverseRegistration</c> mode exists
    ///     to prove it. The id SET must be invariant under it — if reversing changed which layers exist
    ///     rather than only when they were added, every "sort order wins" test would be testing a
    ///     different stack from the one it thinks it is.
    /// </summary>
    [Test]
    public async Task ReverseRegistration_ChangesTheOrder_NotTheSet()
    {
        using SceneStage forward = new(new SKSizeI(320, 180));
        using SceneStage reversed = new(new SKSizeI(320, 180), reverseRegistration: true);

        await Assert.That(reversed.Compositor.Layers.Select(l => l.Id).Order().ToArray())
            .IsEquivalentTo(forward.Compositor.Layers.Select(l => l.Id).Order().ToArray());
    }
}
