#region

using Avalonia.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>D6 G-3.</b> Six places named a layer stack and exactly one derived it from
///     <see cref="SceneLayerCatalog" />, so adding a scene layer meant editing four hand-written arrays
///     and a new layer that learned three of them shipped missing from the fourth. Two of the six are
///     gone (the catalog's second registration table, and <c>ExportCommand.BuildLayerIds</c>, which now
///     projects <see cref="SceneLayerCatalog.SceneStackIds" />); the remaining hand-written ones are
///     <c>Scene2DHost.BuildScene</c> — asserted here — and <c>SceneStage</c>, asserted by
///     <c>SceneStageParityTests</c> in the project that can see it.
///     <para>
///         <b>Why an assertion and not a shared factory.</b> Neither list is a copy that <i>should</i>
///         be deleted. <c>Scene2DHost</c> holds typed fields for the three layers it re-binds per frame
///         and hands <c>VisionLayer</c> a live <c>VisibilityEngineSolver</c>; <c>SceneStage</c> needs the
///         same handles plus a reverse-registration mode that proves draw order beats registration
///         order. Forcing both through <c>CreateSceneStack</c> would trade a drift risk for a worse
///         one — a factory with two callers and five opinions. What must not differ is the <b>id set</b>,
///         and that is exactly what these assert.
///     </para>
///     <para>
///         <b>What legitimately differs, and why each difference is spelled out rather than tolerated:</b>
///         the four <see cref="SceneLayerIds.OptIn" /> ids are absent from every scene stack (they need a
///         HUD source or an ink document that only an export supplies), and <c>Scene2DHost</c> mounts
///         <c>playback2d.annotations</c> <i>later</i>, when a session is attached — which is why the
///         claim under test is "the non-opt-in set is identical", not "the lists are equal".
///     </para>
/// </summary>
[NotInParallel]
public class SceneLayerListParityTests
{
    /// <summary>The seven ids a scene stack must hold: every catalog id that is not opt-in.</summary>
    private static string[] SceneIds =>
        [.. SceneLayerCatalog.SceneStackIds.Where(id => !SceneLayerIds.OptIn.Contains(id))];

    /// <summary>
    ///     The window's own stack. <c>Scene2DHost.BuildScene</c> writes seven <c>compositor.Add</c> calls
    ///     by hand; if the catalog grows an eighth scene layer and this one does not, the app silently
    ///     stops drawing what every export and every golden draws.
    /// </summary>
    [Test]
    public async Task Scene2DHost_MountsExactlyTheCatalogsNonOptInLayers()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);

            Scene2DHost host = Playback2DTimelineHarness.SceneHost(view);
            string[] mounted = [.. host.Compositor.Layers.Select(l => l.Id).Order()];
            Console.WriteLine($"[layers] Scene2DHost: {string.Join(", ", mounted)}");

            // The SCENE half, exactly. Split rather than compared whole because the host mounts one
            // opt-in layer the CLI cannot: AttachAnnotationsToCurrentDemo binds an AnnotationSession
            // after BuildScene and adds playback2d.annotations then, which is the live document the user
            // is drawing into. An export gets a frozen copy instead, which is why the catalog leaves the
            // id opt-in rather than making it one of the seven.
            string[] scene = [.. mounted.Where(id => !SceneLayerIds.OptIn.Contains(id))];
            await Assert.That(scene).IsEquivalentTo(SceneIds.Order().ToArray());

            // Not a restatement: it is the assertion that would have failed while the catalog registered
            // playback2d.debuggrid and nothing else, which is the shape of G-1.
            await Assert.That(scene.Length).IsEqualTo(7);

            // And the ONLY opt-in id a window may hold. The three HUD layers are burned-in export
            // chrome — the window draws its scoreboard, clock and kill feed in XAML — so one appearing
            // here would mean an export-only layer had leaked into the interactive stack.
            string[] optIn = [.. mounted.Where(SceneLayerIds.OptIn.Contains)];
            await Assert.That(optIn.Length).IsLessThanOrEqualTo(1);
            foreach (string id in optIn)
            {
                await Assert.That(id).IsEqualTo(SceneLayerIds.Annotations);
            }

            window.Close();
        });
    }

    /// <summary>
    ///     The catalog's own table, pinned by name and order. <see cref="SceneLayerCatalog" /> ids are
    ///     persisted keys — a saved export preset, a feature gate and the layer panel all store them — so
    ///     a rename is a silent data migration, and this is the tripwire for one. Order is asserted too:
    ///     it is the registration order a compositor receives, and while <c>ISceneLayer.Order</c> is what
    ///     actually decides draw order, a reader of the table has every right to expect the two agree.
    /// </summary>
    [Test]
    public async Task CatalogTable_IsTheElevenPersistedIds_InDrawOrder()
    {
        string[] expected =
        [
            "playback2d.radar", "playback2d.trails", "playback2d.areaeffects", "playback2d.vision",
            "playback2d.markers", "playback2d.bomb", "playback2d.floorlabel", "playback2d.annotations",
            "hud.roster", "hud.clock", "hud.killfeed"
        ];

        await Assert.That(SceneLayerCatalog.SceneStackIds.ToArray()).IsEquivalentTo(expected);

        // KnownLayerIds is an alias, not a second table — the whole point of the D6 fold. Asserted by
        // reference so a future "helpful" copy of the list is caught rather than a divergence later.
        await Assert.That(ReferenceEquals(SceneLayerCatalog.KnownLayerIds,
            SceneLayerCatalog.SceneStackIds)).IsTrue();
    }
}
