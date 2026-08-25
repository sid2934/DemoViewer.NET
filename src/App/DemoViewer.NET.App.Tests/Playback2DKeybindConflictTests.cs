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
