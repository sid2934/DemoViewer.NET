#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>How far a demo has been indexed (drives the "…" placeholders in the browser).</summary>
public enum DemoIndexState
{
    /// <summary>Only filesystem fields known; metadata not yet read.</summary>
    Pending,

    /// <summary>Background full parse in progress (players/duration).</summary>
    Indexing,

    /// <summary>Fully indexed, map + players + duration all known.</summary>
    Indexed,

    /// <summary>Parse failed (corrupt / unsupported); filesystem fields still shown.</summary>
    Failed
}

/// <summary>
///     One demo in the library browser. Filesystem fields (name/size/date) are set once at discovery;
///     the metadata fields fill in across the two indexing tiers: <b>tier 1</b> (cheap first-frame header:
///     <see cref="MapName" />, <see cref="ServerName" />) then <b>tier 2</b> (background full parse:
///     <see cref="Players" />, <see cref="DurationSeconds" />). Observable so the grid updates live as
///     enrichment completes.
/// </summary>
public partial class DemoEntry : ObservableObject
{
    private static readonly string[] _mapPrefixes = ["de_", "cs_", "ar_", "dz_", "gd_", "coop_"];

    [ObservableProperty]
    private string? _ctClan;

    // Final scoreboard: CCSTeam.m_iScore per side at match end (CT = team 3, T = team 2), entity-replayed
    // in tier 2 AFTER players/duration (so those show first). Null until computed / when the demo has no
    // clean final score. Clans are populated on pro/HLTV demos (empty on matchmaking).
    [ObservableProperty]
    private int? _ctScore;

    [ObservableProperty]
    private string? _demoVersion;

    // ── Content dedup ──
    // Other folders that hold a byte-identical copy of THIS demo. This entry is the primary (the
    // lexicographically-smallest path across the copies); the shadows are not shown as their own cards
    // (appear once) and are not processed separately (processed once). Empty when the demo is unique.
    [ObservableProperty]
    private IReadOnlyList<string> _duplicateFolders = [];

    [ObservableProperty]
    private double _durationSeconds;

    // ── Tier 1 (cheap header) ──
    [ObservableProperty]
    private string? _mapName;

    // ── Tier 2 (full parse) ──
    [ObservableProperty]
    private IReadOnlyList<string> _players = [];

    [ObservableProperty]
    private int _roundCount;

    /// <summary>
    ///     The cache row behind this entry holds a stale half-resolved score, so the score was WITHHELD when
    ///     the row was applied and has not been re-derived yet. Set by <c>DemoLibraryService.ApplyCache</c>;
    ///     drives <see cref="NeedsScoreRepair" />. Nothing on disk is modified.
    /// </summary>
    [ObservableProperty]
    private bool _scoreRepairPending;

    [ObservableProperty]
    private string? _serverName;

    [ObservableProperty]
    private DemoIndexState _state = DemoIndexState.Pending;

    [ObservableProperty]
    private string? _tClan;

    [ObservableProperty]
    private int? _tScore;

    /// <summary>Absolute path to the .dem file (identity).</summary>
    public required string FilePath { get; init; }

    /// <summary>File name (no directory).</summary>
    public required string FileName { get; init; }

    /// <summary>Containing folder (for grouping / display).</summary>
    public required string Directory { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>Last-modified time (local), the browser's default "date".</summary>
    public required DateTime Modified { get; init; }

    /// <summary>Prettified map name for display, e.g. <c>de_nuke → "Nuke"</c>. Falls back to "Unknown".</summary>
    public string MapDisplay => PrettifyMap(MapName);

    /// <summary>Human file size, e.g. "482 MB".</summary>
    public string SizeDisplay => FormatSize(FileSizeBytes);

    /// <summary>Relative modified time, e.g. "2d ago".</summary>
    public string ModifiedDisplay => RelativeTime(Modified);

    /// <summary>
    ///     Comma-joined player names, or a placeholder while tier 2 is pending. Sanitized for
    ///     display: this string feeds a WRAPPING TextBlock, and hostile player names (invisible
    ///     bidi controls + orphaned combining marks) crash Avalonia's wrap splitter: see
    ///     <see cref="DisplayText.Sanitize" />.
    /// </summary>
    public string PlayersDisplay => Players.Count > 0
        ? DisplayText.Sanitize(string.Join(", ", Players))
        : State is DemoIndexState.Failed
            ? "—"
            : "…";

    /// <summary>Match length as m:ss, or blank until known.</summary>
    public string DurationDisplay => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss",
            CultureInfo.InvariantCulture)
        : "";

