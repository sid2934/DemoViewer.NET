#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The keymap a running 2D Playback tab actually routes through: the shipped
///     <see cref="Playback2DKeymap" /> table with the user's
///     <c>Playback2DSettings.KeybindOverrides</c> composed over it.
///     <para>
///         <b>Why this is a second type rather than a mutable static table.</b>
///         <see cref="Playback2DKeymap" />'s static constructor THROWS on a conflicting table. That is
///         right for a table compiled into the binary — a collision is a bug, and it should fail at first
///         touch. It is fatal for one assembled from a hand-editable JSON settings file: a single typo
///         would surface as a <c>TypeInitializationException</c> that takes the 2D tab down with no way
///         to fix it from inside the app, and <c>Playback2DTabViewModel</c> is built by a bare
///         <c>new()</c> with no DI, so there is nowhere useful to catch it. This type validates instead —
///         every row it cannot honour is DROPPED and REPORTED, and everything that survives still
///         resolves. The shipped table stays exactly as it is: the default, and the thing overrides are
///         composed over.
///     </para>
/// </summary>
public sealed class Playback2DKeymapProfile
{
    // Shipped-table position per action, so an override REPLACES a row in place and Bindings keeps the
    // authored order the Settings list and the docs table both read top-to-bottom. An action bound twice
    // by a future table edit lands in _multiBound instead: "which row did you mean" has no answer a
    // settings file can express, so those rows stay un-rebindable rather than silently picking one.
    private static readonly Dictionary<Playback2DAction, int> _indexByAction;
    private static readonly HashSet<Playback2DAction> _multiBound;
    private static readonly (Key Key, KeyModifiers Modifiers)[] _shell;
    private static readonly (Key Key, KeyModifiers Modifiers)[] _shellAndBrowser;

    private readonly Playback2DBinding[] _bindings;
    private readonly HashSet<Playback2DAction> _overridden;

    // Ordered, not field initializers: BuildIndex hands _multiBound back through an out parameter, and
    // Default is built from the same shipped table both of them read.
    static Playback2DKeymapProfile()
    {
        _indexByAction = BuildIndex(out _multiBound);
        _shell = [.. Playback2DKeymap.ReservedGestures(false)];
        _shellAndBrowser = [.. Playback2DKeymap.ReservedGestures(true)];
        Default = new Playback2DKeymapProfile([.. Playback2DKeymap.Default], [], []);
    }

    // Which gestures a rebind may not claim on THIS head. Both sets are materialised in the static ctor
    // rather than composed per call: FromOverrides runs the whole conflict sweep once per accepted row on
    // the fallback path, and re-concatenating two arrays inside that loop is churn for nothing.
    private static (Key Key, KeyModifiers Modifiers)[] Reserved(bool isBrowser) =>
        isBrowser ? _shellAndBrowser : _shell;

    // OperatingSystem.IsBrowser() is a JIT-folded intrinsic, so it cannot be faked from outside — every
    // public entry point below takes a nullable override instead, which is what lets the WASM branch be
    // proved on a desktop runner (the same seam ShellModuleFeatureGate and AnnotationSessionController use).
    private static bool HostIsBrowser(bool? isBrowser) => isBrowser ?? OperatingSystem.IsBrowser();

    private Playback2DKeymapProfile(Playback2DBinding[] bindings, HashSet<Playback2DAction> overridden,
        IReadOnlyList<string> rejected)
    {
        _bindings = bindings;
        _overridden = overridden;
        Rejected = rejected;
    }

    /// <summary>The shipped table with no overrides — what a tab with no container, or no settings, routes.</summary>
    public static Playback2DKeymapProfile Default { get; }

    /// <summary>Every binding, bound and reserved, in the shipped table's authored order.</summary>
    public IReadOnlyList<Playback2DBinding> Bindings => _bindings;

