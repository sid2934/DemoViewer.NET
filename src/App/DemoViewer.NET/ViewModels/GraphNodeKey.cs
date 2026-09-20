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
///         <b>And a name is not unique within one player either.</b> Two rulesets may declare the same
///         stat id, and the engine then materialises both into a single player's node list under the
///         one name. On the shipped corpus that is <c>enemy_kills_round</c> (declared by
///         <c>highlights_multikill</c> and by <c>kast</c>) and <c>wallbang_kills</c> (by
///         <c>highlights_aim</c> and by <c>kast</c>): 2 of a player's 373 nodes, 20 of the demo's
///         3 791. <see cref="Occurrence" /> separates them by counting copies of a name in
///         <c>MaterializedPlayer.Nodes</c> order, which the build fixes and a re-run reproduces.
///     </para>
///     <para>
///         The key is derived, not assigned, so it is available on the pre-evaluation skeleton as well
///         as after a run, and it is stable across runs of the same demo: it holds the materialising
///         template's index and the player's <em>slot</em>, never the player's name or their position
///         in discovery order. That matters for a coming engine change, which reorders players by
///         discovery and drops roster slots that never materialise; slots do not move under it.
///     </para>
///     <para>
///         Stable across runs of one demo, NOT across a change to the loaded ruleset set:
///         <see cref="TemplateIndex" /> is a position among the materialising templates, and
///         <see cref="Occurrence" /> a position among the copies of one name, so adding, removing or
///         reordering a ruleset can shift either and orphan a persisted breakpoint.
///     </para>
/// </summary>
public readonly record struct GraphNodeKey
{
    // Game is 0 so that default(GraphNodeKey) lands here rather than on a per-player slot.
    private enum GraphNodeScope
    {
        Game = 0,
        PerPlayer = 1
    }

    private const string GameScopePrefix = "g:";
    private const string PerPlayerPrefix = "p";

    // Separates slot from occurrence inside the SECOND wire segment, which holds integers only, so it
    // can never be mistaken for a character of a node name however that name is spelled.
    private const char OccurrenceSeparator = '#';

    private GraphNodeKey(GraphNodeScope scope, int templateIndex, int playerSlot, int occurrence, string name)
    {
        Scope = scope;
        TemplateIndex = templateIndex;
        PlayerSlot = playerSlot;
        Occurrence = occurrence;
        Name = name;
    }

    // An explicit discriminator rather than a -1 sentinel on PlayerSlot, so that default(GraphNodeKey)
    // - which the language permits and TryParse assigns before every false return - is game scope with
    // no name, NOT a well-formed-looking key for template 0 / slot 0.
    private GraphNodeScope Scope { get; }

    /// <summary>The materialising template's index, or <c>-1</c> for a game-scope node.</summary>
    public int TemplateIndex { get; }

    /// <summary>The player's roster slot, or <c>-1</c> for a game-scope node.</summary>
    public int PlayerSlot { get; }

    /// <summary>
    ///     Which copy of <see cref="Name" /> this is within the owning materialisation, counted in
    ///     <c>MaterializedPlayer.Nodes</c> order: <c>0</c> for every name but the handful two rulesets
    ///     both declare, and <c>-1</c> for a game-scope node. Game-scope names do not repeat (61 nodes,
    ///     61 distinct names on the reference demo), so the scaffolding needs no counter.
    /// </summary>
    public int Occurrence { get; }

    /// <summary>The engine's node name. Unique within a scope, not across scopes.</summary>
    public string Name { get; }

    /// <summary>True when this key names a per-player copy rather than a shared scaffolding node.</summary>
    public bool IsPerPlayer => Scope == GraphNodeScope.PerPlayer;

    /// <summary>A node from the shared, game-scope scaffolding: one copy, no owning player.</summary>
    public static GraphNodeKey ForGameScope(string name) => new(GraphNodeScope.Game, -1, -1, -1, name);

    /// <summary>
    ///     One player's copy of a per-player template node. The occurrence-less overload means the
    ///     FIRST copy, which is every node but the two per player a second ruleset re-declares, and is
    ///     also what a key persisted before <see cref="Occurrence" /> existed denotes.
    /// </summary>
    public static GraphNodeKey ForPlayer(int templateIndex, int playerSlot, string name) =>
        ForPlayer(templateIndex, playerSlot, 0, name);

    /// <summary>One player's <paramref name="occurrence" />th copy of a per-player template node.</summary>
    public static GraphNodeKey ForPlayer(int templateIndex, int playerSlot, int occurrence, string name) =>
        new(GraphNodeScope.PerPlayer, templateIndex, playerSlot, occurrence, name);

    /// <summary>
    ///     The wire form: <c>g:{name}</c>, <c>p{template}:{slot}:{name}</c> for the first copy, or
    ///     <c>p{template}:{slot}#{occurrence}:{name}</c> for a later one. Round-trips through
    ///     <see cref="TryParse" />, and is what the breakpoint store writes. Names may contain colons,
    ///     so parsing splits a bounded number of times from the left rather than on every colon.
    ///     <para>
    ///         The occurrence is OMITTED at zero rather than always written, and that is what carries
    ///         the breakpoints already on disk: every per-player key a build before this one persisted
    ///         is byte-for-byte the first copy's form, so it parses and resolves to a node it already
    ///         matched rather than being orphaned. Writing it unconditionally would have invalidated
    ///         every one of them and required a file rewrite to avoid destroying the conditions on
    ///         them, which is the loss the identity work exists to prevent.
    ///     </para>
    /// </summary>
    public override string ToString()
    {
        if (!IsPerPlayer)
        {
            return GameScopePrefix + Name;
        }

        string template = TemplateIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string slot = PlayerSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string occurrence = Occurrence <= 0
            ? string.Empty
            : OccurrenceSeparator + Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $"{PerPlayerPrefix}{template}:{slot}{occurrence}:{Name}";
    }

    /// <summary>
    ///     Parses the <see cref="ToString" /> form. Returns <c>false</c> for anything else, including
    ///     the bare node name a build predating this type would have written. No such value reaches
    ///     here in practice: the pre-v2 breakpoint file is deleted rather than read.
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

        if (!TryParseInt(text.AsSpan(PerPlayerPrefix.Length, firstColon - PerPlayerPrefix.Length),
                out int templateIndex))
        {
            return false;
        }

        // The slot segment carries the occurrence after a '#' when there is one. A malformed
        // occurrence rejects the whole key rather than being dropped, which would leave a
        // well-formed-looking first-copy key pointing at a node the writer did not mean.
        ReadOnlySpan<char> slotSegment = text.AsSpan(firstColon + 1, secondColon - firstColon - 1);
        int occurrence = 0;
        int separator = slotSegment.IndexOf(OccurrenceSeparator);
        if (separator >= 0)
        {
            if (!TryParseInt(slotSegment[(separator + 1)..], out occurrence) || occurrence <= 0)
            {
                return false;
            }

            slotSegment = slotSegment[..separator];
        }

        if (!TryParseInt(slotSegment, out int playerSlot))
        {
            return false;
        }

        key = ForPlayer(templateIndex, playerSlot, occurrence, text[(secondColon + 1)..]);
        return true;
    }

    private static bool TryParseInt(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
}