    /// <summary>True once a clean final score is known (both sides present and the match wasn't warmup-only).</summary>
    public bool HasScore => CtScore is int c && TScore is int t && c + t > 0;

    /// <summary>
    ///     Show the "score needs re-deriving" badge: the cache row holds a stale half-score, it was withheld
    ///     rather than rendered, and nothing has re-derived it yet.
    ///     <para>
    ///         This state exists because an absent score is otherwise SILENT: <see cref="HasScore" /> needs
    ///         both sides, so a withheld score just quietly loses the badge and reads identically to a demo
    ///         whose score genuinely cannot be resolved (warmup, truncated). Since re-deriving is on-demand
    ///         and costs a full parse per demo, that silence would leave hundreds of cards looking scoreless
    ///         with nothing saying why. "Absent because stale" and "absent because unresolvable" must not
    ///         render the same.
    ///     </para>
    ///     <para>
    ///         ANDed with <c>!HasScore</c> so a completed re-derivation stops showing it even if the flag has
    ///         not been cleared yet, the score itself is the more reliable evidence of the two.
    ///     </para>
    /// </summary>
    public bool NeedsScoreRepair => ScoreRepairPending && !HasScore;

    /// <summary>True when clan/team names are known (pro/HLTV demos), matchmaking demos have none.</summary>
    public bool HasClans => !string.IsNullOrWhiteSpace(CtClan) && !string.IsNullOrWhiteSpace(TClan);

    /// <summary>
    ///     True while THIS demo's tier-2 full parse is in flight. The indexer runs one demo at a
    ///     time, so at most one entry is ever true: the card's animated top bar and the list row's
    ///     pulsing dot are unique "being analyzed right now" signals.
    /// </summary>
    public bool IsIndexing => State == DemoIndexState.Indexing;

    /// <summary>True when the full parse failed: drives the static red indicator.</summary>
    public bool IsFailed => State == DemoIndexState.Failed;

    /// <summary>Card subtitle: the clan matchup on pro demos (e.g. "Vitality vs FUT"), else the server name.</summary>
    public string? SubtitleDisplay => HasClans ? $"{CtClan} vs {TClan}" : ServerName;

    /// <summary>True when a byte-identical copy of this demo exists in another registered folder.</summary>
    public bool HasDuplicates => DuplicateFolders.Count > 0;

    /// <summary>
    ///     Compact duplicate hint, e.g. "＋1 copy", a byte-identical copy lives in another folder,
    ///     collapsed into this one card. The tooltip lists the folders (<see cref="DuplicateTooltip" />).
    /// </summary>
    public string DuplicateHint => DuplicateFolders.Count switch
    {
        0 => "",
        1 => "＋1 copy",
        int n => $"＋{n} copies"
    };

    /// <summary>Tooltip body listing the other folders that hold a byte-identical copy of this demo.</summary>
    public string DuplicateTooltip => DuplicateFolders.Count == 0
        ? ""
        : "Also in:\n" + string.Join("\n", DuplicateFolders);

