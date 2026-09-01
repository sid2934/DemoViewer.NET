#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The migration escape hatch, end to end: with the toggle OFF (the shipping default) the v2 host is
///     the only surface in the tree and it renders; with it ON the pre-v2 control mounts and renders.
///     <para>
///         The second half is a parity A/B a user can actually run for one release, and the first half is
///         the claim the removal plan depends on: nothing constructs
///         <c>Playback2DViewport</c> on the default path, so deleting it next release changes no default
///         behaviour. Both write a PNG to the artifact dir for eyeball review.
///     </para>
///     <para>
///         <b>Deleted wholesale by the removal commit</b>: see
///         <c>docs/playback2d-v2/old-control-removal.md</c>.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DLegacyToggleTests
{
    /// <summary>The escape hatch ships OFF. A default of true would make v2 dead code on every install.</summary>
    [Test]
    public async Task LegacyViewport_DefaultsOff() =>
        await Assert.That(new AppSettings().Playback2D.LegacyViewport).IsFalse();

    [Test]
    public async Task ToggleOff_MountsScene2DHost_AndRendersAFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers(
                (0, 2, -800f, 600f, 64f, 90f),
                (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            try
            {
                await Assert.That(Playback2DTimelineHarness.Surface(view)).IsTypeOf<Scene2DHost>();

                // The claim the removal plan rests on: the OLD control is not hidden, it is never
                // constructed. A view that built both and showed one would keep the old control's
                // per-frame work alive behind the toggle.
                await Assert.That(view.GetVisualDescendants().OfType<Playback2DViewport>().Any()).IsFalse()
                    .Because("with the toggle off no Playback2DViewport instance may exist at all");

                Playback2DTimelineHarness.SceneHost(view).FitToExtent();
                Playback2DTimelineHarness.Pump();

                await Assert.That(Capture(window, "legacy-toggle-off.png")).IsTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Test]
    public async Task ToggleOn_MountsLegacyViewport_AndRendersAFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.PushMarkers(
                (0, 2, -800f, 600f, 64f, 90f),
                (1, 3, 900f, -500f, 64f, 270f));

            (Window window, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Legacy);
            try
            {
                await Assert.That(Playback2DTimelineHarness.Surface(view)).IsTypeOf<Playback2DViewport>();
                await Assert.That(view.GetVisualDescendants().OfType<Scene2DHost>().Any()).IsFalse();

                Playback2DTimelineHarness.Viewport(view).FitToExtent();
                Playback2DTimelineHarness.Pump();

                await Assert.That(Capture(window, "legacy-toggle-on.png")).IsTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static bool Capture(Window window, string fileName)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            return false;
        }

        string path = Path.Combine(HeadlessSession.ArtifactDir, fileName);
        frame.Save(path);
        Console.WriteLine($"[legacy-toggle] {path}");
        return true;
    }
}
