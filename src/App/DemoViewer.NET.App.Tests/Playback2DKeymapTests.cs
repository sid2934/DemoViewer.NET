#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The declarative keymap. The table is conflict-checked in its own static constructor, so the first
///     test here is really "the type could be loaded at all"; the rest pin the two design decisions that
///     were direct collisions in the spec (Q/E round nav vs E erase; Space play/pause vs hold-to-pan).
/// </summary>
public class Playback2DKeymapTests
{
    [Test]
    public async Task DefaultTable_HasNoInternalConflicts()
    {
        IReadOnlyList<string> conflicts =
            Playback2DKeymap.FindConflicts(Playback2DKeymap.Default, Playback2DKeymap.ShellReservedGestures);

        await Assert.That(conflicts).IsEmpty();
    }

    [Test]
    public async Task ActiveBindings_ExcludeReserved()
    {
        await Assert.That(Playback2DKeymap.Active.Any(b => b.IsReserved)).IsFalse();
        await Assert.That(Playback2DKeymap.Reserved.All(b => b.IsReserved)).IsTrue();
        await Assert.That(Playback2DKeymap.Active.Count + Playback2DKeymap.Reserved.Count)
            .IsEqualTo(Playback2DKeymap.Default.Count);
        await Assert.That(Playback2DKeymap.Reserved.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task TryResolve_SpaceIsTogglePlay()
    {
        bool ok = Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, false,
            out Playback2DAction action);

        await Assert.That(ok).IsTrue();
        await Assert.That(action).IsEqualTo(Playback2DAction.TogglePlay);
    }

    [Test]
    public async Task TryResolve_ShiftE_IsNextKill_NotNextRound()
    {
        // The D6/D9 regression: bare E is round nav, Shift+E is kill nav, and the erase tool takes X.
        await Assert.That(Resolve(Key.E, KeyModifiers.None)).IsEqualTo(Playback2DAction.NextRound);
        await Assert.That(Resolve(Key.E, KeyModifiers.Shift)).IsEqualTo(Playback2DAction.NextKill);
        await Assert.That(Resolve(Key.Q, KeyModifiers.None)).IsEqualTo(Playback2DAction.PrevRound);
        await Assert.That(Resolve(Key.Q, KeyModifiers.Shift)).IsEqualTo(Playback2DAction.PrevKill);
    }

    [Test]
    public async Task TryResolve_ReservedGesture_ReturnsFalseInA1()
    {
        // X is DECLARED (so the conflict checker guards it) but not routed until B2 ships the erase tool.
        await Assert.That(Playback2DKeymap.TryResolve(Key.X, KeyModifiers.None, false, out Playback2DAction erase))
            .IsFalse();
        await Assert.That(erase).IsEqualTo(Playback2DAction.None);

        await Assert.That(Playback2DKeymap.TryResolve(Key.D, KeyModifiers.None, false, out _)).IsFalse();
        await Assert.That(Playback2DKeymap.TryResolve(Key.Z, KeyModifiers.Control, false, out _)).IsFalse();
        await Assert.That(Playback2DKeymap.TryResolve(Key.Home, KeyModifiers.None, false, out _)).IsFalse();
    }

    [Test]
    public async Task TryResolve_ToolActive_PrefersToolScopedBinding()
    {
        // D7: Space is play/pause normally, but a tool-scoped HoldPan SHADOWS it while a tool is active.
        // HoldPan is reserved in A1, so the shadow shows up as "no longer TogglePlay" — which is exactly
        // the mechanism B2 needs, proved before B2 depends on it.
        await Assert.That(Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, false,
            out Playback2DAction idle)).IsTrue();
        await Assert.That(idle).IsEqualTo(Playback2DAction.TogglePlay);

        await Assert.That(Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, true,
            out Playback2DAction drawing)).IsFalse();
        await Assert.That(drawing).IsNotEqualTo(Playback2DAction.TogglePlay);

        // D8: same for Esc — clear-follow normally, gesture bail while a tool is active.
        await Assert.That(Resolve(Key.Escape, KeyModifiers.None)).IsEqualTo(Playback2DAction.ClearFollow);
        await Assert.That(Playback2DKeymap.TryResolve(Key.Escape, KeyModifiers.None, true, out _)).IsFalse();
    }

    [Test]
    public async Task TryResolve_UnboundKey_ReturnsFalse()
    {
        await Assert.That(Playback2DKeymap.TryResolve(Key.J, KeyModifiers.None, false, out Playback2DAction a))
            .IsFalse();
        await Assert.That(a).IsEqualTo(Playback2DAction.None);
    }

    [Test]
    public async Task GestureText_FormatsModifiers()
    {
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.NextKill)).IsEqualTo("Shift+E");
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.TogglePlay)).IsEqualTo("Space");
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.Redo)).IsEqualTo("Ctrl+Shift+Z");
        await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.None)).IsEqualTo("");
    }

    [Test]
    public async Task FindConflicts_DetectsADuplicateGestureAndAShellCollision()
    {
        // The checker itself must actually catch something, or the clean-table assertion above is vacuous.
        Playback2DBinding[] duplicate =
        [
            new(Playback2DAction.TogglePlay, Key.Space, KeyModifiers.None, Playback2DBindingScope.Always,
                "a", false),
            new(Playback2DAction.StepBack, Key.Space, KeyModifiers.None, Playback2DBindingScope.Always,
                "b", false)
        ];
        await Assert.That(Playback2DKeymap.FindConflicts(duplicate, Playback2DKeymap.ShellReservedGestures))
            .IsNotEmpty();

        Playback2DBinding[] shellClash =
        [
            new(Playback2DAction.TogglePlay, Key.P, KeyModifiers.Control, Playback2DBindingScope.Always,
                "a", false)
        ];
        await Assert.That(Playback2DKeymap.FindConflicts(shellClash, Playback2DKeymap.ShellReservedGestures))
            .IsNotEmpty();
    }

    private static Playback2DAction Resolve(Key key, KeyModifiers modifiers)
    {
        Playback2DKeymap.TryResolve(key, modifiers, false, out Playback2DAction action);
        return action;
    }
}
