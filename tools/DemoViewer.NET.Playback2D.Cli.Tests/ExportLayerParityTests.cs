#region

using DemoViewer.NET.Playback2D.Core.Layers;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     What a bare <c>dv2d export</c> draws, and what <c>--layers</c> does to it.
///     <para>
///         <c>export.md</c> claimed "a request the dialog accepts is a request the CLI accepts", which was
///         true of the validator and quietly false of the picture: the CLI's default set named
///         <c>playback2d.vision</c> where the app's does not, so the same request rendered a different
///         video depending on which front end ran it. Worse, the CLI has no visibility engine to feed
///         <c>VisionLayer</c>, so every one of those <c>--json</c> manifests listed a layer that drew
///         nothing.
///     </para>
/// </summary>
public class ExportLayerParityTests
{
    /// <summary>
    ///     A bare export names neither the opt-ins nor vision. Both would be manifest lies: the ink and
    ///     the HUD have no source to draw from, and the CLI has no visibility engine at all.
    /// </summary>
    [Test]
    public async Task ABareExport_NamesNeitherTheOptIns_NorVision()
    {
        HashSet<string> ids = ExportCommand.BuildLayerIds(null, false, false);

        await Assert.That(ids.Contains(SceneLayerIds.Annotations)).IsFalse();
        await Assert.That(ids.Contains(SceneLayerIds.Vision)).IsFalse()
            .Because("the app's dialog ships vision OFF — it is the frame's biggest per-frame cost");
    }

    [Test]
    public async Task AnExplicitLayerList_StillWins()
    {
        // --layers is the escape hatch, including for vision: naming it is a choice, and the defaults
        // above must not fight it.
        HashSet<string> ids = ExportCommand.BuildLayerIds(
            [SceneLayerIds.Radar, SceneLayerIds.Vision], false, false);

        await Assert.That(ids.Order()).IsEquivalentTo(
            new[]
            {
                SceneLayerIds.Radar, SceneLayerIds.Vision
            }.Order());
    }
}
