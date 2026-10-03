#region

using Avalonia.Input;
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
///     The merged command set: the core <see cref="Playback2DKeymap" /> table, whose rows already carry a
///     command id one to one with their <see cref="Playback2DAction" /> name, union every compiled-in
///     pack's <see cref="IFeaturePack.Commands" />. Built once, with no DI and no composition root, so a
///     bare-constructed view model in a headless test resolves pack chords the same as a fully composed app.
///     <para>
///         A pack row whose default chord collides with an earlier row (core, the shell, or another pack)
///         is reported in <see cref="Conflicts" /> and ships with no default chord of its own: the earlier
///         row keeps it, but silently is the one thing that never happens.
///     </para>
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<Playback2DAction, PackCommand> _packByAction;

    private CommandRegistry(IReadOnlyList<Playback2DBinding> effectiveBindings,
        IReadOnlyList<PackCommand> packCommands, IReadOnlyList<string> conflicts,
        Dictionary<Playback2DAction, PackCommand> packByAction)
    {
        EffectiveBindings = effectiveBindings;
        PackCommands = packCommands;
        Conflicts = conflicts;
        _packByAction = packByAction;
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
    ///     One line per default-chord collision found while composing. Empty on the real, shipped table;
    ///     a fake pack's colliding default proves this is populated rather than throwing or silently
    ///     preferring one side.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; }

    /// <summary>Built once from the compiled-in pack list (<see cref="FeaturePacks.Default" />).</summary>
    public static CommandRegistry Default { get; } = Build(FeaturePacks.Default);

    /// <summary>
    ///     Looks up a pack command by the <see cref="Playback2DAction" /> its id parses to. Used by the
    ///     keybind settings list to label and gate a row; absent for every core action.
    /// </summary>
    public IReadOnlyDictionary<Playback2DAction, PackCommand> PackOwnerByAction => _packByAction;

    /// <summary>
    ///     Composes <paramref name="packs" />' commands over the core table. Pure: no shared or cached
    ///     state, so a test proves the conflict and pack-off paths with its own fake pack instead of
    ///     touching <see cref="Default" />.
    /// </summary>
    public static CommandRegistry Build(IReadOnlyList<IFeaturePack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        List<Playback2DBinding> bindings = [.. Playback2DKeymap.Default];
        List<PackCommand> packCommands = [];
        List<string> conflicts = [];
        Dictionary<Playback2DAction, PackCommand> byAction = new();

        foreach (IFeaturePack pack in packs)
        {
            string label = pack.Features.FirstOrDefault(f => f.Id == pack.FeatureId)?.Label ?? pack.FeatureId;

            foreach (CommandDescriptor command in pack.Commands)
            {
                if (!TryParseAction(command.Id, out Playback2DAction action))
                {
                    throw new InvalidOperationException(
                        $"Pack '{pack.Id}' command '{command.Id}' names no Playback2DAction; a command id " +
                        "must equal the action's enum name.");
                }

                PackCommand entry = new(command, pack.Id, pack.FeatureId, label);
                if (!byAction.TryAdd(action, entry))
                {
                    throw new InvalidOperationException(
                        $"Pack '{pack.Id}' command '{command.Id}' duplicates an action another pack already owns.");
                }

                packCommands.Add(entry);

                if (command.DefaultChord is not { } chord)
                {
                    continue; // no default to place on the table; the command is still looked up by id
                }

                Playback2DBinding candidate = new(action, chord.Key, chord.KeyModifiers,
                    ParseScope(command.Scope), command.Label, false);

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
        }

        return new CommandRegistry(bindings, packCommands, conflicts, byAction);
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
        ArgumentNullException.ThrowIfNull(isPackEnabled);
        Playback2DBindingScope parsedScope = ParseScope(scope);

        foreach (Playback2DBinding binding in EffectiveBindings)
        {
            if (binding.IsReserved || binding.Key != key || binding.Modifiers != modifiers
                || binding.Scope != parsedScope)
            {
                continue;
            }

            if (_packByAction.TryGetValue(binding.Action, out PackCommand? owner))
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
    ///     root runs between a pack's <c>Contribute(...)</c> call and its <see cref="IFeaturePack.Commands" />
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

    // A core row has no pack to call back into generically (CommandRegistry is core and may not reference
    // the Strat canvas VM), so its Run targets the 2D Playback tab VM, the shared surface every core
    // action already routes through today.
    private static CommandDescriptor CoreCommand(Playback2DBinding binding) => new(
        binding.Action.ToString(), binding.Description, ScopeName(binding.Scope),
        new KeyGesture(binding.Key, binding.Modifiers),
        ctx => ctx.Target is Playback2DTabViewModel tab && tab.ExecuteAction(binding.Action));

    internal static Playback2DBindingScope ParseScope(string scope) => scope switch
    {
        "playback2d" => Playback2DBindingScope.Always,
        "playback2d.tool" => Playback2DBindingScope.WhenToolActive,
        "playback2d.palette" => Playback2DBindingScope.WhenPaletteFocused,
        "playback2d.suggestion" => Playback2DBindingScope.WhenSuggestionSelected,
        _ => throw new InvalidOperationException($"Unknown command scope '{scope}'.")
    };

    internal static string ScopeName(Playback2DBindingScope scope) => scope switch
    {
        Playback2DBindingScope.Always => "playback2d",
        Playback2DBindingScope.WhenToolActive => "playback2d.tool",
        Playback2DBindingScope.WhenPaletteFocused => "playback2d.palette",
        Playback2DBindingScope.WhenSuggestionSelected => "playback2d.suggestion",
        _ => throw new InvalidOperationException($"Unknown binding scope '{scope}'.")
    };

    private static bool TryParseAction(string id, out Playback2DAction action) =>
        Enum.TryParse(id, false, out action) && Enum.IsDefined(action);
}
