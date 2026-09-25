#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     Every action the 2D Playback tab's keymap can dispatch. The trailing block is DECLARED here but not
///     yet bound: declaring them now lets the conflict checker protect those gestures from the day the
///     table ships, instead of discovering the collision when the tool arrives.
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
    HoldPan,

    // Bound by Find Rounds Like This (Strat Room, plan D7):
    FindRoundsLikeThis,

    // Bound by Result Cards And Walking (Strat Room): J / K over the Situations result set.
    NextSituationResult,
    PrevSituationResult,

    // Bound by Tag Palette (Strat Room). The first is how the keyboard reaches the palette; the rest
    // are palette-scoped, so they only act while it has focus.
    FocusTagPalette,
    TagPaletteBack,
    TagNote,
    TagClearSticky,

    // Bound by Label Mode (Strat Room): palette-scoped like the three above.
    TagLabelMode,
    TagLabelGroupNext,

    // Bound by Suggested Tags Review (Strat Room): the Proposal Queue's walk and verdicts.
    SuggestionNext,
    SuggestionPrev,
    SuggestionAccept,
    SuggestionReject,
    SuggestionEdit,
    SuggestionAcceptAll,

    // Bound by Shape Tools (Strat Room, step-authoring.md §3.7): the annotation toolbar's shape and
    // text tools.
    ToolLine,
    ToolArrow,
    ToolRect,
    ToolEllipse,
    ToolText,

    // Bound by Step Authoring (Strat Room, step-authoring.md §3.7): the strat canvas's token tool and
    // step keys. Rows in this shared table so one set of overrides and one Settings list cover both
    // tabs; the 2D Playback tab leaves every one of them unhandled.
    ToolToken,
    AddStep,
    DuplicateStep,
    DeleteStep,
    PrevStep,
    NextStep
}

/// <summary>When a binding applies. Tool-scoped bindings take precedence while a drawing tool is active.</summary>
public enum Playback2DBindingScope
{
    /// <summary>Applies whenever the 2D surface has focus.</summary>
    Always,

    /// <summary>Applies only while a pointer TOOL (draw / erase) is active, and then shadows <see cref="Always" />.</summary>
    WhenToolActive,

    /// <summary>
    ///     Applies only while the Tag Palette has focus, and then shadows both scopes above (overview
    ///     correction 21). The palette's own button hotkeys are routed at this scope too, after its rows.
    /// </summary>
    WhenPaletteFocused,

    /// <summary>
    ///     Applies only while a proposal is selected in the Suggested Tags queue, and then shadows
    ///     <see cref="Always" /> and <see cref="WhenToolActive" />; the palette scope still wins. It exists for
    ///     J / K, which walk the Situations result set otherwise: both walks keep the keys the plan gave
    ///     them, and the one on screen is the one they drive.
    /// </summary>
    WhenSuggestionSelected
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

    // The ONE gesture formatter, in two spellings of the key: display text for human eyes, and the
    // parseable form Playback2DKeymapProfile.Row persists (the arrow glyphs and "Esc" below would not
    // survive KeyGesture.Parse). The modifier chain MUST stay shared: a second copy that drops Meta
    // reads a macOS user's captured ⌘+K back as a bare "K" in every Settings row, reset chip, tooltip
    // and refusal, indistinguishable from a DIFFERENT action bound to bare K.
    internal static string Format(Key key, KeyModifiers modifiers, bool display = true)
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

        // ── Situation Search (Always). Ctrl+F, not F: bare F is follow cycling, and the owner chose the
        //    common find chord (plan D7). It is not a shell accelerator, and the browser DOES deliver
        //    Ctrl+F to the page (the find bar opens only when nothing handles it), so it is not in the
        //    browser-reserved list either.
        new(Playback2DAction.FindRoundsLikeThis, Key.F, KeyModifiers.Control, Playback2DBindingScope.Always,
            "Find rounds like this: snapshot the alive players onto the Situations query", false),

        // ── Result walking (Always). J / K step the Situations result set from inside playback, so a
        //    search is reviewed without a tab switch per hit. Unhandled with no result set, and the
        //    ends of the set do not wrap.
        new(Playback2DAction.NextSituationResult, Key.J, KeyModifiers.None, Playback2DBindingScope.Always,
            "Next situation result: seek to the next card of the Situations search", false),
        new(Playback2DAction.PrevSituationResult, Key.K, KeyModifiers.None, Playback2DBindingScope.Always,
            "Previous situation result: seek to the previous card of the Situations search", false),

