#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     Every action the 2D Playback tab's keymap can dispatch. The trailing block is DECLARED here but bound
///     by a later phase — declaring them now is what lets the conflict checker protect those gestures from
///     the day the table ships, instead of discovering the collision when the tool arrives.
/// </summary>
public enum Playback2DAction
{
    None,
    TogglePlay,
    StepBack,
    StepForward,
    SpeedUp,
    SpeedDown,
    PrevRound,
    NextRound,
    PrevKill,
    NextKill,
    CycleFollowNext,
    CycleFollowPrev,
    ClearFollow,
    FitCamera,

    // Declared in A1, bound by B2 (annotations):
    ToolDraw,
    ToolErase,
    CancelGesture,
    Undo,
    Redo,
    ClearAnnotations,
    HoldPan
}

/// <summary>When a binding applies. Tool-scoped bindings take precedence while a drawing tool is active.</summary>
public enum Playback2DBindingScope
{
    /// <summary>Applies whenever the 2D surface has focus.</summary>
    Always,

    /// <summary>Applies only while a pointer TOOL (draw / erase) is active, and then shadows <see cref="Always" />.</summary>
    WhenToolActive
}

/// <summary>One row of the declarative keymap.</summary>
public readonly record struct Playback2DBinding(
    Playback2DAction Action,
    Key Key,
    KeyModifiers Modifiers,
    Playback2DBindingScope Scope,
    string Description,
    bool IsReserved);

