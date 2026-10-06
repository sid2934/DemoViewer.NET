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

    [Test]
    public async Task ActionFor_AndGestureText_ReadTheUsersRows_OverTheShippedTable()
    {
        HostKeymap shipped = new(() => CommandRegistry.Default.EffectiveBindings);
        HostKeymap rebound = new(() => CommandRegistry.Default.EffectiveBindings, () => ["NextRound=Shift+W"]);

        using (Assert.Multiple())
        {
            await Assert.That(shipped.ActionFor("playback2d", Key.E, KeyModifiers.None)).IsEqualTo("NextRound");
            await Assert.That(shipped.GestureText("NextRound")).IsEqualTo("E");
            await Assert.That(rebound.ActionFor("playback2d", Key.W, KeyModifiers.Shift)).IsEqualTo("NextRound");
            await Assert.That(rebound.ActionFor("playback2d", Key.E, KeyModifiers.None)).IsNotEqualTo("NextRound");
            await Assert.That(rebound.GestureText("NextRound")).IsEqualTo("Shift+W");
            await Assert.That(rebound.ActionFor("", Key.W, KeyModifiers.Shift)).IsNull();
            await Assert.That(rebound.GestureText("NoSuchAction")).IsEqualTo("");
        }
    }

    [Test]
    public async Task Refresh_RaisesChanged_OnlyWhenTheRowsDiffer()
    {
        string[] rows = [];
        HostKeymap keymap = new(() => CommandRegistry.Default.EffectiveBindings, () => rows);
        int changed = 0;
        keymap.Changed += () => changed++;

        _ = keymap.GestureText("NextRound");
        keymap.Refresh();
        await Assert.That(changed).IsEqualTo(0).Because("nothing changed since the rows were read");

        rows = ["NextRound=Shift+W"];
        keymap.Refresh();
        using (Assert.Multiple())
        {
            await Assert.That(changed).IsEqualTo(1);
            await Assert.That(keymap.GestureText("NextRound")).IsEqualTo("Shift+W");
        }

        keymap.Refresh();
        await Assert.That(changed).IsEqualTo(1);
    }
}
