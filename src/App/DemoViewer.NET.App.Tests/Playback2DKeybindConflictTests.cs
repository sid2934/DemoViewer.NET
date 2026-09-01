#region

using System.Text.RegularExpressions;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The guard that catches a FUTURE shell binding stealing a 2D key. It reads
///     <c>MainView.axaml</c>'s own text rather than a copy of the list, so adding a
///     <c>&lt;KeyBinding Gesture="Q" …&gt;</c> to the shell fails here instead of silently shadowing round
///     navigation inside the tab.
///     <para>
///         <b>Conflicts only.</b> Which gesture each action carries is <c>Playback2DKeymapTests</c>'
///         business, and the shipped table's conflict-freedom is the static constructor's: restating
///         both here once made a rebind a three-file edit, and a real duplicate threw
///         <c>TypeInitializationException</c> before any of the three ran. The single global
///         text-input-suppression rule is asserted where it lives, in
///         <c>Playback2DKeyRoutingTests.TextBoxFocused_KeysAreNotIntercepted</c>.
///     </para>
/// </summary>
public class Playback2DKeybindConflictTests
{
    [Test]
    public async Task ShellReservedGestures_MatchesMainViewAxaml()
    {
        // The keymap's own copy of the shell list is what the static ctor checks against; if the two
        // drift, the ctor's guarantee quietly weakens.
        HashSet<(Key, KeyModifiers)> fromSource = [.. ParseShellGestures()];
        HashSet<(Key, KeyModifiers)> declared = [.. Playback2DKeymap.ShellReservedGestures];

        Console.WriteLine($"[keybind-audit] shell accelerators parsed from MainView.axaml: {fromSource.Count}");
        await Assert.That(declared.SetEquals(fromSource)).IsTrue();
    }

    /// <summary>
    ///     Erase and round navigation must never share a gesture. Both were once assigned <c>E</c>, "Q/E
    ///     round nav" and "erase" colliding; whichever keys the two end up on,
    ///     <b>
    ///         they must not be the
    ///         same one
    ///     </b>
    ///     , or picking up the pen shadows round navigation. Which key each actually is
    ///     belongs to <c>Playback2DKeymapTests</c>; pinning it here as well would make a rebind a
    ///     two-file edit.
    /// </summary>
    [Test]
    public async Task Erase_AndRoundNav_AreDifferentGestures()
    {
        Playback2DBinding erase = Single(Playback2DAction.ToolErase);
        Playback2DBinding nextRound = Single(Playback2DAction.NextRound);

        Console.WriteLine($"[keybind-audit] erase={Playback2DKeymap.Format(erase.Key, erase.Modifiers)} "
                          + $"nextRound={Playback2DKeymap.Format(nextRound.Key, nextRound.Modifiers)}");

        await Assert.That((erase.Key, erase.Modifiers)).IsNotEqualTo((nextRound.Key, nextRound.Modifiers));
    }

    /// <summary>
    ///     The transport owns the arrow keys and the speed ladder, at <c>Always</c> scope. The
    ///     player-card <c>ItemsControl</c> is independently selectable, so an arrow that reaches the list
    ///     instead of the transport is a silently dead key. The routing half is
    ///     <c>Playback2DKeyRoutingTests.ArrowKeys_DoNotChangeListBoxSelection</c>; this half pins that the
    ///     table still claims them.
    /// </summary>
    [Test]
    public async Task ArrowKeys_AreBoundToTheTransport()
    {
        (Key Key, Playback2DAction Action)[] expected =
        [
            (Key.Left, Playback2DAction.StepBack),
            (Key.Right, Playback2DAction.StepForward),
            (Key.Up, Playback2DAction.SpeedUp),
            (Key.Down, Playback2DAction.SpeedDown)
        ];

        foreach ((Key key, Playback2DAction action) in expected)
        {
            await Assert.That(Playback2DKeymap.TryResolve(key, KeyModifiers.None, false,
                out Playback2DAction resolved)).IsTrue();
            await Assert.That(resolved).IsEqualTo(action);
        }
    }