        // ── Tag Palette. C (for code) reaches the palette from anywhere on the surface; the three below
        //    act only while the palette has focus, which is how Esc steps back out of a panel instead of
        //    clearing the follow. The note and the sticky reset are chords so a palette author keeps
        //    every bare letter and digit for buttons. None of the four touches Ctrl+F or the Suggested
        //    Tags queue's J/K/Y/N/Enter/Ctrl+Y (suggested-tags.md §3.6), and C is none of Step
        //    Authoring's V/A/T/L/R/O.
        new(Playback2DAction.FocusTagPalette, Key.C, KeyModifiers.None, Playback2DBindingScope.Always,
            "Tag palette: focus it so its hotkeys tag (press again to leave)", false),
        new(Playback2DAction.TagPaletteBack, Key.Escape, KeyModifiers.None,
            Playback2DBindingScope.WhenPaletteFocused,
            "Tag palette: finish the tag and go back to the codes, or leave the palette", false),
        new(Playback2DAction.TagNote, Key.M, KeyModifiers.Control, Playback2DBindingScope.WhenPaletteFocused,
            "Tag palette: write a note on the tag just made", false),
        new(Playback2DAction.TagClearSticky, Key.Back, KeyModifiers.Control,
            Playback2DBindingScope.WhenPaletteFocused, "Tag palette: clear the sticky labels", false),

        // ── Label Mode. Chords for the palette's reason: a palette author keeps every bare key. Ctrl+L
        //    and Ctrl+G are neither shell accelerators nor gestures the browser keeps from the page.
        new(Playback2DAction.TagLabelMode, Key.L, KeyModifiers.Control, Playback2DBindingScope.WhenPaletteFocused,
            "Tag palette: label mode, adding labels to the tag under the playhead instead of making tags", false),
        new(Playback2DAction.TagLabelGroupNext, Key.G, KeyModifiers.Control,
            Playback2DBindingScope.WhenPaletteFocused, "Tag palette: in label mode, show the next label group",
            false),

        // ── Suggested Tags queue (suggested-tags.md §3.6). Y, N, Enter and Ctrl+Y are Always rows so the
        //    checker keeps them for the queue, and they do nothing until a proposal is selected. J and K
        //    are the Situations walk's Always rows, so the queue's walk sits in its own scope that only a
        //    selection turns on; next is J there too, the direction the result walk shipped with.
        new(Playback2DAction.SuggestionNext, Key.J, KeyModifiers.None,
            Playback2DBindingScope.WhenSuggestionSelected,
            "Suggested tags: select the next pending proposal and seek to it", false),
        new(Playback2DAction.SuggestionPrev, Key.K, KeyModifiers.None,
            Playback2DBindingScope.WhenSuggestionSelected,
            "Suggested tags: select the previous pending proposal and seek to it", false),
        new(Playback2DAction.SuggestionAccept, Key.Y, KeyModifiers.None, Playback2DBindingScope.Always,
            "Suggested tags: accept the selected proposal as a tag and go to the next", false),
        new(Playback2DAction.SuggestionReject, Key.N, KeyModifiers.None, Playback2DBindingScope.Always,
            "Suggested tags: reject the selected proposal and go to the next", false),
        new(Playback2DAction.SuggestionEdit, Key.Enter, KeyModifiers.None, Playback2DBindingScope.Always,
            "Suggested tags: edit the selected proposal before accepting it", false),
        new(Playback2DAction.SuggestionAcceptAll, Key.Y, KeyModifiers.Control, Playback2DBindingScope.Always,
            "Suggested tags: accept every pending proposal the queue's filter shows, after a confirm", false),

        // ── Shape Tools (step-authoring.md §3.7). Bare letters in the Always scope like D and X, each
        //    pressed again to go back to pan. None collides with the shipped rows, the shell list or the
        //    browser list, and the Tag Palette's own letters are palette-scoped, so a palette's "A" is a
        //    site while it has focus and the Arrow tool otherwise (overview correction 21).
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

        // ── Step Authoring (step-authoring.md §3.7), acted on by the Strat Book canvas only. AddStep is
        //    Shift+N, not N: Suggested Tags holds bare N for reject (overview correction 21). Ctrl+D is
        //    Chrome's bookmark key but reaches the page and is cancellable, so it is deliberately not in
        //    the browser list; [ and ] are free in every list.
        new(Playback2DAction.ToolToken, Key.V, KeyModifiers.None, Playback2DBindingScope.Always,
            "Strat canvas: token tool (press again for pan)", false),
        new(Playback2DAction.AddStep, Key.N, KeyModifiers.Shift, Playback2DBindingScope.Always,
            "Strat canvas: insert a step after the active one at the playhead's round-clock time", false),
        new(Playback2DAction.DuplicateStep, Key.D, KeyModifiers.Control, Playback2DBindingScope.Always,
            "Strat canvas: copy the active step's positions and strokes into a new step 5 s later", false),
        new(Playback2DAction.DeleteStep, Key.Delete, KeyModifiers.Control, Playback2DBindingScope.Always,
            "Strat canvas: delete the active step", false),
        new(Playback2DAction.PrevStep, Key.OemOpenBrackets, KeyModifiers.None, Playback2DBindingScope.Always,
            "Strat canvas: seek to the previous step and make it active", false),
        new(Playback2DAction.NextStep, Key.OemCloseBrackets, KeyModifiers.None, Playback2DBindingScope.Always,
            "Strat canvas: seek to the next step and make it active", false),

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
