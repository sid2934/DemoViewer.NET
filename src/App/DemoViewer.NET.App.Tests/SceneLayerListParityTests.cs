#region

using Avalonia.Controls;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Six places name a layer stack and only one derives it from <see cref="SceneLayerCatalog" />, so a
///     new layer can ship missing from one of the hand-written arrays. The one that ships is
///     <c>Scene2DHost.BuildScene</c>, asserted here directly against the catalog.
///     <para>
///         The list is not a copy safe to delete in favor of a shared factory: <c>Scene2DHost</c> holds
///         typed fields for the layers it re-binds per frame and hands <c>VisionLayer</c> a live
///         <c>VisibilityEngineSolver</c>, so forcing it through <c>CreateSceneStack</c> would trade one
///         drift risk for a worse one: a factory with two callers and five opinions. What must not
///         differ is the <b>id set</b>.
///     </para>
///     <para>
///         The four <see cref="SceneLayerIds.OptIn" /> ids are absent from every scene stack (they need a
///         HUD source or an ink document only an export supplies), and <c>Scene2DHost</c> mounts
///         <c>playback2d.annotations</c> later, when a session attaches. The claim under test is "the
///         non-opt-in set is identical", not "the lists are equal".
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
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
            // after BuildScene and adds playback2d.annotations then, the live document the user is
            // drawing into. An export gets a frozen copy instead, so the catalog leaves the id opt-in
            // rather than making it one of the seven.
            string[] scene = [.. mounted.Where(id => !SceneLayerIds.OptIn.Contains(id))];
            await Assert.That(scene).IsEquivalentTo(SceneIds.Order().ToArray());

            // Not a restatement: an equivalence check would still pass against an accidentally short
            // catalog, so the count is pinned independently.
            await Assert.That(scene.Length).IsEqualTo(7);

            // And the ONLY opt-in id a window may hold. The three HUD layers are burned-in export
            // chrome, the window draws its scoreboard, clock and kill feed in XAML, so one appearing
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
    ///     persisted keys, a saved export preset, a feature gate and the layer panel all store them, so
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

        // KnownLayerIds is an alias, not a second table, so a rename only has one place to happen.
        // Asserted by reference so a future "helpful" copy of the list is caught, not just a divergence.
        await Assert.That(ReferenceEquals(SceneLayerCatalog.KnownLayerIds,
            SceneLayerCatalog.SceneStackIds)).IsTrue();
    }
}
