namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A demo as the Library lists it: one row of the library's index, which the host keeps in memory. Reading
///     one opens no file. Lists compare by reference, so two reads of an unchanged row are the same instance.
///     <para>
///         A demo is its content (<see cref="Sha256" />), not its path. Copies of one file in several folders, or
///         one share mounted at two paths, are one row with several <see cref="Locations" />. Key what you keep
///         per demo on <see cref="Sha256" /> once it is set; a path only says where the bytes are today.
///     </para>
/// </summary>
/// <param name="FilePath">
///     Where the Library shows the demo: the first of <see cref="Locations" />. Every path in
///     <see cref="Locations" /> answers the same row, and a path can change on a rescan while the demo stays.
/// </param>
/// <param name="FileName">The file name.</param>
/// <param name="MapName">The map, once the demo is indexed.</param>
/// <param name="Modified">The file's last write time, local.</param>
/// <param name="FileSizeBytes">The file's size.</param>
public sealed record LibraryDemo(string FilePath, string FileName, string? MapName, DateTime Modified, long FileSizeBytes)
{
    private readonly IReadOnlyList<string>? _locations;

    /// <summary>
    ///     Lowercase hex SHA-256 of the file: the demo's identity, the key the library and
    ///     <see cref="IExtensionDemoData" /> keep its data under. Null until the library has read the file at
    ///     <see cref="FilePath" /> in full.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    ///     Every path the library has confirmed holds these bytes, <see cref="FilePath" /> first, the rest in
    ///     ordinal order. Just <see cref="FilePath" /> while <see cref="Sha256" /> is null. A path matched to the
    ///     demo without a full read is not listed until a read confirms it. Until then
    ///     <see cref="IExtensionLibrary.Find" /> answers that path with a row of its own with no hash, which
    ///     <see cref="IExtensionLibrary.Demos" /> lists only when it is the only path the library has for the demo.
    /// </summary>
    public IReadOnlyList<string> Locations
    {
        get => _locations ?? [FilePath];
        init => _locations = value is { Count: > 0 } ? value : null;
    }

    /// <summary>The server name from the demo's header, or null.</summary>
    public string? Server { get; init; }

    /// <summary>
    ///     What kind of server recorded the demo, by the name of the parser's <c>DemoSourceKind</c>
    ///     (<c>"Valve"</c>, <c>"Faceit"</c>, <c>"Hltv"</c> and so on), or null when the parse has not said.
    /// </summary>
    public string? SourceKind { get; init; }

    /// <summary>Length of the demo in seconds; 0 until parsed.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Rounds played; 0 until parsed.</summary>
    public int RoundCount { get; init; }

    /// <summary>The CT side's final score, or null when the parse could not resolve it.</summary>
    public int? CtScore { get; init; }

    /// <summary>The T side's final score, or null when the parse could not resolve it.</summary>
    public int? TScore { get; init; }

    /// <summary>The clan tag of the side that ended the demo as CT, or null.</summary>
    public string? CtClan { get; init; }

    /// <summary>The clan tag of the side that ended the demo as T, or null.</summary>
    public string? TClan { get; init; }

    /// <summary>The roster's names as the demo carried them, unsanitized; empty until parsed.</summary>
    public IReadOnlyList<string> PlayerNames { get; init; } = [];

    /// <summary>
    ///     The players who ended the demo on the CT side: humans, not coaches, with a SteamID, sorted by it.
    ///     Null when the library's row was written before it carried sides; the demo's
    ///     <see cref="IExtensionLibrary.GetDetailAsync" /> has the players then.
    /// </summary>
    public IReadOnlyList<LibrarySidePlayer>? CtPlayers { get; init; }

    /// <summary>The players who ended the demo on the T side, by the rule of <see cref="CtPlayers" />.</summary>
    public IReadOnlyList<LibrarySidePlayer>? TPlayers { get; init; }

    /// <summary>
    ///     Rounds won on the CT side across the match, by either team, from the highlights scan; null until the
    ///     scan resolved it, or on a row written before the library carried it.
    /// </summary>
    public int? CtSideWins { get; init; }

    /// <summary>Rounds won on the T side across the match, by the rule of <see cref="CtSideWins" />.</summary>
    public int? TSideWins { get; init; }

    /// <summary>How far the library has read the demo.</summary>
    public LibraryDemoState State { get; init; }

