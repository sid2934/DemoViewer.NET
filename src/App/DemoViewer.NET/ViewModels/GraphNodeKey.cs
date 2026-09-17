namespace DemoViewer.NET.ViewModels;

/// <summary>
///     The stable identity of a node in the Analysis rule graph, and the only thing that may be
///     persisted to refer to one.
///     <para>
///         <b>A node's name is not unique.</b> The engine's <c>StateNode.Name</c> is documented as a
///         display name, and it is unique only across the game-scope scaffolding. Once the graph
///         includes the per-player nodes that actually ran, one real demo produces 3 791 nodes sharing
///         432 distinct names: 371 of those names occur ten times over, once per player slot
///         (<c>Alive</c>, <c>Survived</c>, <c>round_team_alive</c>, and so on). Keying anything on the
///         name alone silently resolves to whichever copy happened to be first.
///     </para>
///     <para>
///         The key is derived, not assigned, so it is available on the pre-evaluation skeleton as well
///         as after a run, and it is stable across runs of the same demo: it holds the materialising
///         template's index and the player's <em>slot</em>, never the player's name or their position
///         in discovery order. That matters for a coming engine change, which reorders players by
///         discovery and drops roster slots that never materialise; slots do not move under it.
///     </para>
/// </summary>
public readonly record struct GraphNodeKey
{
    private const string GameScopePrefix = "g:";
    private const string PerPlayerPrefix = "p";

    private GraphNodeKey(int templateIndex, int playerSlot, string name)
    {
        TemplateIndex = templateIndex;
        PlayerSlot = playerSlot;
        Name = name;
    }

    /// <summary>The materialising template's index, or <c>-1</c> for a game-scope node.</summary>
    public int TemplateIndex { get; }

    /// <summary>The player's roster slot, or <c>-1</c> for a game-scope node.</summary>
    public int PlayerSlot { get; }

    /// <summary>The engine's node name. Unique within a scope, not across scopes.</summary>
    public string Name { get; init; }

    /// <summary>True when this key names a per-player copy rather than a shared scaffolding node.</summary>
    public bool IsPerPlayer => PlayerSlot >= 0;

    /// <summary>A node from the shared, game-scope scaffolding: one copy, no owning player.</summary>
    public static GraphNodeKey ForGameScope(string name) => new(-1, -1, name);

    /// <summary>One player's copy of a per-player template node.</summary>
    public static GraphNodeKey ForPlayer(int templateIndex, int playerSlot, string name) =>
        new(templateIndex, playerSlot, name);

    /// <summary>
    ///     The wire form: <c>g:{name}</c> or <c>p{template}:{slot}:{name}</c>. Round-trips through
    ///     <see cref="TryParse" />, and is what the breakpoint store writes. Names may contain colons,
    ///     so parsing splits a bounded number of times from the left rather than on every colon.
    /// </summary>
    public override string ToString() =>
        IsPerPlayer
            ? $"{PerPlayerPrefix}{TemplateIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}:"
              + $"{PlayerSlot.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{Name}"
            : GameScopePrefix + Name;

    /// <summary>
    ///     Parses the <see cref="ToString" /> form. Returns <c>false</c> for anything else, including a
    ///     bare node name written by a build that predates this type: the caller treats that as a record
    ///     it cannot honour rather than guessing which of ten nodes was meant.
    /// </summary>
    public static bool TryParse(string? text, out GraphNodeKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith(GameScopePrefix, StringComparison.Ordinal))
        {
            string name = text[GameScopePrefix.Length..];
            if (name.Length == 0)
            {
                return false;
            }

            key = ForGameScope(name);
            return true;
        }

        if (!text.StartsWith(PerPlayerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int firstColon = text.IndexOf(':', PerPlayerPrefix.Length);
        if (firstColon < 0)
        {
            return false;
        }

        int secondColon = text.IndexOf(':', firstColon + 1);
        if (secondColon < 0 || secondColon + 1 >= text.Length)
        {
            return false;
        }

        if (!int.TryParse(
                text[PerPlayerPrefix.Length..firstColon],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int templateIndex)
            || !int.TryParse(
                text[(firstColon + 1)..secondColon],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int playerSlot))
        {
            return false;
        }

        key = ForPlayer(templateIndex, playerSlot, text[(secondColon + 1)..]);
        return true;
    }
}