    /// <summary>
    ///     The override rows this profile refused, one human-readable line each (<c>"row: reason"</c>).
    ///     Empty on a clean profile. Surfaced in Settings so a rejected rebind says why.
    /// </summary>
    public IReadOnlyList<string> Rejected { get; }

    /// <summary>Whether <paramref name="action" />'s gesture came from the user rather than the shipped table.</summary>
    /// <param name="action">The action.</param>
    public bool IsOverridden(Playback2DAction action) => _overridden.Contains(action);

    /// <summary>
    ///     Composes <paramref name="overrides" /> — <c>"Action=Gesture"</c> rows, e.g. <c>"NextRound=Shift+R"</c> —
    ///     over the shipped table. NEVER throws: a row that is malformed, names an unknown or reserved
    ///     action, carries an unparseable gesture, shadows a shell accelerator, or duplicates another
    ///     binding within its scope is dropped and reported in <paramref name="rejected" />.
    /// </summary>
    /// <param name="overrides">The persisted rows, in file order.</param>
    /// <param name="rejected">Receives one line per dropped row.</param>
    /// <param name="isBrowser">
    ///     Whether to also refuse the gestures the BROWSER takes before the page sees them. Null reads
    ///     the real host; a test passes <c>true</c> to prove the WASM branch on a desktop runner.
    /// </param>
    public static Playback2DKeymapProfile FromOverrides(IEnumerable<string> overrides,
        out IReadOnlyList<string> rejected, bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        bool browser = HostIsBrowser(isBrowser);
        (Key Key, KeyModifiers Modifiers)[] reserved = Reserved(browser);

        List<string> problems = [];
        List<(string Row, Playback2DAction Action, Key Key, KeyModifiers Modifiers)> accepted = [];

        foreach (string raw in overrides)
        {
            // A blank index carries no intent (a shrunk array, a stray comma in a hand-edited file), so
            // it is skipped silently — reporting it would bury the row that IS a mistake.
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string row = raw.Trim();
            if (!TryParseRow(row, browser, out Playback2DAction action, out Key key,
                    out KeyModifiers modifiers, out string error))
            {
                problems.Add($"{row}: {error}");
                continue;
            }

            if (accepted.Exists(a => a.Action == action))
            {
                problems.Add($"{row}: {action} is already rebound by an earlier row");
                continue;
            }

            accepted.Add((row, action, key, modifiers));
        }

        // Apply the whole accepted set FIRST. A swap (PrevRound=E together with NextRound=Q) is clean
        // only as a batch — checked row by row, its first half collides with the second half's not-yet-
        // replaced default — so this pass is what lets a user exchange two keys at all.
        Playback2DBinding[] table = [.. Playback2DKeymap.Default];
        foreach ((string _, Playback2DAction action, Key key, KeyModifiers modifiers) in accepted)
        {
            table[_indexByAction[action]] = Rebind(table[_indexByAction[action]], key, modifiers);
        }

        HashSet<Playback2DAction> overridden = [.. accepted.Select(a => a.Action)];

        if (Playback2DKeymap.FindConflicts(table, reserved).Count > 0)
        {
            // The batch does not stand up. Re-apply row by row and drop only the rows that actually
            // collide, so the report names the offending row instead of condemning the whole file.
            table = [.. Playback2DKeymap.Default];
            overridden = [];
            foreach ((string row, Playback2DAction action, Key key, KeyModifiers modifiers) in accepted)
            {
                Playback2DBinding[] candidate = [.. table];
                candidate[_indexByAction[action]] = Rebind(candidate[_indexByAction[action]], key, modifiers);

                IReadOnlyList<string> conflicts = Playback2DKeymap.FindConflicts(candidate, reserved);
                if (conflicts.Count > 0)
                {
                    problems.Add($"{row}: {conflicts[0]}");
                    continue;
                }

                table = candidate;
                overridden.Add(action);
            }
        }

        rejected = problems;
        return new Playback2DKeymapProfile(table, overridden, problems);
    }

