#region

using System.Runtime.Loader;
using Avalonia.Input;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>One pack's command, with the pack metadata the keybind settings list groups and gates by.</summary>
/// <param name="Command">The descriptor itself.</param>
/// <param name="PackId">The owning pack's reverse-DNS id.</param>
/// <param name="PackFeatureId">The owning pack's umbrella gate id, e.g. <c>"pack.stratbook"</c>.</param>
/// <param name="PackLabel">The owning pack's display label, e.g. <c>"Strat Book extension"</c>.</param>
public sealed record PackCommand(CommandDescriptor Command, string PackId, string PackFeatureId, string PackLabel);

/// <summary>
///     The bare command ids compiled-in extensions shipped before command ids carried the extension's prefix.
///     Users' saved keybind overrides still name commands that way, so override parsing maps each bare id to
///     the prefixed one. The host keeps this table because the old ids live in the host's settings file.
/// </summary>
public static class LegacyCommandIds
{
    // Extensions whose every command once shipped as its id minus the extension's prefix.
    private static readonly HashSet<string> _shippedBare = new(StringComparer.Ordinal) { "net.demoviewer.pack.stratbook" };

    /// <summary>Old id to current id for <paramref name="pack" />'s commands; empty for every other extension.</summary>
    /// <param name="pack">The extension.</param>
    /// <param name="commands">The commands it contributes.</param>
    public static IReadOnlyDictionary<string, string> For(IExtension pack, IReadOnlyList<CommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(commands);
        if (!_shippedBare.Contains(pack.Id))
        {
            return new Dictionary<string, string>();
        }

        string prefix = pack.Id + ".";
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        foreach (CommandDescriptor command in commands)
        {
            if (IsPrefixed(command.Id, prefix))
            {
                aliases[command.Id[prefix.Length..]] = command.Id;
            }
        }

        return aliases;
    }

    private static bool IsPrefixed(string? id, string prefix) =>
        id is not null && id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.Ordinal);
}