    /// <summary>
    ///     The per-demo facts written into the library's record for this demo, by facet id: Round Facts
    ///     (<c>"roundfacts"</c>) and other analysis outputs. Empty when none were written.
    /// </summary>
    public IReadOnlyList<LibraryFactState> Facts { get; init; } = [];

    /// <summary>The state of the fact <paramref name="id" />, or null when it was never written.</summary>
    /// <param name="id">The facet id.</param>
    public LibraryFactState? Fact(string id)
    {
        foreach (LibraryFactState fact in Facts)
        {
            if (string.Equals(fact.Id, id, StringComparison.Ordinal))
            {
                return fact;
            }
        }

        return null;
    }

    /// <summary>The players of side <paramref name="side" />: 3 for CT, 2 for T, the parser's numbering.</summary>
    /// <param name="side">3 for CT, 2 for T.</param>
    public IReadOnlyList<LibrarySidePlayer>? PlayersOn(int side) => side == 3 ? CtPlayers : side == 2 ? TPlayers : null;
}

/// <summary>How far the library has read a demo.</summary>
public enum LibraryDemoState
{
    /// <summary>Only the file is known: path, size, write time.</summary>
    Found,

    /// <summary>The header is read: map and server.</summary>
    HeaderRead,

    /// <summary>The demo is parsed: duration, roster, rounds, score.</summary>
    Parsed,

    /// <summary>The highlights scan ran on it as well.</summary>
    Analyzed
}

/// <summary>A player on one side at the end of a demo, as the library's row carries it.</summary>
/// <param name="SteamId64">The player's SteamID64.</param>
/// <param name="Name">The name the demo carried, unsanitized.</param>
public sealed record LibrarySidePlayer(ulong SteamId64, string Name)
{
    /// <summary>Every slot the player held in the demo: one, or more after a reconnect.</summary>
    public IReadOnlyList<int> Slots { get; init; } = [];
}

/// <summary>What the library's row says about one fact written for a demo. Reading it opens no file.</summary>
/// <param name="Id">The facet id, such as <c>"roundfacts"</c>.</param>
/// <param name="Schema">The shape it was written at; 0 when it never was.</param>
/// <param name="Fingerprint">What it was computed under, or null when it was marked for a rebuild.</param>
/// <param name="State">How its last write ended.</param>
/// <param name="Count">The writer's own count (rows, items), or 0.</param>
public sealed record LibraryFactState(string Id, int Schema, string? Fingerprint, DemoDataState State, int Count)
{
    /// <summary>True when it was written at <paramref name="schema" /> under <paramref name="fingerprint" />.</summary>
    /// <param name="schema">The shape the reader expects.</param>
    /// <param name="fingerprint">The fingerprint the reader expects.</param>
    public bool IsCurrent(int schema, string? fingerprint) =>
        State == DemoDataState.Written && Schema == schema && string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal);

    /// <summary>True when rows were written at any schema under some fingerprint.</summary>
    public bool IsWritten => Schema > 0 && Fingerprint is not null;
}

/// <summary>A player of a demo, as the library's record holds them.</summary>
/// <param name="Slot">The player's slot in the demo.</param>
/// <param name="Name">The name the demo carried, unsanitized.</param>
/// <param name="SteamId64">The player's SteamID64, or 0 when the demo carried none.</param>
/// <param name="Team">The side at the end of the demo: 3 for CT, 2 for T, anything else for a spectator.</param>
/// <param name="IsBot">True for a bot.</param>
/// <param name="IsCoach">True for a registered coach, who sits on a side without playing.</param>
public sealed record LibraryPlayer(int Slot, string Name, ulong SteamId64, int Team, bool IsBot, bool IsCoach);

/// <summary>Where a round starts.</summary>
/// <param name="Number">The round's number, from 1.</param>
/// <param name="StartTick">The tick it starts on, in the demo's frame clock.</param>
public sealed record LibraryRound(int Number, int StartTick);

/// <summary>What the library's record holds for one demo beyond its row: the roster and the rounds.</summary>
/// <param name="Demo">The demo's row.</param>
/// <param name="TickRate">Ticks per second; 0 until parsed.</param>
/// <param name="TickCount">The demo's length in ticks; 0 until parsed.</param>
/// <param name="ServerStartTick">The server tick of the demo's first frame.</param>
/// <param name="Players">Every player, spectators and coaches included.</param>
/// <param name="Rounds">Where each round starts, in order.</param>
public sealed record LibraryDemoDetail(
    LibraryDemo Demo,
    int TickRate,
    int TickCount,
    int ServerStartTick,
    IReadOnlyList<LibraryPlayer> Players,
    IReadOnlyList<LibraryRound> Rounds);