/// <summary>
///     The 2D Playback tab's declarative action→gesture table, conflict-checked at registration: the static
///     constructor runs <see cref="FindConflicts" /> over the shipped table and THROWS on a non-empty
///     result, so a duplicate gesture or a collision with a shell accelerator fails at first touch rather
///     than silently shadowing a key at runtime.
///     <para>
///         Every action a binding dispatches routes through <c>PlaybackController</c> commands or
///         capability-gated <c>IModuleContext.Request*</c> — the exact surfaces LiveSync's
///         <c>SyncStateObserver</c> observes. A parallel path would silently bypass it.
///     </para>
/// </summary>
public static class Playback2DKeymap
{
    static Playback2DKeymap()
    {
        Default = BuildDefault();
        Active = Default.Where(b => !b.IsReserved).ToArray();
        Reserved = Default.Where(b => b.IsReserved).ToArray();
        ShellReservedGestures = BuildShellReserved();

        IReadOnlyList<string> conflicts = FindConflicts(Default, ShellReservedGestures);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "Playback2DKeymap has conflicting bindings: " + string.Join("; ", conflicts));
        }
    }

    /// <summary>Every declared binding, bound and reserved. Conflict-checked in the static ctor.</summary>
    public static IReadOnlyList<Playback2DBinding> Default { get; }

    /// <summary>The subset actually routed in this build (<c>IsReserved == false</c>).</summary>
    public static IReadOnlyList<Playback2DBinding> Active { get; }

    /// <summary>Declared-but-unbound bindings future phases will claim.</summary>
    public static IReadOnlyList<Playback2DBinding> Reserved { get; }

    /// <summary>The shell accelerators from <c>MainView.axaml</c> the tab must never shadow.</summary>
    public static IReadOnlyList<(Key Key, KeyModifiers Modifiers)> ShellReservedGestures { get; }

    /// <summary>
    ///     Resolves a keypress to an action. Pure — the primary, Avalonia-event-free overload. A gesture that
    ///     resolves to a RESERVED binding returns false: the key is claimed but not yet implemented, so the
    ///     view leaves it unhandled rather than pretending to act.
    /// </summary>
    public static bool TryResolve(Key key, KeyModifiers modifiers, bool toolActive,
        out Playback2DAction action)
    {
        // Tool-scoped bindings SHADOW the always-scoped ones while a tool is active — that is how B2's
        // hold-Space-to-pan and Esc-cancels-the-gesture take Space/Esc back without editing this table.
        if (toolActive && TryFind(Playback2DBindingScope.WhenToolActive, key, modifiers,
                out Playback2DBinding tool))
        {
            action = tool.IsReserved ? Playback2DAction.None : tool.Action;
            return !tool.IsReserved;
        }

        if (TryFind(Playback2DBindingScope.Always, key, modifiers, out Playback2DBinding always)
            && !always.IsReserved)
        {
            action = always.Action;
            return true;
        }

        action = Playback2DAction.None;
        return false;
    }

    /// <summary>Convenience overload for the view's KeyDown handler.</summary>
    public static bool TryResolve(KeyEventArgs e, bool toolActive, out Playback2DAction action)
    {
        if (e is null)
        {
            action = Playback2DAction.None;
            return false;
        }

        return TryResolve(e.Key, e.KeyModifiers, toolActive, out action);
    }

    /// <summary>
    ///     Human-readable conflict list: duplicate gestures within a scope, and collisions with
    ///     <paramref name="shellReserved" />. Empty = clean. The static ctor throws on non-empty.
    /// </summary>
    public static IReadOnlyList<string> FindConflicts(
        IEnumerable<Playback2DBinding> bindings,
        IEnumerable<(Key Key, KeyModifiers Modifiers)> shellReserved)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(shellReserved);

        List<string> conflicts = new();
        Dictionary<(Playback2DBindingScope, Key, KeyModifiers), Playback2DAction> seen = new();
        HashSet<(Key, KeyModifiers)> shell = new(shellReserved);

        foreach (Playback2DBinding binding in bindings)
        {
            (Playback2DBindingScope, Key, KeyModifiers) key = (binding.Scope, binding.Key, binding.Modifiers);
            if (seen.TryGetValue(key, out Playback2DAction other))
            {
                conflicts.Add(
                    $"{Format(binding.Key, binding.Modifiers)} ({binding.Scope}) is bound to both "
                    + $"{other} and {binding.Action}");
            }
            else
            {
                seen[key] = binding.Action;
            }

            if (shell.Contains((binding.Key, binding.Modifiers)))
            {
                conflicts.Add(
                    $"{Format(binding.Key, binding.Modifiers)} ({binding.Action}) shadows a shell accelerator");
            }
        }

        return conflicts;
    }

    /// <summary>Display text for an action's gesture (e.g. "Shift+E"), "" when unbound. For tooltips.</summary>
    public static string GestureText(Playback2DAction action)
    {
        foreach (Playback2DBinding binding in Default)
        {
            if (binding.Action == action)
            {
                return Format(binding.Key, binding.Modifiers);
            }
        }

        return "";
    }

    private static bool TryFind(Playback2DBindingScope scope, Key key, KeyModifiers modifiers,
        out Playback2DBinding found)
    {
        foreach (Playback2DBinding binding in Default)
        {
            if (binding.Scope == scope && binding.Key == key && binding.Modifiers == modifiers)
            {
                found = binding;
                return true;
            }
        }

        found = default;
        return false;
    }

    // DISPLAY text, not persistence text: the arrow glyphs and "Esc" below are for human eyes and would
    // not survive KeyGesture.Parse. Playback2DKeymapProfile.Row writes the parseable form and calls this
    // for everything a user reads, so one formatter serves both the shipped table and a rebound one.
    internal static string Format(Key key, KeyModifiers modifiers)
    {
        List<string> parts = new(4);
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Escape => "Esc",
        Key.Space => "Space",
        Key.Home => "Home",
        _ => key.ToString()
    };

    private static Playback2DBinding[] BuildDefault() =>
    [
        // ── Transport (Always) ───────────────────────────────────────────────
        new(Playback2DAction.TogglePlay, Key.Space, KeyModifiers.None, Playback2DBindingScope.Always,
            "Play / pause", false),
        new(Playback2DAction.StepBack, Key.Left, KeyModifiers.None, Playback2DBindingScope.Always,
            "Step back one frame", false),
        new(Playback2DAction.StepForward, Key.Right, KeyModifiers.None, Playback2DBindingScope.Always,
            "Step forward one frame", false),
        new(Playback2DAction.SpeedUp, Key.Up, KeyModifiers.None, Playback2DBindingScope.Always,
            "Next playback speed", false),
        new(Playback2DAction.SpeedDown, Key.Down, KeyModifiers.None, Playback2DBindingScope.Always,
            "Previous playback speed", false),

        // ── Navigation (Always). Q/E are ROUND nav, per the CS:DM parity table; the erase tool takes
        //    bare X (reserved below), which is what resolves design §7.5's Q/E-vs-E collision.
        new(Playback2DAction.PrevRound, Key.Q, KeyModifiers.None, Playback2DBindingScope.Always,
            "Previous round", false),
        new(Playback2DAction.NextRound, Key.E, KeyModifiers.None, Playback2DBindingScope.Always,
            "Next round", false),
        new(Playback2DAction.PrevKill, Key.Q, KeyModifiers.Shift, Playback2DBindingScope.Always,
            "Previous kill", false),
        new(Playback2DAction.NextKill, Key.E, KeyModifiers.Shift, Playback2DBindingScope.Always,
            "Next kill", false),

        // ── Follow (Always) ──────────────────────────────────────────────────
        new(Playback2DAction.CycleFollowNext, Key.F, KeyModifiers.None, Playback2DBindingScope.Always,
            "Follow the next player", false),
        new(Playback2DAction.CycleFollowPrev, Key.F, KeyModifiers.Shift, Playback2DBindingScope.Always,
            "Follow the previous player", false),
        new(Playback2DAction.ClearFollow, Key.Escape, KeyModifiers.None, Playback2DBindingScope.Always,
            "Clear the follow target and re-fit the camera", false),

        // ── Annotations (declared by A1, BOUND by B2). ──
        new(Playback2DAction.ToolDraw, Key.D, KeyModifiers.None, Playback2DBindingScope.Always,
            "Draw tool (press again for pan)", false),
        new(Playback2DAction.ToolErase, Key.X, KeyModifiers.None, Playback2DBindingScope.Always,
            "Erase tool (press again for pan)", false),
        new(Playback2DAction.Undo, Key.Z, KeyModifiers.Control, Playback2DBindingScope.Always,
            "Undo the last annotation edit", false),
        new(Playback2DAction.Redo, Key.Z, KeyModifiers.Control | KeyModifiers.Shift,
            Playback2DBindingScope.Always, "Redo the last undone annotation edit", false),
        new(Playback2DAction.ClearAnnotations, Key.X, KeyModifiers.Control, Playback2DBindingScope.Always,
            "Clear every annotation", false),
        new(Playback2DAction.HoldPan, Key.Space, KeyModifiers.None, Playback2DBindingScope.WhenToolActive,
            "Hold to pan while a drawing tool is active", false),
        new(Playback2DAction.CancelGesture, Key.Escape, KeyModifiers.None,
            Playback2DBindingScope.WhenToolActive, "Cancel the in-progress gesture", false),

        // ── Reserved: declared so the conflict checker guards them; bound by B3. ──
        new(Playback2DAction.FitCamera, Key.Home, KeyModifiers.None, Playback2DBindingScope.Always,
            "Fit the camera to the map (reserved)", true)
    ];

    // Mirrors MainView.axaml's UserControl.KeyBindings block. Playback2DKeybindConflictTests asserts this
    // list against that file's own text, so a shell binding added later cannot silently steal a 2D key.
    private static (Key Key, KeyModifiers Modifiers)[] BuildShellReserved() =>
    [
        (Key.P, KeyModifiers.Control),
        (Key.O, KeyModifiers.Control),
        (Key.W, KeyModifiers.Control),
        (Key.OemComma, KeyModifiers.Control),
        (Key.B, KeyModifiers.Control),
        (Key.D1, KeyModifiers.Control),
        (Key.D2, KeyModifiers.Control),
        (Key.D3, KeyModifiers.Control),
        (Key.D4, KeyModifiers.Control),
        (Key.D5, KeyModifiers.Control),
        (Key.D6, KeyModifiers.Control),
        (Key.D7, KeyModifiers.Control),
        (Key.D8, KeyModifiers.Control),
        (Key.D9, KeyModifiers.Control)
    ];
}
