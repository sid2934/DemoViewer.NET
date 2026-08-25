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
