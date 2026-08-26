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
///         B5-5's keybind audit lands here too (the plan calls the class
///         <c>Playback2DKeymapConflictTests</c>; A1 had already shipped this one under a different name and
///         a near-duplicate would have been two files answering the same question). The audit's own
///         resolutions — <c>X</c> is erase, <c>E</c> is round nav, no duplicate gesture within a scope —
///         are pinned below. Text-input suppression is A1's single global rule and is asserted where the
///         rule lives, in <c>Playback2DKeyRoutingTests.TextBoxFocused_KeysAreNotIntercepted</c>.
///     </para>
/// </summary>
public class Playback2DKeybindConflictTests
{
    [Test]
    public async Task Keymap_DoesNotCollideWithShellAccelerators()
    {
        List<(Key Key, KeyModifiers Modifiers)> shell = ParseShellGestures();

        IReadOnlyList<string> conflicts = Playback2DKeymap.FindConflicts(Playback2DKeymap.Default, shell);

        Console.WriteLine($"[keybind-audit] shell accelerators parsed from MainView.axaml: {shell.Count}");
        await Assert.That(conflicts).IsEmpty();
    }

    [Test]
    public async Task ShellReservedGestures_MatchesMainViewAxaml()
    {
        // The keymap's own copy of the shell list is what the static ctor checks against; if the two
        // drift, the ctor's guarantee quietly weakens.
        HashSet<(Key, KeyModifiers)> fromSource = [.. ParseShellGestures()];
        HashSet<(Key, KeyModifiers)> declared = [.. Playback2DKeymap.ShellReservedGestures];

        await Assert.That(declared.SetEquals(fromSource)).IsTrue();
    }

    /// <summary>
    ///     No gesture is bound twice WITHIN a scope. This is the second, independent net behind the static
    ///     constructor's own <c>FindConflicts</c> throw — a table whose conflict check is only ever run by
    ///     the code that owns it has nobody watching the watchman.
    /// </summary>
    [Test]
    public async Task NoDuplicateGesture_WithinTheKeymap()
    {
        HashSet<(Playback2DBindingScope, Key, KeyModifiers)> seen = [];
        List<string> duplicates = [];

        foreach (Playback2DBinding binding in Playback2DKeymap.Default)
        {
            if (!seen.Add((binding.Scope, binding.Key, binding.Modifiers)))
            {
                duplicates.Add($"{binding.Modifiers}+{binding.Key} ({binding.Scope}) → {binding.Action}");
            }
        }

        await Assert.That(string.Join("; ", duplicates)).IsEqualTo("");
    }

    /// <summary>
    ///     B5 D1, pinned. Design §7.5 assigned <c>E</c> to BOTH "Q/E round nav" and "erase"; the keybind
    ///     audit is the phase that had to resolve it. Rounds keep <c>Q</c>/<c>E</c> (parity with the rest
    ///     of the market), erase moved to <c>X</c>, which pairs coherently with <c>Ctrl+X</c> = clear all.
    ///     A later edit re-introducing the clash fails here rather than shadowing round navigation the
    ///     moment somebody picks up the pen.
    /// </summary>
    [Test]
    public async Task EraseIsX_NotE()
    {
        Playback2DBinding erase = Single(Playback2DAction.ToolErase);
        await Assert.That(erase.Key).IsEqualTo(Key.X);
        await Assert.That(erase.Modifiers).IsEqualTo(KeyModifiers.None);

        Playback2DBinding nextRound = Single(Playback2DAction.NextRound);
        await Assert.That(nextRound.Key).IsEqualTo(Key.E);
        await Assert.That(nextRound.Modifiers).IsEqualTo(KeyModifiers.None);

        Playback2DBinding clearAll = Single(Playback2DAction.ClearAnnotations);
        await Assert.That(clearAll.Key).IsEqualTo(Key.X);
        await Assert.That(clearAll.Modifiers).IsEqualTo(KeyModifiers.Control);
    }

    /// <summary>
    ///     B5-5's <c>Space</c> resolution: play/pause normally, hold-to-pan while a drawing tool is
    ///     active — and while a tool is active a tap must NOT also toggle playback, or every pan starts by
    ///     un-pausing the demo under the user's pen.
    /// </summary>
    [Test]
    public async Task Space_IsPlayPause_UnlessADrawingToolIsActive()
    {
        await Assert.That(Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, false,
            out Playback2DAction idle)).IsTrue();
        await Assert.That(idle).IsEqualTo(Playback2DAction.TogglePlay);

        await Assert.That(Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, true,
            out Playback2DAction drawing)).IsTrue();
        await Assert.That(drawing).IsEqualTo(Playback2DAction.HoldPan);
    }

    /// <summary>
    ///     The transport owns the arrow keys and the speed ladder, at <c>Always</c> scope. Risk 2: the
    ///     player-card <c>ItemsControl</c> became selectable in A1, and an arrow that reached the list
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
    ///     <b>D6 finding 20 — the display formatter dropped <c>Meta</c> while the persist formatter wrote
    ///     it.</b> A macOS user who captured ⌘+K got <c>ToolDraw=Meta+K</c> in the file, correctly, and
    ///     read back a bare <c>"K"</c> in every Settings row, reset chip, tooltip and refusal message —
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
    ///     <b>D6 §4b — the browser eats some gestures before the page sees them.</b> Settings promises
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
        string onBrowser = Playback2DKeymapProfile.ValidateOverride([], row, isBrowser: true);
        string onDesktop = Playback2DKeymapProfile.ValidateOverride([], row, isBrowser: false);

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
    ///     and navigation keys the browser DOES deliver to the page and the page CAN cancel — reserving
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
