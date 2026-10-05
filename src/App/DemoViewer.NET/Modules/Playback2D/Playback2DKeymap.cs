#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The core actions of the 2D Playback tab's keymap, a closed vocabulary: an extension's actions are
///     string ids (<see cref="Playback2DBinding.ActionId" />) and never join this enum. A member's name is its
///     id and a persisted override key. <see cref="FitCamera" /> is declared but not yet bound, so the
///     conflict checker protects its gesture from the day the table ships.
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

    // Annotations:
    ToolDraw,
    ToolErase,
    CancelGesture,
    Undo,
    Redo,
    ClearAnnotations,
    HoldPan,

    // Shape tools:
    ToolLine,
    ToolArrow,
    ToolRect,
    ToolEllipse,
    ToolText
}

/// <summary>
///     When a binding applies, by name. The tab resolves the two core scopes, <see cref="Always" /> and
///     <see cref="WhenToolActive" />, itself. An extension declares its own focus scopes and resolves them in
///     a key handler before the tab's keymap, which is what lets a focused panel's rows shadow both.
/// </summary>
/// <param name="Name">The scope's name, spelled as <c>CommandDescriptor.Scope</c> spells it.</param>
public readonly record struct Playback2DBindingScope(string Name)
{
    /// <summary>Applies whenever the 2D surface has focus.</summary>
    public static Playback2DBindingScope Always { get; } = new("playback2d");

    /// <summary>Applies only while a pointer TOOL (draw / erase) is active, and then shadows <see cref="Always" />.</summary>
    public static Playback2DBindingScope WhenToolActive { get; } = new("playback2d.tool");

    /// <summary>Whether this is one of the two scopes the tab resolves itself.</summary>
    public bool IsCore => this == Always || this == WhenToolActive;

    /// <inheritdoc />
    public override string ToString() => Name ?? "";
}

/// <summary>
///     The core action vocabulary as string ids. A core id is the <see cref="Playback2DAction" /> member's
///     name; every other id belongs to an extension and never maps to the enum.
/// </summary>
public static class Playback2DActionIds
{
    private static readonly Dictionary<string, Playback2DAction> _byName =
        Enum.GetValues<Playback2DAction>().Where(a => a != Playback2DAction.None)
            .ToDictionary(a => a.ToString(), a => a, StringComparer.Ordinal);

    /// <summary>Every core id, <see cref="Playback2DAction.None" /> excluded.</summary>
    public static IReadOnlyCollection<string> Core => _byName.Keys;

    /// <summary>The id of a core action.</summary>
    /// <param name="action">The action.</param>
    public static string Of(Playback2DAction action) => action.ToString();

    /// <summary>
    ///     Maps a core id back to its enum member. Exact and case-sensitive: an extension id, a number or a
    ///     differently cased name is not a core action.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="action">The core action, or <see cref="Playback2DAction.None" />.</param>
    public static bool TryCore(string? id, out Playback2DAction action)
    {
        if (id is not null && _byName.TryGetValue(id, out action))
        {
            return true;
        }

        action = Playback2DAction.None;
        return false;
    }
}

/// <summary>One row of the declarative keymap.</summary>
/// <param name="ActionId">The action's id: a core action's enum name, or an extension command's id.</param>
/// <param name="Key">The key.</param>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Scope">When the row applies.</param>
/// <param name="Description">Human description, shown in Settings.</param>
/// <param name="IsReserved">Declared but not routed.</param>
public readonly record struct Playback2DBinding(
    string ActionId,
    Key Key,
    KeyModifiers Modifiers,
    Playback2DBindingScope Scope,
    string Description,
    bool IsReserved)
{
    /// <summary>A core row.</summary>
    /// <param name="action">The core action.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="scope">When the row applies.</param>
    /// <param name="description">Human description.</param>
    /// <param name="isReserved">Declared but not routed.</param>
    public Playback2DBinding(Playback2DAction action, Key key, KeyModifiers modifiers, Playback2DBindingScope scope,
        string description, bool isReserved)
        : this(Playback2DActionIds.Of(action), key, modifiers, scope, description, isReserved)
    {
    }

    /// <summary>The core action this row dispatches, or <see cref="Playback2DAction.None" /> for an extension row.</summary>
    public Playback2DAction CoreAction =>
        Playback2DActionIds.TryCore(ActionId, out Playback2DAction action) ? action : Playback2DAction.None;
}

