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
    public async Task TryResolve_ReservedGesture_ReturnsFalse()
    {
        // Home is still DECLARED-not-routed (the conflict checker guards it; B3 binds it). A reserved
        // binding resolves to nothing, so the view leaves the key unhandled rather than pretending.
        await Assert.That(Playback2DKeymap.TryResolve(Key.Home, KeyModifiers.None, false,
            out Playback2DAction fit)).IsFalse();
        await Assert.That(fit).IsEqualTo(Playback2DAction.None);
    }

    /// <summary>
    ///     A1 declared the annotation gestures reserved; B2 binds them. This is the moment the reservation
    ///     paid off — the gestures were guarded from the day the table shipped, so binding them here is a
    ///     flag flip rather than a collision hunt.
    /// </summary>
    [Test]
    public async Task TryResolve_AnnotationGestures_AreBoundByB2()
    {
        await Assert.That(Resolve(Key.X, KeyModifiers.None)).IsEqualTo(Playback2DAction.ToolErase);
        await Assert.That(Resolve(Key.D, KeyModifiers.None)).IsEqualTo(Playback2DAction.ToolDraw);
        await Assert.That(Resolve(Key.Z, KeyModifiers.Control)).IsEqualTo(Playback2DAction.Undo);
        await Assert.That(Resolve(Key.Z, KeyModifiers.Control | KeyModifiers.Shift))
            .IsEqualTo(Playback2DAction.Redo);
        await Assert.That(Resolve(Key.X, KeyModifiers.Control))
            .IsEqualTo(Playback2DAction.ClearAnnotations);
    }

    [Test]
    public async Task TryResolve_ToolActive_PrefersToolScopedBinding()
    {
        // D7: Space is play/pause normally, but the tool-scoped HoldPan SHADOWS it while a drawing tool
        // is active. B2 bound HoldPan, so the shadow now resolves to the action rather than to nothing.
        await Assert.That(Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, false,
            out Playback2DAction idle)).IsTrue();
        await Assert.That(idle).IsEqualTo(Playback2DAction.TogglePlay);

        await Assert.That(Playback2DKeymap.TryResolve(Key.Space, KeyModifiers.None, true,
            out Playback2DAction drawing)).IsTrue();
        await Assert.That(drawing).IsEqualTo(Playback2DAction.HoldPan);

        // D8: same for Esc — clear-follow normally, gesture bail while a drawing tool is active.
        await Assert.That(Resolve(Key.Escape, KeyModifiers.None)).IsEqualTo(Playback2DAction.ClearFollow);
        await Assert.That(Playback2DKeymap.TryResolve(Key.Escape, KeyModifiers.None, true,
            out Playback2DAction bail)).IsTrue();
        await Assert.That(bail).IsEqualTo(Playback2DAction.CancelGesture);
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