/// <summary>How <see cref="IExtensionLibrary.Query" /> orders its results.</summary>
public enum LibrarySort
{
    /// <summary>Newest file first.</summary>
    NewestFirst,

    /// <summary>Oldest file first.</summary>
    OldestFirst,

    /// <summary>By file name.</summary>
    FileName,

    /// <summary>By map, then newest first.</summary>
    Map
}

/// <summary>A filter and a page over the library. Every criterion left null matches every demo.</summary>
public sealed record LibraryQuery
{
    /// <summary>The map, compared ignoring case.</summary>
    public string? Map { get; init; }

    /// <summary>Files written at or after this time.</summary>
    public DateTime? ModifiedFrom { get; init; }

    /// <summary>Files written before this time.</summary>
    public DateTime? ModifiedBefore { get; init; }

    /// <summary>A clan tag on either side, compared ignoring case.</summary>
    public string? Clan { get; init; }

    /// <summary>A part of a player's name, compared ignoring case.</summary>
    public string? PlayerName { get; init; }

    /// <summary>A player on either side, by SteamID64.</summary>
    public ulong? SteamId64 { get; init; }

    /// <summary>Demos read at least this far.</summary>
    public LibraryDemoState? AtLeast { get; init; }

    /// <summary>Demos with this fact written (<see cref="LibraryFactState.IsWritten" />).</summary>
    public string? HasFact { get; init; }

    /// <summary>The order.</summary>
    public LibrarySort Sort { get; init; }

    /// <summary>Matches to skip before the page.</summary>
    public int Skip { get; init; }

    /// <summary>The page's size; null for every match after <see cref="Skip" />.</summary>
    public int? Take { get; init; }
}

/// <summary>One page of a <see cref="IExtensionLibrary.Query" />.</summary>
/// <param name="Demos">The page.</param>
/// <param name="Total">How many demos matched, across every page.</param>
public sealed record LibraryPage(IReadOnlyList<LibraryDemo> Demos, int Total);

/// <summary>What changed in the library.</summary>
public enum LibraryChangeKind
{
    /// <summary>The demo joined the library.</summary>
    Added,

    /// <summary>The demo left the library.</summary>
    Removed,

    /// <summary>The demo's row changed: a read, a parse, a rename of its hash.</summary>
    Updated,

    /// <summary>Only the demo's facts changed (<see cref="LibraryDemo.Facts" />).</summary>
    FactsUpdated
}

/// <summary>One change to the library.</summary>
/// <param name="Path">
///     The demo's <see cref="LibraryDemo.FilePath" />, or null when many demos changed at once; the kind is then
///     <see cref="LibraryChangeKind.Updated" />. A path joining or leaving a demo that has others is reported that
///     way too, as it can change which path the demo is shown at.
/// </param>
/// <param name="Kind">What changed.</param>
public sealed record LibraryChange(string? Path, LibraryChangeKind Kind);

/// <summary>
///     The demo library, read only. <see cref="Demos" />, <see cref="Find" />, <see cref="FindBySha256" /> and
///     <see cref="Query" /> read the index the host keeps in memory and are safe on any thread.
///     <see cref="GetDetailAsync" /> reads one demo's record from disk. Work over many demos belongs in a record
///     pass (<see cref="IExtensionContributions.RecordPass" />) or a queued job, not a loop over
///     <see cref="GetDetailAsync" /> from the UI.
/// </summary>
public interface IExtensionLibrary
{
    /// <summary>
    ///     Every demo in the library, as of now, one row per demo however many paths hold it. A new list after any
    ///     change; the same list until then.
    /// </summary>
    IReadOnlyList<LibraryDemo> Demos { get; }