/// <summary>
///     The 2D Playback tab's declarative action→gesture table, conflict-checked at registration: the static
///     constructor runs <see cref="FindConflicts" /> over the shipped table and THROWS on a non-empty
///     result, so a duplicate gesture or a collision with a shell accelerator fails at first touch rather
///     than silently shadowing a key at runtime.
///     <para>
///         Every action a binding dispatches routes through <c>PlaybackController</c> commands or
///         capability-gated <c>IModuleContext.Request*</c>, the exact surfaces LiveSync's
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
        BrowserReservedGestures = BuildBrowserReserved();

        // SHELL only. The shipped table is compiled once and runs on every head, so a browser gesture
        // has no business failing the desktop build's type initialiser, and none of the shipped
        // bindings uses one anyway. The browser set exists to refuse a USER'S rebind, a per-host
        // question that Playback2DKeymapProfile asks.
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
    ///     Gestures the BROWSER consumes before the page ever sees them. Empty of meaning on a desktop
    ///     head; on WASM these are the keys a rebind can be offered, accepted and persisted for, and then
    ///     never fire, because Chrome opened a tab instead.
    ///     <para>
    ///         Deliberately a SECOND list rather than more rows in <see cref="ShellReservedGestures" />:
    ///         that one is asserted character-for-character against <c>MainView.axaml</c>'s own
    ///         <c>KeyBindings</c> block by <c>Playback2DKeybindConflictTests</c>, so anything added to it
    ///         that the shell does not declare breaks the test that keeps the two lists in agreement.
    ///     </para>
    ///     <para>
    ///         <b>Conservative by construction.</b> Only gestures the browser takes at CHROME level and
    ///         never delivers to the document are here. <c>Ctrl+Z</c>, <c>Ctrl+X</c> and friends are
    ///         editing commands that DO reach the page and are cancellable, so reserving them would
    ///         refuse a rebind that works perfectly.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<(Key Key, KeyModifiers Modifiers)> BrowserReservedGestures { get; }

    /// <summary>
    ///     The gestures a rebind must not claim on <paramref name="isBrowser" />'s head: the shell
    ///     accelerators always, plus <see cref="BrowserReservedGestures" /> on the WASM one.
    /// </summary>
    /// <param name="isBrowser">Whether the host is the browser head.</param>
    public static IReadOnlyList<(Key Key, KeyModifiers Modifiers)> ReservedGestures(bool isBrowser) =>
        isBrowser ? [.. ShellReservedGestures, .. BrowserReservedGestures] : ShellReservedGestures;

    /// <summary>Whether <paramref name="isBrowser" />'s head hands this gesture to the browser chrome.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="isBrowser">Whether the host is the browser head.</param>
    public static bool IsBrowserReserved(Key key, KeyModifiers modifiers, bool isBrowser)
    {
        if (!isBrowser)
        {
            return false;
        }

        foreach ((Key reservedKey, KeyModifiers reservedModifiers) in BrowserReservedGestures)
        {
            if (reservedKey == key && reservedModifiers == modifiers)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Resolves a keypress to an action. Pure: the primary, Avalonia-event-free overload. A gesture that
    ///     resolves to a RESERVED binding returns false: the key is claimed but not yet implemented, so the
    ///     view leaves it unhandled rather than pretending to act.
    /// </summary>
    public static bool TryResolve(Key key, KeyModifiers modifiers, bool toolActive,
        out Playback2DAction action)
    {
        // Tool-scoped bindings SHADOW the always-scoped ones while a tool is active. That is how
        // hold-Space-to-pan and Esc-cancels-the-gesture take Space/Esc back without editing this table.
        if (toolActive && TryFind(Playback2DBindingScope.WhenToolActive, key, modifiers,
                out Playback2DBinding tool))
        {
            action = tool.IsReserved ? Playback2DAction.None : tool.CoreAction;
            return !tool.IsReserved;
        }

        if (TryFind(Playback2DBindingScope.Always, key, modifiers, out Playback2DBinding always)
            && !always.IsReserved)
        {
            action = always.CoreAction;
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
        Dictionary<(Playback2DBindingScope, Key, KeyModifiers), string> seen = new();
        HashSet<(Key, KeyModifiers)> shell = new(shellReserved);

        foreach (Playback2DBinding binding in bindings)
        {
            (Playback2DBindingScope, Key, KeyModifiers) key = (binding.Scope, binding.Key, binding.Modifiers);
            if (seen.TryGetValue(key, out string? other))
            {
                conflicts.Add(
                    $"{Format(binding.Key, binding.Modifiers)} ({ScopeText(binding.Scope)}) is bound to both "
                    + $"{other} and {binding.ActionId}");
            }
            else
            {
                seen[key] = binding.ActionId;
            }

            if (shell.Contains((binding.Key, binding.Modifiers)))
            {
                conflicts.Add(
                    $"{Format(binding.Key, binding.Modifiers)} ({binding.ActionId}) shadows a shell accelerator");
            }
        }

        return conflicts;
    }

    /// <summary>Display text for an action's gesture (e.g. "Shift+E"), "" when unbound. For tooltips.</summary>
    public static string GestureText(Playback2DAction action)
    {
        foreach (Playback2DBinding binding in Default)
        {
            if (binding.CoreAction == action)
            {
                return Format(binding.Key, binding.Modifiers);
            }
        }

        return "";
    }

    // The two core scopes keep the names conflict text has always used.
    private static string ScopeText(Playback2DBindingScope scope) =>
        scope == Playback2DBindingScope.Always ? "Always"
        : scope == Playback2DBindingScope.WhenToolActive ? "WhenToolActive"
        : scope.Name;

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

    // The ONE gesture formatter, in two spellings of the key: display text for human eyes, and the
    // parseable form Playback2DKeymapProfile.Row persists (the arrow glyphs and "Esc" below would not
    // survive KeyGesture.Parse). The modifier chain MUST stay shared: a second copy that drops Meta
    // reads a macOS user's captured ⌘+K back as a bare "K" in every Settings row, reset chip, tooltip
    // and refusal, indistinguishable from a DIFFERENT action bound to bare K.
    public static string Format(Key key, KeyModifiers modifiers, bool display = true)
    {
        List<string> parts = new(5);
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

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(display ? KeyName(key) : key.ToString());
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
        Key.Back => "Backspace",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.Enter => "Enter", // the same value as Key.Return, which is what ToString names it
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
        //    bare X (reserved below), so the erase tool does not collide with E.
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

        // ── Annotations ──
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

        // Extension commands join through CommandRegistry, never here. This table stays core-only: its
        // static constructor conflict-checks eagerly, which an extension's commands must never trip.

        // ── Shape Tools. Bare letters in the Always scope like D and X, each
        //    pressed again to go back to pan. None collides with the shipped rows, the shell list or the
        //    browser list, and the Tag Palette's own letters are palette-scoped, so a palette's "A" is a
        //    site while it has focus and the Arrow tool otherwise.
        new(Playback2DAction.ToolArrow, Key.A, KeyModifiers.None, Playback2DBindingScope.Always,
            "Arrow tool (press again for pan)", false),
        new(Playback2DAction.ToolText, Key.T, KeyModifiers.None, Playback2DBindingScope.Always,
            "Text tool (press again for pan)", false),
        new(Playback2DAction.ToolLine, Key.L, KeyModifiers.None, Playback2DBindingScope.Always,
            "Line tool (press again for pan)", false),
        new(Playback2DAction.ToolRect, Key.R, KeyModifiers.None, Playback2DBindingScope.Always,
            "Rectangle tool (press again for pan)", false),
        new(Playback2DAction.ToolEllipse, Key.O, KeyModifiers.None, Playback2DBindingScope.Always,
            "Ellipse tool (press again for pan)", false),

        // ── Reserved: declared so the conflict checker guards them. ──
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

    // The gestures Chrome and Firefox handle in the CHROME and never dispatch to the document, so
    // preventDefault cannot reach them and neither can Avalonia's WASM key pipeline. A user can bind one
    // in Settings today, watch it persist, and never see it fire, while the Settings copy promises that
    // "keys already taken … are refused with a reason".
    //
    // Ctrl+W is already a shell accelerator, so it is listed there too; the profile checks the union of
    // the two sets, and a gesture in both is refused once.
    private static (Key Key, KeyModifiers Modifiers)[] BuildBrowserReserved() =>
    [
        (Key.T, KeyModifiers.Control), // new tab
        (Key.T, KeyModifiers.Control | KeyModifiers.Shift), // reopen closed tab
        (Key.N, KeyModifiers.Control), // new window
        (Key.N, KeyModifiers.Control | KeyModifiers.Shift), // new private window
        (Key.W, KeyModifiers.Control), // close tab
        (Key.W, KeyModifiers.Control | KeyModifiers.Shift), // close window
        (Key.Q, KeyModifiers.Control | KeyModifiers.Shift), // quit (Chrome, Linux/Windows)
        (Key.F12, KeyModifiers.None), // dev tools
        (Key.I, KeyModifiers.Control | KeyModifiers.Shift), // dev tools
        (Key.J, KeyModifiers.Control | KeyModifiers.Shift), // dev tools console
        (Key.F11, KeyModifiers.None) // browser fullscreen
    ];
}