    /// <summary>
    ///     <b>The display formatter dropped <c>Meta</c> while the persist formatter wrote it.</b> A
    ///     macOS user who captured ⌘+K got <c>ToolDraw=Meta+K</c> in the file, correctly, and
    ///     read back a bare <c>"K"</c> in every Settings row, reset chip, tooltip and refusal message,
    ///     indistinguishable from an unmodified K, and from a DIFFERENT action bound to plain K.
    ///     <para>
    ///         The two formatters are asserted against each other rather than against a literal: they are
    ///         one contract in two spellings (one for eyes, one for <c>KeyGesture.Parse</c>), and the bug
    ///         was that only one of them knew about a modifier.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(KeyModifiers.Meta)]
    [Arguments(KeyModifiers.Meta | KeyModifiers.Shift)]
    [Arguments(KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt)]
    public async Task Format_CarriesEveryModifier_Row_Persists(KeyModifiers modifiers)
    {
        string display = Playback2DKeymap.Format(Key.K, modifiers);
        string row = Playback2DKeymapProfile.Row(Playback2DAction.ToolDraw, Key.K, modifiers);

        Console.WriteLine($"[keybind-format] {modifiers} → display='{display}' row='{row}'");

        await Assert.That(display).Contains("Meta")
            .Because("a gesture the user can capture and the file can hold must be readable back");
        await Assert.That(display).IsNotEqualTo("K")
            .Because("that is the whole defect: ⌘+K rendered exactly as an unmodified K");

        // Every token the persisted row carries appears in the display text, so the two can only drift by
        // one of them growing a modifier the other does not know.
        foreach (string token in row["ToolDraw=".Length..].Split('+'))
        {
            await Assert.That(display).Contains(token);
        }
    }

    /// <summary>
    ///     The same contract closed over EVERY modifier combination rather than three samples. Both
    ///     spellings now come from one formatter asked for a different key half, and this is what pins
    ///     them there: the modifier chain must be character-identical, and only the key may differ: the
    ///     arrow glyph is the half that would not survive <c>KeyGesture.Parse</c>.
    /// </summary>
    [Test]
    public async Task DisplayAndPersistedSpellings_ShareOneModifierChain()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            KeyModifiers modifiers =
                ((mask & 1) != 0 ? KeyModifiers.Control : KeyModifiers.None)
                | ((mask & 2) != 0 ? KeyModifiers.Shift : KeyModifiers.None)
                | ((mask & 4) != 0 ? KeyModifiers.Alt : KeyModifiers.None)
                | ((mask & 8) != 0 ? KeyModifiers.Meta : KeyModifiers.None);

            string display = Playback2DKeymap.Format(Key.Left, modifiers);
            string gesture = Playback2DKeymapProfile
                .Row(Playback2DAction.StepBack, Key.Left, modifiers)["StepBack=".Length..];

            Console.WriteLine($"[keybind-format] {modifiers} → display='{display}' row='{gesture}'");

