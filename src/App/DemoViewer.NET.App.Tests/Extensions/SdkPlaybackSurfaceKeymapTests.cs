#region

using Avalonia.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The keymap as an SDK extension reads it through <see cref="SdkPlaybackSurface" />: every action id the
///     registry knows, gesture hints by id, and a key resolved in a named scope, which is how an extension's
///     own focus scope shadows the tab's keys.
/// </summary>
[NotInParallel]
public class SdkPlaybackSurfaceKeymapTests
{
    [Test]
    public async Task ActionFor_ResolvesAKeyInANamedScope_UnderTheUsersKeymap()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        using SdkPlaybackSurface surface = new(vm.Surface, new FaultRig().Guard);

        using (Assert.Multiple())
        {
            await Assert.That(surface.ActionFor(StratBookActions.PaletteScope.Name, Key.M, KeyModifiers.Control))
                .IsEqualTo(StratBookActions.TagNote);
            await Assert.That(surface.ActionFor("playback2d", Key.E, KeyModifiers.None)).IsEqualTo("NextRound");
            await Assert.That(surface.ActionFor("playback2d.tool", Key.Space, KeyModifiers.None)).IsEqualTo("HoldPan");
            await Assert.That(surface.ActionFor("dev.example.nowhere", Key.M, KeyModifiers.Control)).IsNull();
            await Assert.That(surface.ActionFor("", Key.E, KeyModifiers.None)).IsNull();
            await Assert.That(surface.ActionFor("playback2d", Key.Home, KeyModifiers.None)).IsNull()
                .Because("a reserved row resolves to nothing");
        }

        vm.Dispose();
    }

    [Test]
    public async Task ActionIds_AndGestureHints_CoverExtensionActions()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        using SdkPlaybackSurface surface = new(vm.Surface, new FaultRig().Guard);

        using (Assert.Multiple())
        {
            await Assert.That(surface.ActionIds).Contains(StratBookActions.FindRoundsLikeThis);
            await Assert.That(surface.ActionIds).Contains("TogglePlay");
            await Assert.That(surface.ActionIds).DoesNotContain("None");
            await Assert.That(surface.GestureHint(StratBookActions.FindRoundsLikeThis)).IsEqualTo(" (Ctrl+F)");
            await Assert.That(surface.GestureHint("NextRound")).IsEqualTo(" (E)");
            await Assert.That(surface.GestureHint("dev.example.nothing")).IsEqualTo("");
        }

        vm.Dispose();
    }
}
