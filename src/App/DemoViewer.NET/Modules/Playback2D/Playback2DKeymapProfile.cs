#region

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Avalonia.Input;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The keymap a running 2D Playback tab actually routes through: the shipped
///     <see cref="Playback2DKeymap" /> table with the user's
///     <c>Playback2DSettings.KeybindOverrides</c> composed over it.
///     <para>
///         <see cref="Playback2DKeymap" />'s static constructor THROWS on a conflicting table. That is
///         right for a table compiled into the binary: a collision is a bug, and it should fail at first
///         touch. It is fatal for one assembled from a hand-editable JSON settings file: a single typo
///         would surface as a <c>TypeInitializationException</c> that takes the 2D tab down with no way
///         to fix it from inside the app, and <c>Playback2DTabViewModel</c> is built by a bare
///         <c>new()</c> with no DI, so there is nowhere useful to catch it. This type validates instead:
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
    private static readonly Playback2DBinding[] _shipped;
    private static readonly Dictionary<string, int> _indexByAction;
    private static readonly HashSet<string> _multiBound;
    private static readonly (Key Key, KeyModifiers Modifiers)[] _shell;
    private static readonly (Key Key, KeyModifiers Modifiers)[] _shellAndBrowser;

    private readonly Playback2DBinding[] _bindings;
    private readonly HashSet<string> _overridden;

    // Ordered, not field initializers: BuildIndex hands _multiBound back through an out parameter, and
    // Default is built from the same shipped table both of them read. _shipped is core (Playback2DKeymap)
    // union every pack's commands (CommandRegistry.Default.EffectiveBindings), unconditionally: no live
    // gate reaches this static table, so a pack-off chord has no view surface to act on instead.
    static Playback2DKeymapProfile()
    {
        _shipped = [.. CommandRegistry.Default.EffectiveBindings];
        _indexByAction = BuildIndex(out _multiBound);
        _shell = [.. Playback2DKeymap.ReservedGestures(false)];
        _shellAndBrowser = [.. Playback2DKeymap.ReservedGestures(true)];
        Default = new Playback2DKeymapProfile([.. _shipped], new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
    }

    private Playback2DKeymapProfile(Playback2DBinding[] bindings, HashSet<string> overridden,
        IReadOnlyList<string> rejected)
    {
        _bindings = bindings;
        _overridden = overridden;
        Rejected = rejected;
    }

    /// <summary>The shipped table with no overrides: what a tab with no container, or no settings, routes.</summary>
    public static Playback2DKeymapProfile Default { get; }

    /// <summary>Every binding, bound and reserved, in the shipped table's authored order.</summary>
    public IReadOnlyList<Playback2DBinding> Bindings => _bindings;

    /// <summary>
    ///     The override rows this profile refused, one human-readable line each (<c>"row: reason"</c>).
    ///     Empty on a clean profile. Surfaced in Settings so a rejected rebind says why.
    /// </summary>
    public IReadOnlyList<string> Rejected { get; }

    // Which gestures a rebind may not claim on THIS head. Both sets are materialised in the static ctor
    // rather than composed per call: FromOverrides runs the whole conflict sweep once per accepted row on
    // the fallback path, and re-concatenating two arrays inside that loop is churn for nothing.
    private static (Key Key, KeyModifiers Modifiers)[] Reserved(bool isBrowser) =>
        isBrowser ? _shellAndBrowser : _shell;

    // OperatingSystem.IsBrowser() is a JIT-folded intrinsic and cannot be faked from outside. Every
    // public entry point below takes a nullable override instead, so the WASM branch can be proved on a
    // desktop runner (the same seam ShellModuleFeatureGate and AnnotationSessionController use).
    private static bool HostIsBrowser(bool? isBrowser) => isBrowser ?? OperatingSystem.IsBrowser();

    /// <summary>Whether <paramref name="actionId" />'s gesture came from the user rather than the shipped table.</summary>
    /// <param name="actionId">The action's id.</param>
    public bool IsOverridden(string actionId) => _overridden.Contains(actionId);

    /// <summary>Whether a core action's gesture came from the user rather than the shipped table.</summary>
    /// <param name="action">The core action.</param>
    public bool IsOverridden(Playback2DAction action) => IsOverridden(Playback2DActionIds.Of(action));

    /// <summary>
    ///     The current id an override row's left-hand side names: a core or command id in its own casing, or
    ///     an old command id through <see cref="CommandRegistry.Aliases" />. <paramref name="name" /> itself,
    ///     trimmed, when it names no action.
    /// </summary>
    /// <param name="name">The id as written.</param>
    public static string CanonicalActionId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        return CommandRegistry.Default.Canonical(trimmed) ?? trimmed;
    }

    /// <summary>
    ///     Composes <paramref name="overrides" /> (<c>"Action=Gesture"</c> rows, e.g. <c>"NextRound=Shift+R"</c>)
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
        List<(string Row, string Action, Key Key, KeyModifiers Modifiers)> accepted = [];

        foreach (string raw in overrides)
        {
            // A blank index carries no intent (a shrunk array, a stray comma in a hand-edited file), so
            // it is skipped silently; reporting it would bury the row that IS a mistake.
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string row = raw.Trim();
            if (!TryParseRow(row, browser, out string action, out Key key,
                    out KeyModifiers modifiers, out string error))
            {
                problems.Add($"{row}: {error}");
                continue;
            }

            if (accepted.Exists(a => string.Equals(a.Action, action, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{row}: {action} is already rebound by an earlier row");
                continue;
            }

            accepted.Add((row, action, key, modifiers));
        }

        // Apply the whole accepted set FIRST. A swap (PrevRound=E together with NextRound=Q) is clean
        // only as a batch: checked row by row, its first half collides with the second half's not-yet-
        // replaced default. This pass is what lets a user exchange two keys at all.
        Playback2DBinding[] table = [.. _shipped];
        foreach ((string _, string action, Key key, KeyModifiers modifiers) in accepted)
        {
            table[_indexByAction[action]] = Rebind(table[_indexByAction[action]], key, modifiers);
        }

        HashSet<string> overridden = new(accepted.Select(a => a.Action), StringComparer.OrdinalIgnoreCase);

        if (Playback2DKeymap.FindConflicts(table, reserved).Count > 0)
        {
            // The batch does not stand up. Re-apply row by row and drop only the rows that actually
            // collide, so the report names the offending row instead of condemning the whole file.
            table = [.. _shipped];
            overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string row, string action, Key key, KeyModifiers modifiers) in accepted)
            {
                Playback2DBinding[] candidate = [.. table];
                candidate[_indexByAction[action]] = Rebind(candidate[_indexByAction[action]], key, modifiers);

                IReadOnlyList<string> conflicts = Playback2DKeymap.FindConflicts(candidate, reserved);
                if (conflicts.Count > 0)
                {
                    problems.Add($"{row}: {AnnotatePackOwners(conflicts[0])}");
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
    ///     Whether one candidate row could join <paramref name="existing" />: <c>""</c> when it can,
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
        // Compared by current id, so an old-id row for the same action is replaced too.
        string action = ActionIdOfRow(candidate);
        List<string> rows =
            [.. existing.Where(r => !string.Equals(ActionIdOfRow(r), action, StringComparison.OrdinalIgnoreCase))];
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
    ///     Builds the persisted row for a gesture. The gesture itself comes from the one formatter the
    ///     display text also comes from, asked for the tokens <see cref="KeyGesture.Parse" /> accepts
    ///     rather than the human ones: <c>"←"</c> and <c>"Esc"</c> would not survive the next load.
    /// </summary>
    /// <param name="actionId">The id of the action being rebound.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers.</param>
    public static string Row(string actionId, Key key, KeyModifiers modifiers) =>
        $"{actionId}={Playback2DKeymap.Format(key, modifiers, false)}";

    /// <summary>The persisted row for a core action's gesture.</summary>
    /// <param name="action">The core action being rebound.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers.</param>
    public static string Row(Playback2DAction action, Key key, KeyModifiers modifiers) =>
        Row(Playback2DActionIds.Of(action), key, modifiers);

    /// <summary>
    ///     Resolves a keypress against THIS profile. Same two rules as the shipped table: a tool-scoped
    ///     binding shadows an always-scoped one while a drawing tool is active, and a RESERVED binding
    ///     resolves to nothing so the view leaves the key unhandled rather than pretending to act.
    /// </summary>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The active modifiers.</param>
    /// <param name="toolActive">Whether a pointer tool (draw / erase) is selected.</param>
    /// <param name="actionId">The resolved action's id.</param>
    public bool TryResolve(Key key, KeyModifiers modifiers, bool toolActive, [NotNullWhen(true)] out string? actionId)
    {
        if (toolActive && TryFind(Playback2DBindingScope.WhenToolActive, key, modifiers,
                out Playback2DBinding tool))
        {
            actionId = tool.IsReserved ? null : tool.ActionId;
            return !tool.IsReserved;
        }

        if (TryFind(Playback2DBindingScope.Always, key, modifiers, out Playback2DBinding always)
            && !always.IsReserved)
        {
            actionId = always.ActionId;
            return true;
        }

        actionId = null;
        return false;
    }

    /// <summary>
    ///     Resolves a keypress against ONE scope's rows of this profile. An extension's focus scope asks it
    ///     from a key handler, which runs before
    ///     <see cref="TryResolve(Key, KeyModifiers, bool, out string)" />: that is what puts the scope above
    ///     the tool and always scopes. A reserved row resolves to nothing.
    /// </summary>
    /// <param name="scope">The scope to look in.</param>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The active modifiers.</param>
    /// <param name="actionId">The resolved action's id.</param>
    public bool TryResolveInScope(Playback2DBindingScope scope, Key key, KeyModifiers modifiers,
        [NotNullWhen(true)] out string? actionId)
    {
        if (TryFind(scope, key, modifiers, out Playback2DBinding found) && !found.IsReserved)
        {
            actionId = found.ActionId;
            return true;
        }

        actionId = null;
        return false;
    }

    /// <summary>Convenience overload for the view's KeyDown handler.</summary>
    /// <param name="e">The key event.</param>
    /// <param name="toolActive">Whether a pointer tool is selected.</param>
    /// <param name="actionId">The resolved action's id.</param>
    public bool TryResolve(KeyEventArgs e, bool toolActive, [NotNullWhen(true)] out string? actionId)
    {
        if (e is null)
        {
            actionId = null;
            return false;
        }

        return TryResolve(e.Key, e.KeyModifiers, toolActive, out actionId);
    }

    /// <summary>
    ///     This profile's binding for <paramref name="actionId" />, or null when unbound. The view's KeyUp
    ///     needs it: hold-to-pan is released by KEY, and a rebound pan key released against a hard-coded
    ///     <c>Space</c> would leave the surface panning forever.
    /// </summary>
    /// <param name="actionId">The action's id, matched ignoring case.</param>
    public Playback2DBinding? BindingFor(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        foreach (Playback2DBinding binding in _bindings)
        {
            if (string.Equals(binding.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            {
                return binding;
            }
        }

        return null;
    }

    /// <summary>This profile's binding for a core action, or null when unbound.</summary>
    /// <param name="action">The core action.</param>
    public Playback2DBinding? BindingFor(Playback2DAction action) => BindingFor(Playback2DActionIds.Of(action));

    /// <summary>
    ///     Display text for an action's gesture (e.g. "Shift+E"), "" when unbound. For tooltips and the
    ///     Settings rows, resolved from THIS profile, so a rebound key shows the user's gesture rather
    ///     than the shipped one.
    /// </summary>
    /// <param name="actionId">The action's id.</param>
    public string GestureText(string actionId) =>
        BindingFor(actionId) is { } binding ? Playback2DKeymap.Format(binding.Key, binding.Modifiers) : "";

    /// <summary>Display text for a core action's gesture, "" when unbound.</summary>
    /// <param name="action">The core action.</param>
    public string GestureText(Playback2DAction action) => GestureText(Playback2DActionIds.Of(action));

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

    // A conflict naming a pack-owned action (TagNote, AddStep, …) is otherwise opaque the moment that
    // pack is off: the Settings list hides its row, so the user reads an action name that appears
    // nowhere they can see. Named here regardless of the pack's current on/off state, since this is
    // about which pack a chord belongs to, not whether it is live right now.
    private static string AnnotatePackOwners(string conflict)
    {
        foreach (PackCommand entry in CommandRegistry.Default.PackCommands)
        {
            string id = entry.Command.Id;
            conflict = Regex.Replace(conflict, $@"(?<![\w.]){Regex.Escape(id)}(?![\w.])", $"{id} ({entry.PackLabel})");
        }

        return conflict;
    }

    // "Action=Gesture" → the two halves, with every reason a row can be refused. Split at the FIRST '='
    // because no gesture Avalonia parses contains one.
    private static bool TryParseRow(string row, bool isBrowser, out string action, out Key key,
        out KeyModifiers modifiers, out string error)
    {
        action = "";
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

        // Matched ignoring case, and an old command id through the registry's aliases, so a row written
        // before a command id changed still applies to the action it always named.
        if (name.Length == 0 || !char.IsLetter(name[0]) || CommandRegistry.Default.Canonical(name) is not { } id)
        {
            error = $"'{name}' is not a 2D playback action";
            return false;
        }

        action = id;

        if (_multiBound.Contains(action) || !_indexByAction.TryGetValue(action, out int at))
        {
            error = $"{action} has no single shipped binding to rebind";
            return false;
        }

        Playback2DBinding shipped = _shipped[at];
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

    /// <summary>The current id an override row's left-hand side names, through <see cref="CanonicalActionId" />.</summary>
    /// <param name="row">An <c>"Action=Gesture"</c> row.</param>
    public static string ActionIdOfRow(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return CanonicalActionId(ActionPartOf(row));
    }

    private static string ActionPartOf(string row)
    {
        int split = row.IndexOf('=', StringComparison.Ordinal);
        return split <= 0 ? row.Trim() : row[..split].Trim();
    }

    private static Dictionary<string, int> BuildIndex(out HashSet<string> multiBound)
    {
        Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
        multiBound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Playback2DBinding[] shipped = _shipped;
        for (int i = 0; i < shipped.Length; i++)
        {
            if (!index.TryAdd(shipped[i].ActionId, i))
            {
                multiBound.Add(shipped[i].ActionId);
            }
        }

        return index;
    }
}
