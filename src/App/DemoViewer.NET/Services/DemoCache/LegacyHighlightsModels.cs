#region

using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

// ─────────────────────────────────────────────────────────────────────────────
//  MIGRATION-ONLY DTOs: the on-disk shape of the RETIRED <ConfigRoot>/highlights.json.
//
//  Nothing writes these types and nothing reads them at runtime. They exist so
//  LegacyCacheMigration can deserialize a file left behind by v0.5.2 and earlier, once, on a
//  machine that has one. The live model is DemoCacheRecord.
//
//  They live here, next to the migration that is their only consumer, rather than under
//  Modules/Highlights/ where the store that wrote them used to be. The store is gone, and
//  leaving its models in a feature folder invited exactly the mistake this file is named
//  against: reading them as a live model. The `Legacy` prefix is not decoration; without it
//  CachedRound and CachedPlayer collide with the real ones in this namespace.
//
//  Do not "modernize" these. Every property name, and the PascalCase convention itself, is
//  the format some user's file is already written in. The retired store serialized with plain
//  `new JsonSerializerOptions { WriteIndented = true }`, no naming policy, which is why the
//  migration reads them with default (case-sensitive PascalCase) options. Fields the migration
//  does not read are kept deliberately: this file is now the only surviving description of the
//  format, so it documents the whole of it, not the part that happened to survive.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
///     One demo's row in the retired library-wide highlights cache.
///     Superseded by <see cref="DemoCacheRecord" />; read only by <see cref="LegacyCacheMigration" />.
/// </summary>
public sealed class LegacyHighlightsRow
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string FilePath { get; set; } = "";

    public long FileSize { get; set; }

    public long ModifiedTicks { get; set; }

    /// <summary>SHA-256 of the demo bytes: the <c>MatchChecksum</c> handed to CSVG (any stable string).</summary>
    public string? DemoSha256 { get; set; }

    // ── Demo facts (clip assembly without re-parse) ───────────────────────────
    public string? MapName { get; set; }

    public int TickRate { get; set; }

    public int TickCount { get; set; }

    /// <summary>Diagnostic only: events are frame clock; nothing here ever subtracts it.</summary>
    public int ServerStartTick { get; set; }

    public string? ProfileName { get; set; }

    public List<LegacyCachedPlayer> Players { get; set; } = [];

    public List<LegacyCachedRound> Rounds { get; set; } = [];

    // ── Invalidation ──────────────────────────────────────────────────────────
    /// <summary>The A2 combined config fingerprint the row was scanned under.</summary>
    public string? ConfigFingerprint { get; set; }

    /// <summary>Per-highlight-type hashes, keyed <c>{rulesetId}.{highlightId}</c>.</summary>
    public Dictionary<string, string> HighlightHashes { get; set; } = new();

    public LegacyHighlightScanState ScanState { get; set; }

    // ── The harvest ───────────────────────────────────────────────────────────
    public List<LegacyCachedHighlight> Events { get; set; } = [];
}

/// <summary>A demo participant. Superseded by <see cref="CachedPlayerInfo" />.</summary>
public sealed class LegacyCachedPlayer
{
    public int Slot { get; set; }

    /// <summary>RAW name: never sanitized on the way in.</summary>
    public string Name { get; set; } = "";

    public string SteamId64 { get; set; } = "";

    /// <summary>2 = T, 3 = CT (demo convention).</summary>
    public int Team { get; set; }
}

/// <summary>A round boundary in frame clock. Superseded by <see cref="CachedRound" />.</summary>
public sealed class LegacyCachedRound
{
    public int Number { get; set; }

    public int StartTickFrameClock { get; set; }
}

/// <summary>One harvested highlight. Superseded by <see cref="CachedHighlightEvent" />.</summary>
public sealed class LegacyCachedHighlight
{
    public string RulesetId { get; set; } = "";

    public string HighlightId { get; set; } = "";

    public int FrameIndex { get; set; }

    /// <summary>Frame clock: identical semantics to <c>RuleChainEvent.Tick</c>; never converted.</summary>
    public int Tick { get; set; }

    public int PlayerSlot { get; set; }

    /// <summary>
    ///     RAW in-demo name at emission. NOT migrated: the unified record identifies a player by
    ///     <see cref="LegacyCachedHighlight.PlayerSlot" /> into its own roster instead of repeating the name on
    ///     every event. Kept here because it is in the file.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>Per-event steamId join. NOT migrated, for the same reason as <see cref="PlayerName" />.</summary>
    public string SteamId64 { get; set; } = "";

    public int RoundNumber { get; set; }

    public string RenderedTitle { get; set; } = "";

    /// <summary>Derived; was kept out of the persisted schema.</summary>
    [JsonIgnore]
    public string TypeKey => $"{RulesetId}.{HighlightId}";
}

/// <summary>
///     The retired row scan lifecycle. Superseded by <see cref="DemoAnalysisState" />, which carries the
///     same three cases, but whose <c>Pending</c> is no longer PERSISTED: the scan backlog is derived from
///     the rules fingerprint now (see <see cref="DemoCacheRecord.NeedsAnalysis" />).
/// </summary>
public enum LegacyHighlightScanState
{
    /// <summary>Never scanned, or invalidated.</summary>
    Pending,

    /// <summary>Scanned successfully under <see cref="LegacyHighlightsRow.ConfigFingerprint" />.</summary>
    Indexed,

    /// <summary>The scan failed (parse or analysis).</summary>
    Failed
}

/// <summary>The on-disk wrapper of the retired <c>highlights.json</c>.</summary>
public sealed class LegacyHighlightsFile
{
    public int Version { get; set; } = 1;

    public List<LegacyHighlightsRow> Rows { get; set; } = [];
}
