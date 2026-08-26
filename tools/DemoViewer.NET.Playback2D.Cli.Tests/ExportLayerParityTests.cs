#region

using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <b>Cross-surface layer parity, the CLI half.</b> Its counterpart is
///     <c>Playback2DExportDialogTests.TheShippedIncludeSet_IsTheCliesFullOverlaySet</c> in the App suite,
///     which pins <c>Playback2DExportDialogViewModel.BuildLayerIds</c> to the same two expressions from
///     the other side. Two assemblies, because the App suite carries Avalonia and this one is forbidden
///     to — but both derive their expectation from the same Core sets, so adding a layer to
///     <c>SceneStackIds</c> moves both and changing one front end's defaults moves only one.
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
    [Test]
    public async Task BareExport_IsTheSceneWithoutTheOptInsAndWithoutVision()
    {
        HashSet<string> ids = ExportCommand.BuildLayerIds(null, hud: false, hasInk: false);

        await Assert.That(ids.Order()).IsEquivalentTo(BareSceneSet().Order());
        await Assert.That(ids.Contains(SceneLayerIds.Vision)).IsFalse()
            .Because("the app's dialog ships vision OFF — it is the frame's biggest per-frame cost");
    }

    [Test]
    public async Task HudAndAnnotations_ReachTheDialogsShippedSet()
    {
        HashSet<string> ids = ExportCommand.BuildLayerIds(null, hud: true, hasInk: true);

        await Assert.That(ids.Order()).IsEquivalentTo(FullOverlaySet().Order());
    }

    /// <summary>
    ///     <c>--annotations</c> against a demo with no sidecar must not NAME the ink layer. That is the
    ///     same manifest lie <c>BuildLayerIds</c>' own comment records for the default set: a
    ///     <c>layers</c> array listing something the render starved and skipped is a claim a later golden
    ///     diff has to chase.
    /// </summary>
    [Test]
    public async Task AskingForInkWithNoSidecar_NamesNoInkLayer()
    {
        HashSet<string> ids = ExportCommand.BuildLayerIds(null, hud: false, hasInk: false);

        await Assert.That(ids.Contains(SceneLayerIds.Annotations)).IsFalse();
    }

    [Test]
    public async Task AnExplicitLayerList_StillWins()
    {
        // --layers is the escape hatch, including for vision: naming it is a choice, and the defaults
        // above must not fight it.
        HashSet<string> ids = ExportCommand.BuildLayerIds(
            [SceneLayerIds.Radar, SceneLayerIds.Vision], hud: false, hasInk: false);

        await Assert.That(ids.Order()).IsEquivalentTo(
            new[] { SceneLayerIds.Radar, SceneLayerIds.Vision }.Order());
    }

    /// <summary>Every scene-stack id except vision — the dialog with HUD and annotations on.</summary>
    private static IEnumerable<string> FullOverlaySet() =>
        SceneLayerCatalog.SceneStackIds
            .Where(id => !string.Equals(id, SceneLayerIds.Vision, StringComparison.Ordinal));

    /// <summary>Every non-opt-in scene-stack id except vision — the dialog with every Include off.</summary>
    private static IEnumerable<string> BareSceneSet() =>
        SceneLayerCatalog.SceneStackIds
            .Where(id => !SceneLayerIds.OptIn.Contains(id) &&
                         !string.Equals(id, SceneLayerIds.Vision, StringComparison.Ordinal));
}