    /// <summary>
    ///     Whether one candidate row could join <paramref name="existing" /> — <c>""</c> when it can,
    ///     otherwise the reason, verbatim from <see cref="FromOverrides" />. The Settings rebind affordance
    ///     asks this BEFORE persisting, so a refused rebind can say why instead of vanishing.
    /// </summary>
    /// <param name="existing">The rows already persisted.</param>
    /// <param name="candidate">The row being proposed.</param>
    /// <param name="isBrowser">
    ///     Whether the browser's own chrome gestures are also refused. Null reads the real host.
    /// </param>
    public static string ValidateOverride(IEnumerable<string> existing, string candidate,
        bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);

        // The candidate goes LAST and its own action's previous row is dropped: the rows already in the
        // file win, so the new gesture has to justify itself against them rather than silently unseating
        // one. That is also what makes the reason below always be about the candidate.
        string action = ActionPartOf(candidate);
        List<string> rows =
            [.. existing.Where(r => !string.Equals(ActionPartOf(r), action, StringComparison.OrdinalIgnoreCase))];
        rows.Add(candidate);

        _ = FromOverrides(rows, out IReadOnlyList<string> rejected, isBrowser);

        string prefix = candidate + ": ";
        foreach (string line in rejected)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return "";
    }

    /// <summary>
    ///     Builds the persisted row for a gesture. Written with the tokens <see cref="KeyGesture.Parse" />
    ///     accepts, never the display text — <c>"←"</c> and <c>"Esc"</c> are for human eyes and would not
    ///     survive the next load.
    /// </summary>
    /// <param name="action">The action being rebound.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers.</param>
    public static string Row(Playback2DAction action, Key key, KeyModifiers modifiers)
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

        parts.Add(key.ToString());
        return $"{action}={string.Join("+", parts)}";
    }

    /// <summary>
    ///     Resolves a keypress against THIS profile. Same two rules as the shipped table: a tool-scoped
    ///     binding shadows an always-scoped one while a drawing tool is active, and a RESERVED binding
    ///     resolves to nothing so the view leaves the key unhandled rather than pretending to act.
    /// </summary>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The active modifiers.</param>
    /// <param name="toolActive">Whether a pointer tool (draw / erase) is selected.</param>
    /// <param name="action">The resolved action.</param>
    public bool TryResolve(Key key, KeyModifiers modifiers, bool toolActive, out Playback2DAction action)
    {
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
    /// <param name="e">The key event.</param>
    /// <param name="toolActive">Whether a pointer tool is selected.</param>
    /// <param name="action">The resolved action.</param>
    public bool TryResolve(KeyEventArgs e, bool toolActive, out Playback2DAction action)
    {
        if (e is null)
        {
            action = Playback2DAction.None;
            return false;
        }

        return TryResolve(e.Key, e.KeyModifiers, toolActive, out action);
    }

    /// <summary>
    ///     This profile's binding for <paramref name="action" />, or null when unbound. The view's KeyUp
    ///     needs it: hold-to-pan is released by KEY, and a rebound pan key released against a hard-coded
    ///     <c>Space</c> would leave the surface panning forever.
    /// </summary>
    /// <param name="action">The action.</param>
    public Playback2DBinding? BindingFor(Playback2DAction action)
    {
        foreach (Playback2DBinding binding in _bindings)
        {
            if (binding.Action == action)
            {
                return binding;
            }
        }

        return null;
    }

    /// <summary>
    ///     Display text for an action's gesture (e.g. "Shift+E"), "" when unbound. For tooltips and the
    ///     Settings rows — resolved from THIS profile, so a rebound key shows the user's gesture rather
    ///     than the shipped one.
    /// </summary>
    /// <param name="action">The action.</param>
    public string GestureText(Playback2DAction action) =>
        BindingFor(action) is { } binding ? Playback2DKeymap.Format(binding.Key, binding.Modifiers) : "";

    private bool TryFind(Playback2DBindingScope scope, Key key, KeyModifiers modifiers,
        out Playback2DBinding found)
    {
        foreach (Playback2DBinding binding in _bindings)
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

    private static Playback2DBinding Rebind(Playback2DBinding binding, Key key, KeyModifiers modifiers) =>
        binding with
        {
            Key = key,
            Modifiers = modifiers
        };

    // "Action=Gesture" → the two halves, with every reason a row can be refused. Split at the FIRST '='
    // because no gesture Avalonia parses contains one.
    private static bool TryParseRow(string row, bool isBrowser, out Playback2DAction action, out Key key,
        out KeyModifiers modifiers, out string error)
    {
        action = Playback2DAction.None;
        key = Key.None;
        modifiers = KeyModifiers.None;

        int split = row.IndexOf('=', StringComparison.Ordinal);
        if (split <= 0 || split == row.Length - 1)
        {
            error = "not an \"Action=Gesture\" row";
            return false;
        }

        string name = row[..split].Trim();
        string gesture = row[(split + 1)..].Trim();

        // Enum.TryParse happily accepts "7" and yields a defined member, so a leading non-letter is
        // refused up front: an ordinal in a settings file is a typo, not a binding.
        if (name.Length == 0 || !char.IsLetter(name[0])
                             || !Enum.TryParse(name, true, out action)
                             || !Enum.IsDefined(action) || action == Playback2DAction.None)
        {
            error = $"'{name}' is not a 2D playback action";
            action = Playback2DAction.None;
            return false;
        }

        if (_multiBound.Contains(action) || !_indexByAction.TryGetValue(action, out int at))
        {
            error = $"{action} has no single shipped binding to rebind";
            return false;
        }

        Playback2DBinding shipped = Playback2DKeymap.Default[at];
        if (shipped.IsReserved)
        {
            error = $"{action} is reserved and not bindable";
            return false;
        }

        KeyGesture parsed;
        try
        {
            parsed = KeyGesture.Parse(gesture);
        }
        catch (Exception)
        {
            // KeyGesture.Parse throws a handful of unrelated types for a bad string; which one it picked
            // tells the user nothing, so they all mean the same thing here.
            error = $"'{gesture}' is not a key gesture";
            return false;
        }

        if (parsed.Key == Key.None)
        {
            error = $"'{gesture}' names no key";
            return false;
        }

        key = parsed.Key;
        modifiers = parsed.KeyModifiers;

        // The browser check comes FIRST for the gestures that are both (Ctrl+W is a shell accelerator and
        // a close-tab): on the WASM head the browser is the reason the key can never arrive, and "it is an
        // app-wide shortcut" would send the user looking for a conflict inside DemoViewer that they could
        // resolve. Neither list is consulted for a refusal it cannot explain.
        if (Playback2DKeymap.IsBrowserReserved(key, modifiers, isBrowser))
        {
            error = $"{Playback2DKeymap.Format(key, modifiers)} is taken by the browser — the page never "
                    + "receives it";
            return false;
        }

        foreach ((Key shellKey, KeyModifiers shellModifiers) in _shell)
        {
            if (shellKey == key && shellModifiers == modifiers)
            {
                error = $"{Playback2DKeymap.Format(key, modifiers)} is an app-wide shortcut";
                return false;
            }
        }

        error = "";
        return true;
    }

    private static string ActionPartOf(string row)
    {
        int split = row.IndexOf('=', StringComparison.Ordinal);
        return split <= 0 ? row.Trim() : row[..split].Trim();
    }

    private static Dictionary<Playback2DAction, int> BuildIndex(out HashSet<Playback2DAction> multiBound)
    {
        Dictionary<Playback2DAction, int> index = new();
        multiBound = [];

        IReadOnlyList<Playback2DBinding> shipped = Playback2DKeymap.Default;
        for (int i = 0; i < shipped.Count; i++)
        {
            if (!index.TryAdd(shipped[i].Action, i))
            {
                multiBound.Add(shipped[i].Action);
            }
        }

        return index;
    }
}