            await Assert.That(display).IsEqualTo(gesture[..^"Left".Length] + "←")
                .Because("one chain, two key spellings — a modifier either formatter knows alone is the "
                         + "Meta defect coming back");
        }
    }

    /// <summary>
    ///     <b>The browser eats some gestures before the page sees them.</b> Settings promises
    ///     that "keys already taken … are refused with a reason"; on the WASM head a user could bind
    ///     <c>Ctrl+T</c> or <c>F12</c>, watch it persist, and never see it fire once.
    /// </summary>
    [Test]
    [Arguments("ToolDraw=Ctrl+T")]
    [Arguments("ToolDraw=Ctrl+N")]
    [Arguments("ToolDraw=F12")]
    [Arguments("ToolDraw=Ctrl+Shift+I")]
    public async Task BrowserReservedGestures_AreRefusedOnTheBrowserHead_AndAcceptedOnDesktop(string row)
    {
        string onBrowser = Playback2DKeymapProfile.ValidateOverride([], row, true);
        string onDesktop = Playback2DKeymapProfile.ValidateOverride([], row, false);

        Console.WriteLine($"[keybind-browser] '{row}' browser='{onBrowser}' desktop='{onDesktop}'");

        await Assert.That(onBrowser).IsNotEmpty()
            .Because("the browser handles it in its own chrome and never dispatches it to the document");
        await Assert.That(onBrowser).Contains("browser")
            .Because("'is an app-wide shortcut' would send the user hunting for a DemoViewer conflict "
                     + "they could resolve; this one they cannot");

        // Ctrl+W is BOTH a shell accelerator and a close-tab, so it is refused on either head. Every other
        // row here is a perfectly good desktop binding, and refusing it there would be the mirror defect.
        await Assert.That(onDesktop).IsEmpty()
            .Because("none of these is taken by anything on a desktop head");
    }

    /// <summary>
    ///     The browser list must not overreach. <c>Ctrl+Z</c>, <c>Ctrl+X</c> and the arrows are editing
    ///     and navigation keys the browser DOES deliver to the page and the page CAN cancel: reserving
    ///     them would refuse a rebind that works perfectly, which is this defect's mirror image.
    /// </summary>
    [Test]
    public async Task TheBrowserReservedSet_DoesNotClaimKeysThePageActuallyReceives()
    {
        (Key Key, KeyModifiers Modifiers)[] mustStayBindable =
        [
            (Key.Z, KeyModifiers.Control), (Key.X, KeyModifiers.Control), (Key.Space, KeyModifiers.None),
            (Key.Escape, KeyModifiers.None), (Key.Left, KeyModifiers.None), (Key.D, KeyModifiers.None)
        ];

        List<string> overreach = [];
        foreach ((Key key, KeyModifiers modifiers) in mustStayBindable)
        {
            if (Playback2DKeymap.IsBrowserReserved(key, modifiers, true))
            {
                overreach.Add(Playback2DKeymap.Format(key, modifiers));
            }
        }

        Console.WriteLine($"[keybind-browser] reserved set = {Playback2DKeymap.BrowserReservedGestures.Count} "
                          + "gestures");

        await Assert.That(string.Join(", ", overreach)).IsEqualTo("");
        await Assert.That(Playback2DKeymap.BrowserReservedGestures).IsNotEmpty()
            .Because("an empty set would make every assertion above pass while protecting nothing");

        // And the shipped table itself must not sit on one, or the WASM head ships dead default keys.
        List<string> shipped = [];
        foreach (Playback2DBinding binding in Playback2DKeymap.Default)
        {
            if (Playback2DKeymap.IsBrowserReserved(binding.Key, binding.Modifiers, true))
            {
                shipped.Add($"{binding.Action}={Playback2DKeymap.Format(binding.Key, binding.Modifiers)}");
            }
        }

        await Assert.That(string.Join(", ", shipped)).IsEqualTo("")
            .Because("a shipped default the browser eats is a key that has never worked there");
    }

    private static Playback2DBinding Single(Playback2DAction action) =>
        Playback2DKeymap.Default.Single(b => b.Action == action);

    private static List<(Key Key, KeyModifiers Modifiers)> ParseShellGestures()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        string path = root is null
            ? ""
            : Path.Combine(root, "src", "App", "DemoViewer.NET", "Views", "MainView.axaml");

        if (root is null || !File.Exists(path))
        {
            throw new SkipTestException($"MainView.axaml not locatable from the build output (looked at '{path}').");
        }

        string xaml = File.ReadAllText(path);
        int start = xaml.IndexOf("<UserControl.KeyBindings>", StringComparison.Ordinal);
        int end = xaml.IndexOf("</UserControl.KeyBindings>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new SkipTestException("MainView.axaml has no UserControl.KeyBindings block to audit.");
        }

        List<(Key, KeyModifiers)> gestures = [];
        foreach (Match match in Regex.Matches(xaml[start..end], "Gesture=\"([^\"]+)\""))
        {
            KeyGesture gesture = KeyGesture.Parse(match.Groups[1].Value);
            gestures.Add((gesture.Key, gesture.KeyModifiers));
        }

        if (gestures.Count == 0)
        {
            throw new SkipTestException("Parsed zero shell gestures — refusing to pass vacuously.");
        }

        return gestures;
    }
}