/// <summary>
///     The merged command set: the core <see cref="Playback2DKeymap" /> table, whose rows carry their
///     <see cref="Playback2DAction" /> name as their id, union every compiled-in and third-party extension's
///     <see cref="IExtension.Commands" />. Built once, with no DI and no composition root, so a bare-constructed
///     view model in a headless test resolves pack chords the same as a fully composed app.
///     <para>
///         Ids are unique across the merged set, ignoring case, since override rows are matched that way. A
///         compiled-in extension that breaks that, or names a scope it does not declare, fails the build of
///         the registry. A third-party extension's offending command is reported in <see cref="Conflicts" />
///         and left out, as is one whose id or scope lacks the extension's own prefix.
///     </para>
///     <para>
///         A pack row whose default chord collides with an earlier row (core, the shell, or another pack)
///         is reported in <see cref="Conflicts" /> and ships with no default chord of its own: the earlier
///         row keeps it, but silently is the one thing that never happens.
///     </para>
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, string> _aliases;
    private readonly Dictionary<string, PackCommand> _packByAction;
    private readonly Dictionary<string, CommandScope> _scopes;

    private CommandRegistry(IReadOnlyList<Playback2DBinding> effectiveBindings,
        IReadOnlyList<PackCommand> packCommands, IReadOnlyList<string> conflicts,
        Dictionary<string, PackCommand> packByAction, Dictionary<string, string> aliases,
        Dictionary<string, CommandScope> scopes)
    {
        EffectiveBindings = effectiveBindings;
        PackCommands = packCommands;
        Conflicts = conflicts;
        _packByAction = packByAction;
        _aliases = aliases;
        _scopes = scopes;
        ActionIds = [.. Playback2DActionIds.Core, .. packCommands.Select(c => c.Command.Id)];
    }

    /// <summary>
    ///     Core rows (<see cref="Playback2DKeymap.Default" />, reserved rows included) union every pack
    ///     command whose default chord did not collide, in that order. What <c>Playback2DKeymapProfile</c>
    ///     composes the user's overrides over.
    /// </summary>
    public IReadOnlyList<Playback2DBinding> EffectiveBindings { get; }

    /// <summary>Every pack command, whether or not its default chord made it into <see cref="EffectiveBindings" />.</summary>
    public IReadOnlyList<PackCommand> PackCommands { get; }

    /// <summary>
    ///     One line per default-chord collision found while composing, and per third-party command left out.
    ///     Empty on the real, shipped table.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; }

    /// <summary>Every action id: the core ids, then every pack command's, in composition order.</summary>
    public IReadOnlyList<string> ActionIds { get; }

    /// <summary>Built once from the compatible packs (<see cref="FeaturePacks.Compatible" />).</summary>
    // Lazy: the loader builds registries to check a third-party extension before FeaturePacks is set, and
    // reading FeaturePacks then would freeze it empty.
    private static readonly Lazy<CommandRegistry> _default = new(() => Build(FeaturePacks.Compatible, static (pack, ex) =>
    {
        if (ExtensionFaults.Current is { } faults)
        {
            faults.FailStartup(faults.GuardFor(pack).Scope, "commands", ex);
        }
    }));

    /// <summary>The registry over the packs this launch composed.</summary>
    public static CommandRegistry Default => _default.Value;

    /// <summary>
    ///     Looks up a pack command by id, ignoring case. Used by the keybind settings list to label and gate a
    ///     row; absent for every core action.
    /// </summary>
    public IReadOnlyDictionary<string, PackCommand> PackOwnerByAction => _packByAction;

    /// <summary>Old command ids a compiled-in extension still answers to, ignoring case, each to its current id.</summary>
    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    /// <summary>
    ///     Composes <paramref name="packs" />' commands over the core table. Pure: no shared or cached
    ///     state, so a test proves the conflict and pack-off paths with its own fake pack instead of
    ///     touching <see cref="Default" />.
    /// </summary>
    /// <param name="packs">The packs, in composition order.</param>
    /// <param name="onFault">
    ///     Told when a pack's <c>Features</c>, <c>Commands</c> or <c>CommandScopes</c> getter throws; that pack's
    ///     commands are left out. Null rethrows.
    /// </param>
    /// <param name="isThirdParty">
    ///     Whether a pack is a third-party extension. Null reads where its assembly was loaded from: a
    ///     third-party extension loads into its own <see cref="ExternalLoadContext" />.
    /// </param>
    /// <param name="legacyIds">
    ///     A compiled-in pack's old command ids, each to its current id. Null reads <see cref="LegacyCommandIds.For" />.
    ///     Never asked of a third-party extension.
    /// </param>
    /// <exception cref="InvalidOperationException">A compiled-in pack's command id, scope or alias is invalid.</exception>
    public static CommandRegistry Build(IReadOnlyList<IExtension> packs, Action<IExtension, Exception>? onFault = null,
        Func<IExtension, bool>? isThirdParty = null,
        Func<IExtension, IReadOnlyList<CommandDescriptor>, IReadOnlyDictionary<string, string>>? legacyIds = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        isThirdParty ??= IsLoadedExternally;
        legacyIds ??= LegacyCommandIds.For;

        List<Playback2DBinding> bindings = [.. Playback2DKeymap.Default];
        List<PackCommand> packCommands = [];
        List<string> conflicts = [];
        HashSet<string> taken = new(Playback2DActionIds.Core, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PackCommand> byAction = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CommandScope> scopes = new(StringComparer.Ordinal);
        Dictionary<string, string> scopeOwners = new(StringComparer.Ordinal);

        foreach (IExtension pack in packs)
        {
            bool thirdParty = isThirdParty(pack);

            // Every getter is the extension's code; read each once, up front.
            string label;
            CommandDescriptor[] commands;
            CommandScope[] declaredScopes;
            KeyValuePair<string, string>[] declaredAliases;
            try
            {
                label = pack.Features.FirstOrDefault(f => f.Id == pack.FeatureId)?.Label ?? pack.FeatureId;
                commands = [.. pack.Commands];
                declaredScopes = [.. pack.CommandScopes];
                declaredAliases = thirdParty ? [] : [.. legacyIds(pack, commands)];
            }
            catch (Exception ex) when (onFault is not null && ex is not OutOfMemoryException)
            {
                onFault(pack, ex);
                continue;
            }

            string prefix = pack.Id + ".";

            void Refuse(string problem)
            {
                if (!thirdParty)
                {
                    throw new InvalidOperationException($"Pack '{pack.Id}' {problem}.");
                }

                conflicts.Add($"{pack.Id}: {problem}; left out");
            }

            foreach (CommandScope scope in declaredScopes)
            {
                if (!IsPrefixed(scope.Id, prefix))
                {
                    Refuse($"scope '{scope.Id}' is not prefixed with '{prefix}'");
                }
                else if (scopes.TryAdd(scope.Id, scope))
                {
                    scopeOwners[scope.Id] = pack.Id;
                }
                else
                {
                    Refuse($"scope '{scope.Id}' is already declared");
                }
            }

            foreach (CommandDescriptor command in commands)
            {
                if (string.IsNullOrWhiteSpace(command.Id))
                {
                    Refuse("has a command with no id");
                    continue;
                }

                if (thirdParty && !IsPrefixed(command.Id, prefix))
                {
                    Refuse($"command '{command.Id}' is not prefixed with '{prefix}'");
                    continue;
                }

                if (taken.Contains(command.Id) || aliases.ContainsKey(command.Id))
                {
                    Refuse($"command '{command.Id}' duplicates an action id already taken");
                    continue;
                }

                Playback2DBindingScope bindingScope = new(command.Scope ?? "");
                if (!bindingScope.IsCore
                    && !(scopeOwners.TryGetValue(bindingScope.Name, out string? owner) && owner == pack.Id))
                {
                    Refuse($"command '{command.Id}' names scope '{command.Scope}', which it does not declare");
                    continue;
                }

                PackCommand entry = new(command, pack.Id, pack.FeatureId, label);
                taken.Add(command.Id);
                byAction[command.Id] = entry;
                packCommands.Add(entry);

                if (command.DefaultChord is not { } chord)
                {
                    continue; // no default to place on the table; the command is still looked up by id
                }

                Playback2DBinding candidate = new(command.Id, chord.Key, chord.KeyModifiers, bindingScope,
                    command.Label, false);

                // bindings-so-far is already conflict-free (the core table's own static ctor guarantees
                // that, and every earlier pack row only ever joined after passing this same check), so
                // any conflict this call reports necessarily involves the candidate just added.
                IReadOnlyList<string> found =
                    Playback2DKeymap.FindConflicts([.. bindings, candidate], Playback2DKeymap.ShellReservedGestures);
                if (found.Count > 0)
                {
                    conflicts.AddRange(found.Select(f => $"{pack.Id}: {f}"));
                    continue; // the earlier row (core or an earlier pack) keeps the chord; reported, not silent
                }

                bindings.Add(candidate);
            }

            foreach ((string old, string current) in declaredAliases)
            {
                if (!byAction.TryGetValue(current, out PackCommand? target) || target.PackId != pack.Id)
                {
                    throw new InvalidOperationException(
                        $"Pack '{pack.Id}' aliases '{old}' to '{current}', which is not one of its commands.");
                }

                if (taken.Contains(old) || !aliases.TryAdd(old, target.Command.Id))
                {
                    throw new InvalidOperationException(
                        $"Pack '{pack.Id}' alias '{old}' duplicates an action id already taken.");
                }
            }
        }

        return new CommandRegistry(bindings, packCommands, conflicts, byAction, aliases, scopes);
    }

    /// <summary>
    ///     The current id <paramref name="name" /> names, ignoring case: a core id, a pack command's id, or an
    ///     old id through <see cref="Aliases" />. Null when it names no action.
    /// </summary>
    /// <param name="name">The id as written, e.g. the left-hand side of an override row.</param>
    public string? Canonical(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (string core in Playback2DActionIds.Core)
        {
            if (string.Equals(core, name, StringComparison.OrdinalIgnoreCase))
            {
                return core;
            }
        }

        if (_packByAction.TryGetValue(name, out PackCommand? command))
        {
            return command.Command.Id;
        }

        return _aliases.GetValueOrDefault(name);
    }

    /// <summary>
    ///     How the keybind settings list names a scope: "always", "while drawing", or the label the declaring
    ///     extension gave it.
    /// </summary>
    /// <param name="scope">The scope.</param>
    public string ScopeLabel(Playback2DBindingScope scope)
    {
        if (scope == Playback2DBindingScope.Always)
        {
            return "always";
        }

        if (scope == Playback2DBindingScope.WhenToolActive)
        {
            return "while drawing";
        }

        return scope.Name is { } name && _scopes.TryGetValue(name, out CommandScope? declared) ? declared.Label : scope.Name ?? "";
    }

    /// <summary>
    ///     Resolves a keypress to a command, honouring <paramref name="isPackEnabled" /> for a pack row: a
    ///     chord whose pack is off resolves to nothing, same as an unbound key. A core row always resolves.
    ///     <para>
    ///         Reads <see cref="EffectiveBindings" />, not <see cref="PackCommands" /> directly: a pack
    ///         command that lost a chord collision in <see cref="Build" /> has no row there, so it cannot
    ///         resolve just because its own <see cref="CommandDescriptor.DefaultChord" /> still names the
    ///         gesture. The row that WON the slot (core, or an earlier pack) is what resolves.
    ///     </para>
    /// </summary>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifiers">The active modifiers.</param>
    /// <param name="scope">The scope to resolve in, in the same spelling as <see cref="CommandDescriptor.Scope" />.</param>
    /// <param name="isPackEnabled">Given a pack's <see cref="PackCommand.PackFeatureId" />, whether that pack is on.</param>
    /// <param name="command">The resolved command, or null.</param>
    public bool TryResolve(Key key, KeyModifiers modifiers, string scope, Func<string, bool> isPackEnabled,
        out CommandDescriptor? command)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(isPackEnabled);
        Playback2DBindingScope parsedScope = new(scope);

        foreach (Playback2DBinding binding in EffectiveBindings)
        {
            if (binding.IsReserved || binding.Key != key || binding.Modifiers != modifiers
                || binding.Scope != parsedScope)
            {
                continue;
            }

            if (_packByAction.TryGetValue(binding.ActionId, out PackCommand? owner))
            {
                if (!isPackEnabled(owner.PackFeatureId))
                {
                    command = null;
                    return false;
                }

                command = owner.Command;
                return true;
            }

            command = CoreCommand(binding);
            return true;
        }

        command = null;
        return false;
    }

    /// <summary>
    ///     Whether two command lists agree on id, scope and default chord, position by position.
    ///     <see cref="CommandDescriptor.Run" /> and <see cref="CommandDescriptor.CanRun" /> are delegates
    ///     and compare by reference, so they are deliberately excluded. This is the check the composition
    ///     root runs between a pack's <c>Contribute(...)</c> call and its <see cref="IExtension.Commands" />
    ///     property, so the two channels cannot drift apart.
    /// </summary>
    public static bool CommandsMatch(IReadOnlyList<CommandDescriptor> a, IReadOnlyList<CommandDescriptor> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)
                || !string.Equals(a[i].Scope, b[i].Scope, StringComparison.Ordinal)
                || a[i].DefaultChord?.Key != b[i].DefaultChord?.Key
                || a[i].DefaultChord?.KeyModifiers != b[i].DefaultChord?.KeyModifiers)
            {
                return false;
            }
        }

        return true;
    }

    // A third-party extension is the only kind loaded into an ExternalLoadContext.
    private static bool IsLoadedExternally(IExtension pack) =>
        AssemblyLoadContext.GetLoadContext(pack.GetType().Assembly) is ExternalLoadContext;

    private static bool IsPrefixed(string? id, string prefix) =>
        id is not null && id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.Ordinal);

    // A core row has no pack to call back into generically, so its Run targets the 2D Playback tab VM, the
    // shared surface every core action already routes through.
    private static CommandDescriptor CoreCommand(Playback2DBinding binding) => new(
        binding.ActionId, binding.Description, binding.Scope.Name,
        new KeyGesture(binding.Key, binding.Modifiers),
        ctx => ctx.Target is Playback2DTabViewModel tab && tab.ExecuteAction(binding.ActionId));
}