    /// <summary>
    ///     The demo with <paramref name="path" /> among its <see cref="LibraryDemo.Locations" />, compared ignoring
    ///     case, or null. Any of a demo's paths answers the same row, seen from its <see cref="LibraryDemo.FilePath" />.
    ///     A path matched to a known demo without a full read answers a row of that path alone with no
    ///     <see cref="LibraryDemo.Sha256" />, not listed in <see cref="Demos" /> while the demo has a confirmed
    ///     path. A path the library no longer lists answers null, though the demo's data may be kept for a while in
    ///     case its file comes back.
    /// </summary>
    /// <param name="path">Any path of the demo.</param>
    LibraryDemo? Find(string path);

    /// <summary>
    ///     The demo whose content hashes to <paramref name="sha256" />, or null when no path the library lists is
    ///     confirmed to hold it. Copies of one demo are one row, seen from the path the Library shows.
    /// </summary>
    /// <param name="sha256">Lowercase hex SHA-256.</param>
    LibraryDemo? FindBySha256(string sha256);

    /// <summary>The demos matching <paramref name="query" />, one page of them.</summary>
    /// <param name="query">The filter, the order and the page.</param>
    LibraryPage Query(LibraryQuery query);

    /// <summary>
    ///     The demo's roster and rounds from its record, or null when the library has no record for it. One small
    ///     file read and no parse. Called off the UI thread it reads before returning; called on the UI thread it
    ///     reads as a job on the processing queue.
    /// </summary>
    /// <param name="path">Any path of the demo.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    Task<LibraryDemoDetail?> GetDetailAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The analysis outputs the library holds for its demos: highlights, Round Facts and ruleset tables.</summary>
    IAnalysisFacts Facts { get; }

    /// <summary>Raised on the UI thread after the library changed. Many changes at once arrive as one, with a null path.</summary>
    event Action<LibraryChange>? Changed;
}

/// <summary>One choice in a <see cref="LibraryFilter" />.</summary>
/// <param name="Key">What <see cref="LibraryFilter.Matches" /> receives.</param>
/// <param name="Display">What the list shows.</param>
public sealed record LibraryFilterItem(string Key, string Display);

/// <summary>A drop-down filter in the Library's filter bar.</summary>
/// <param name="Label">The drop-down's label.</param>
/// <param name="Items">Its choices.</param>
/// <param name="Matches">Whether a demo passes for the chosen key.</param>
/// <param name="Tooltip">The drop-down's tooltip.</param>
public sealed record LibraryFilter(
    string Label, IReadOnlyList<LibraryFilterItem> Items, Func<LibraryDemo, string, bool> Matches, string? Tooltip = null);

/// <summary>A chip on a Library row.</summary>
/// <param name="Label">The chip text.</param>
/// <param name="Tooltip">The chip tooltip.</param>
/// <param name="IsPinned">True when the user chose it rather than a heuristic.</param>
public sealed record LibraryBadge(string Label, string? Tooltip, bool IsPinned);

/// <summary>A Library filter, a per-row badge, or both. Raise <see cref="Changed" /> when either may have changed.</summary>
public interface ILibraryContribution
{
    /// <summary>Shown only while this feature is on; null for while the extension is on.</summary>
    string? FeatureId => null;

    /// <summary>Raised when the filter's items or any badge may have changed.</summary>
    event Action? Changed;

    /// <summary>The filter, or null for none.</summary>
    LibraryFilter? Filter { get; }

    /// <summary>True when this contribution draws badges.</summary>
    bool HasBadge { get; }

    /// <summary>The badge for <paramref name="demo" />, or null for none.</summary>
    LibraryBadge? BadgeFor(LibraryDemo demo);

    /// <summary>Badges for many rows at once, keyed by <see cref="LibraryDemo.FilePath" />. Override to batch lookups.</summary>
    IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<LibraryDemo> demos)
    {
        ArgumentNullException.ThrowIfNull(demos);
        Dictionary<string, LibraryBadge?> result = new(StringComparer.Ordinal);
        foreach (LibraryDemo demo in demos)
        {
            result[demo.FilePath] = BadgeFor(demo);
        }

        return result;
    }

    /// <summary>The labels the user can pin from a row's menu.</summary>
    IReadOnlyList<string> BadgeLabels { get; }

    /// <summary>The menu entry that clears a pinned label, or null for none.</summary>
    string? BadgeResetLabel { get; }

    /// <summary>The tooltip of that entry.</summary>
    string? BadgeResetTooltip => null;

    /// <summary>Pins <paramref name="label" /> on <paramref name="demo" />, or clears the pin for null.</summary>
    void SetLabel(LibraryDemo demo, string? label);
}
