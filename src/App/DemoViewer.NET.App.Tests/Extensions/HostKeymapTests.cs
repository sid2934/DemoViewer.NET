#region

using Avalonia.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The keymap extensions read is the composed table and the reserved lists, and gestures read as the app writes them.</summary>
public class HostKeymapTests
{
    [Test]
    public async Task TheKeymap_IsTheComposedTable_AndTheReservedLists()
    {
        HostKeymap keymap = HostKeymap.Instance;

        using (Assert.Multiple())
        {
            await Assert.That(keymap.Bindings.Count).IsEqualTo(CommandRegistry.Default.EffectiveBindings.Count);
            await Assert.That(keymap.Bindings.Any(b => b.ActionId == "NextRound" && b.Gesture.Key == Key.E
                                                       && b.Gesture.KeyModifiers == KeyModifiers.None && b.Scope == "playback2d"))
                .IsTrue();
            await Assert.That(keymap.ShellReserved.Count).IsEqualTo(Playback2DKeymap.ShellReservedGestures.Count);
            await Assert.That(keymap.BrowserReserved.Count).IsEqualTo(Playback2DKeymap.BrowserReservedGestures.Count);
        }
    }

    [Test]
    [Arguments(Key.Left, KeyModifiers.None, "←")]
    [Arguments(Key.Escape, KeyModifiers.None, "Esc")]
    [Arguments(Key.F, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+F")]
    [Arguments(Key.K, KeyModifiers.Meta, "Meta+K")]
    [Arguments(Key.OemOpenBrackets, KeyModifiers.Alt, "Alt+[")]
    public async Task GestureText_IsTheAppsSpelling(Key key, KeyModifiers modifiers, string expected)
    {
        await Assert.That(KeyGestureText.Format(key, modifiers)).IsEqualTo(expected);
        await Assert.That(Playback2DKeymap.Format(key, modifiers)).IsEqualTo(expected);
    }
}
