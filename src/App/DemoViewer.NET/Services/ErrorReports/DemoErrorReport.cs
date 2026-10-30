#region

using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.ErrorReports;

/// <summary>
///     One user-written report of what the analysis got wrong on a demo, with the diagnostic snapshot taken
///     when it was last saved. Lives on the demo's cache record (<c>DemoCacheRecord.ErrorReport</c>), so it
///     follows the row's content key when the row re-keys and joins another path of the same bytes, the same
///     way a user's match-time override does.
///     <para>
///         <b>The snapshot is re-taken on every save, not merged.</b> Versions, fingerprints and parse health
///         all describe the run the user is complaining about, and a half-updated snapshot would describe no
///         run at all.
///     </para>
/// </summary>
public sealed class DemoErrorReport
{
    /// <summary>Schema of this payload. A report at a newer schema is left alone rather than rewritten.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Schema the payload was written at.</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>What the user wrote.</summary>
    public string Notes { get; set; } = "";

    /// <summary>When the report was first created, UTC ticks.</summary>
    public long CreatedUtcTicks { get; set; }

    /// <summary>When the report was last saved, UTC ticks.</summary>
    public long UpdatedUtcTicks { get; set; }

    /// <summary>
    ///     When the report was sent to the developers, UTC ticks, or null while it never has been. Nothing sets
    ///     this yet: submission is not wired, and the field exists so a submitted report can be told from an
    ///     edited one without a schema bump.
    /// </summary>
    public long? SubmittedUtcTicks { get; set; }

    /// <summary>The diagnostic snapshot taken at the last save.</summary>
    public DemoErrorReportSnapshot Snapshot { get; set; } = new();

    /// <summary>Members a newer app wrote that this one does not know, kept so a round-trip never drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownMembers { get; set; }
}

/// <summary>
///     The diagnostic snapshot that travels with a report: enough to reproduce what the user saw.
///     <para>
///         <b>Every field here is collected by name.</b> Nothing serialises a settings object or an
///         environment wholesale: environment variables come only from
///         <c>RuntimeEnvInfo.EnvAllowlist</c>, which exists because enumerating all of them
///         leaks Steam tokens and auth. This payload is built to be sent to developers, so that rule is the
///         load-bearing one in this file.
///     </para>
/// </summary>
public sealed class DemoErrorReportSnapshot
{
    /// <summary>When the snapshot was taken, UTC ticks.</summary>
    public long CapturedAtUtcTicks { get; set; }

    /// <summary>The demo's path on the reporting machine. Differs per machine; <see cref="DemoSha256" /> does not.</summary>
    public string DemoPath { get; set; } = "";

    /// <summary>Content hash of the demo's bytes, which is how a developer identifies the same demo.</summary>
    public string? DemoSha256 { get; set; }

    /// <summary>Demo file size in bytes.</summary>
    public long DemoSize { get; set; }

    /// <summary>The demo's own format version, where the header read resolved one.</summary>
    public string? DemoVersion { get; set; }

    /// <summary>Where the demo came from (GOTV, HLTV, …), where the cache resolved it.</summary>
    public string? SourceKind { get; set; }

    /// <summary>Map as the cache holds it (raw, not prettified).</summary>
    public string? Map { get; set; }

    /// <summary>Server name as recorded in the demo.</summary>
    public string? Server { get; set; }

    /// <summary>Tick rate the parse resolved, or 0.</summary>
    public int TickRate { get; set; }

    /// <summary>Demo duration in seconds, or 0.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Rounds the parse resolved, or 0.</summary>
    public int RoundCount { get; set; }

    /// <summary>Which cache tiers this demo has, as "header/parse/analysis" schema numbers.</summary>
    public string? CacheTiers { get; set; }

    /// <summary>The analysis tier's state: whether it ran, is stale, or failed.</summary>
    public string? AnalysisState { get; set; }

    /// <summary>
    ///     Rules profile and config fingerprint the CACHED analysis ran under, and the ones in force NOW. They
    ///     differ whenever the user is looking at a cached render, which is exactly when a stale-rules
    ///     complaint arrives.
    /// </summary>
    public string? CachedProfileName { get; set; }

    /// <inheritdoc cref="CachedProfileName" />
    public string? CachedConfigFingerprint { get; set; }

    /// <inheritdoc cref="CachedProfileName" />
    public string? CurrentProfileName { get; set; }

    /// <inheritdoc cref="CachedProfileName" />
    public string? CurrentConfigFingerprint { get; set; }

    /// <summary>Per-highlight-definition hashes the cached analysis was produced under.</summary>
    public Dictionary<string, string> HighlightHashes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The rule chains in force, one row per ruleset.</summary>
    public List<DemoErrorReportRuleset> Rulesets { get; set; } = [];

    /// <summary>
    ///     Rulesets that v2 composition DROPPED. A ruleset naming a column that does not resolve is excluded
    ///     whole and contributes nothing, which looks identical to rules that ran and matched nothing. This is
    ///     the single most likely cause of an "analysis error" report, so it is collected by name.
    /// </summary>
    public List<string> ExcludedRulesets { get; set; } = [];

    /// <summary>
    ///     True when a composition actually ran in the reporting session, so an empty
    ///     <see cref="ExcludedRulesets" /> means "nothing was dropped". False means nothing composed and the
    ///     list says nothing at all. Without this an empty list reads as a clean bill of health, which is the
    ///     one answer it cannot honestly give.
    /// </summary>
    public bool ExcludedRulesetsKnown { get; set; }

    /// <summary>Parse health as the parser reported it, where the demo is open.</summary>
    public string? ParseHealth { get; set; }

    /// <summary>Parser warnings raised on the open demo.</summary>
    public List<string> ParseWarnings { get; set; } = [];

    /// <summary>Assembly informational versions, keyed by component.</summary>
    public Dictionary<string, string> Versions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Extension packs loaded this launch, "id version".</summary>
    public List<string> Packs { get; set; } = [];

    /// <summary>Runtime and OS rows, as the Diagnostics tab reports them.</summary>
    public Dictionary<string, string> Runtime { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Environment variables, allowlist only. Never the whole environment.</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Members a newer app wrote that this one does not know, kept so a round-trip never drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownMembers { get; set; }
}

/// <summary>One rule chain in force, as a report records it.</summary>
public sealed class DemoErrorReportRuleset
{
    /// <summary>The ruleset's id.</summary>
    public string Id { get; set; } = "";

    /// <summary>Its display name, where it has one.</summary>
    public string? Name { get; set; }

    /// <summary>Whether it ships with the app or came from the user's own rules folder.</summary>
    public string? Origin { get; set; }

    /// <summary>Its content hash: the identity its outputs are stamped under.</summary>
    public string? Hash { get; set; }

    /// <summary>The column-catalogue version the ruleset declares, where it declares one.</summary>
    public string? CatalogVersion { get; set; }

    /// <summary>Whether it was enabled for the run.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     True when another doc with the same id won, so this one contributed nothing. A same-id override is
    ///     the supported way to replace a shipped ruleset, and the loser carries no identity of its own.
    /// </summary>
    public bool Shadowed { get; set; }

    /// <summary>Members a newer app wrote that this one does not know, kept so a round-trip never drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownMembers { get; set; }
}