    // Keep the computed display strings in sync with their backing observable fields.
    partial void OnMapNameChanged(string? value) => OnPropertyChanged(nameof(MapDisplay));
    partial void OnPlayersChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(PlayersDisplay));

    partial void OnStateChanged(DemoIndexState value)
    {
        OnPropertyChanged(nameof(PlayersDisplay));
        OnPropertyChanged(nameof(IsIndexing));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(DurationDisplay));

    // NeedsScoreRepair is derived from BOTH the flag and the score, so both sides have to notify. Raising it
    // only from the flag leaves the badge correct on first render and stale the moment a repair completes:
    // the card would keep saying "needs re-deriving" over a score it had just written.
    partial void OnCtScoreChanged(int? value)
    {
        OnPropertyChanged(nameof(HasScore));
        OnPropertyChanged(nameof(NeedsScoreRepair));
    }

    partial void OnTScoreChanged(int? value)
    {
        OnPropertyChanged(nameof(HasScore));
        OnPropertyChanged(nameof(NeedsScoreRepair));
    }

    partial void OnScoreRepairPendingChanged(bool value) => OnPropertyChanged(nameof(NeedsScoreRepair));

    partial void OnServerNameChanged(string? value) => OnPropertyChanged(nameof(SubtitleDisplay));

    partial void OnDuplicateFoldersChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(DuplicateHint));
        OnPropertyChanged(nameof(DuplicateTooltip));
    }

    partial void OnCtClanChanged(string? value)
    {
        OnPropertyChanged(nameof(HasClans));
        OnPropertyChanged(nameof(SubtitleDisplay));
    }

    partial void OnTClanChanged(string? value)
    {
        OnPropertyChanged(nameof(HasClans));
        OnPropertyChanged(nameof(SubtitleDisplay));
    }

    /// <summary>Strips the <c>de_/cs_/ar_/dz_</c> prefix and title-cases: <c>de_dust2 → "Dust2"</c>.</summary>
    public static string PrettifyMap(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return "Unknown";
        }

        string s = map;
        foreach (string prefix in _mapPrefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        return s.Length == 0 ? map : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return v < 10 && u > 0
            ? $"{v:0.0} {units[u]}"
            : $"{v:0} {units[u]}";
    }

    /// <summary>
    ///     Formats a local <see cref="DateTime" /> as a compact relative age (e.g. "just now", "2d ago").
    ///     Public + static so the Library landing's recent-files rows reuse the one formatter
    ///     rather than forking a second copy.
    /// </summary>
    public static string RelativeTime(DateTime when)
    {
        TimeSpan ago = DateTime.Now - when;
        if (ago < TimeSpan.Zero)
        {
            ago = TimeSpan.Zero;
        }

        if (ago.TotalMinutes < 1)
        {
            return "just now";
        }

        if (ago.TotalHours < 1)
        {
            return $"{(int)ago.TotalMinutes}m ago";
        }

        if (ago.TotalDays < 1)
        {
            return $"{(int)ago.TotalHours}h ago";
        }

        if (ago.TotalDays < 30)
        {
            return $"{(int)ago.TotalDays}d ago";
        }

        if (ago.TotalDays < 365)
        {
            return $"{(int)(ago.TotalDays / 30)}mo ago";
        }

        return $"{(int)(ago.TotalDays / 365)}y ago";
    }
}

/// <summary>Root persisted library state (folders + metadata cache). Serialized to AppData as JSON.</summary>
public sealed class DemoLibraryData
{
    /// <summary>Cache schema version: bump to invalidate stale rows when fields change.</summary>
    public int SchemaVersion { get; set; } = DemoLibraryCacheEntry.CurrentSchema;

    /// <summary>User-configured root folders to scan (recursively) for demos.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Cached per-file metadata, keyed by path (validated against size + mtime on load).</summary>
    public List<DemoLibraryCacheEntry> Cache { get; set; } = [];
}

/// <summary>
///     One cached demo-metadata row. Validity is keyed on (<see cref="Path" />, <see cref="Size" />,
///     <see cref="ModifiedTicks" />), a size/mtime change invalidates it and forces a re-index.
/// </summary>
public sealed class DemoLibraryCacheEntry
{
    /// <summary>Current cache schema; adding a metadata field that changes meaning should bump this.</summary>
    public const int CurrentSchema = 1;

    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }

    // Tier 1
    public string? Map { get; set; }
    public string? Server { get; set; }
    public string? DemoVersion { get; set; }

    // Tier 2
    public List<string>? Players { get; set; }
    public double DurationSeconds { get; set; }
    public int RoundCount { get; set; }

    // Final score (nullable → additive, no schema bump needed; ScoreComputed drives opportunistic backfill
    // of pre-existing cache rows without wiping the whole cache).
    public int? CtScore { get; set; }
    public int? Score { get; set; }
    public string? CtClan { get; set; }
    public string? Clan { get; set; }

    /// <summary>
    ///     True once the tier-2 entity replay has run (whether or not it produced a score).
    ///     <para>
    ///         Note there is deliberately NO persisted "score repair pending" companion to this. A stale
    ///         half-score is detected from THIS ROW'S OWN DATA on every load and refused at the read boundary
    ///         (<c>DemoLibraryService.ApplyCache</c>), which is what makes the state impossible to lose. See
    ///         the half-score repair design.
    ///     </para>
    /// </summary>
    public bool ScoreComputed { get; set; }

    /// <summary>True once the background full parse (tier 2) has completed for this file.</summary>
    public bool FullyIndexed { get; set; }

    /// <summary>
    ///     Content hash (SHA-256, lowercase hex) of the file bytes, the Phase-4b content-dedup key.
    ///     Nullable → additive, no schema bump. Computed only for files that SHARE an exact byte size
    ///     with another discovered file (the size pre-filter: byte-identical copies always share a size,
    ///     so a unique-size file can have no content twin and is never hashed). Cached like the rest of the
    ///     row, keyed on (path, size, mtime), so rescans don't re-read the whole file.
    /// </summary>
    public string? Sha256 { get; set; }
}
