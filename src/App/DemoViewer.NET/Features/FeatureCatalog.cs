#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     The single, code-defined source of truth for the set of gatable features and their per-category
///     default visibility. <see cref="IFeatureGate" /> resolves a live on/off decision from these
///     descriptors plus the user's category and explicit overrides; nothing else defines a feature.
///     <para>
///         The default matrix below encodes the category-visibility matrix from
///         docs/ui/design-system.md: the consumer surface is the viewing tabs (Library + Stats +
///         2D Playback), and the skip-wizard fallback category is Power-User. Every gated feature
///         stays user-toggleable via <c>AppSettings.Features.Overrides</c> regardless of category —
///         this matrix only sets DEFAULTS.
///     </para>
/// </summary>
public static class FeatureCatalog
{
    // --- Stable feature ids (never renamed once shipped — they are persisted override keys) ---

    /// <summary>Group whose members toggle atomically: parser hex pane + parse-chain surfaces.</summary>
    public const string GroupParserDeepDive = "parserDeepDive";

    /// <summary>Group whose members toggle atomically: rule-graph / debugger developer chrome.</summary>
    public const string GroupGraphDebug = "graphDebug";

    // The catalog order is load-bearing: a group's LEADER is its FIRST member in All (see GroupLeader).
    // parser.hex precedes parser.parseChain + chrome.parseChain → parserDeepDive leader = parser.hex.
    // analysis.breakpoints precedes chrome.debugger + chrome.breakpointNav → graphDebug leader =
    // analysis.breakpoints. Do not reorder without re-checking the leader-lock test.
    private static readonly FeatureDescriptor[] _catalog =
    [
        // ---------------- TABS ----------------
        new(
            "tab.library", FeatureScope.Tab, "Library",
            "Demo library landing tab — browse and open demos. Always available.",
            null, null, true, Defaults(true, true, true)),
        new(
            "tab.matchoverview", FeatureScope.Tab, "Match Overview",
            "Demo landing page — shows the header, load progress and a match summary. Core viewing surface.",
            null, null, false, Defaults(true, true, true)),
        new(
            "tab.playback2d", FeatureScope.Tab, "2D Playback",
            "Top-down 2D match playback. Core viewing surface.",
            null, null, false, Defaults(true, true, true)),
        new(
            "tab.stats", FeatureScope.Tab, "Stats",
            "Player-facing scoreboard and per-round stats. Core viewing surface.",
            null, null, false, Defaults(true, true, true)),
        // The Reels dashboard. Was a library-wide highlights
        // BROWSER; it is an authoring surface now — per-game exploration moved to Match Overview. Still
        // default-visible to every category: gating reel generation to power-users would hide the feature's
        // headline payoff from the audience most excited by it.
        //
        // Warning: the id "tab.highlights" is a PERSISTED KEY (settings write Features:Overrides:{id}) and must not
        // change with the rename — doing so would silently reset every user's override for this tab. The label
        // and description are display-only and are free to be reworded.
        new(
            "tab.highlights", FeatureScope.Tab, "Reels",
            "Build and customise highlight reels — stage clips from any match and render them to video. "
            + "Explore a match's own highlights on Match Overview.",
            null, null, false, Defaults(true, true, true)),
        new(
            "tab.parser", FeatureScope.Tab, "Parser",
            "Wire-format message inspector. Needs a wire-format mental model → power-user+.",
            null, null, false, Defaults(false, true, true)),
        new(
            "tab.entity", FeatureScope.Tab, "Entity Tracking",
            "Entity-state replay inspector. Needs entity-layer knowledge → power-user+.",
            null, null, false, Defaults(false, true, true)),
        new(
            "tab.analysis", FeatureScope.Tab, "Analysis Engine",
            "Rule-graph analysis inspector. Needs rule-engine knowledge → power-user+.",
            null, null, false, Defaults(false, true, true)),
        new(
            "tab.authoring", FeatureScope.Tab, "Authoring",
            "Rule-authoring workbench. Rule editing → power-user+.",
            null, null, false, Defaults(false, true, true)),
        new(
            "tab.diagnostics", FeatureScope.Tab, "Diagnostics",
            "Developer diagnostics tab. Developer-only.",
            null, null, false, Defaults(false, false, true)),

        // ---------------- SUB-FEATURES (ParentId = owning tab; cascade off when the tab is off) ----------------
        new(
            "parser.frames", FeatureScope.SubFeature, "Frame list",
            "The parser frame/message list. On whenever the Parser tab is on.",
            "tab.parser", null, true, Defaults(true, true, true)),
        new(
            "parser.cards", FeatureScope.SubFeature, "Message cards",
            "Decoded per-message cards. On whenever the Parser tab is on.",
            "tab.parser", null, false, Defaults(true, true, true)),
        // parser.hex is the parserDeepDive LEADER (first group member in catalog order).
        new(
            "parser.hex", FeatureScope.SubFeature, "Hex pane",
            "Raw-bytes hex view. Expensive to populate → developer default; power-users can enable it.",
            "tab.parser", GroupParserDeepDive, false,
            Defaults(false, false, true)),
        new(
            "parser.parseChain", FeatureScope.SubFeature, "Parse chain",
            "Source-link parse-chain strip for the selected message.",
            "tab.parser", GroupParserDeepDive, false,
            Defaults(false, false, true)),
        new(
            "entity.core", FeatureScope.SubFeature, "Entity fields",
            "The core entity field/state view. On whenever the Entity Tracking tab is on.",
            "tab.entity", null, false, Defaults(true, true, true)),
        new(
            "entity.schema", FeatureScope.SubFeature, "Schema lens",
            "Entity schema-lens inspector. Developer default.",
            "tab.entity", null, false, Defaults(false, false, true)),
        // The Reels config pane's ENCODING section. CRF, bitrate,
        // FPS and container are OBS-encoder knobs a consumer cannot reason about and would never intentionally
        // touch — the textbook "hidden but enableable" tier, so consumer:false / power:true / dev:true. It is
        // hidden, NOT removed: every category can switch it on in Settings, and the consumer-facing path to a
        // finished reel (tray → Default/No-HUD → folder + name → Generate) never routes through it.
        //
        // Careful: consumed via ReelConfig.IsEncodingVisible, re-resolved on IFeatureGate.Changed — NOT a one-shot
        // read, or toggling it in Settings would leave the section wrong until the tab was rebuilt. No GroupId:
        // it must not disturb the parserDeepDive / graphDebug leader-lock ordering.
        new(
            "highlights.encoding", FeatureScope.SubFeature, "Reel encoding options",
            "Video encoder settings for a highlight reel — CRF / bitrate, frame rate and container. "
            + "Power-user+ default; consumers get sensible defaults without the knobs.",
            "tab.highlights", null, false, Defaults(false, true, true)),
        new(
            "analysis.core", FeatureScope.SubFeature, "Rule graph",
            "The core rule-graph view. On whenever the Analysis Engine tab is on.",
            "tab.analysis", null, false, Defaults(true, true, true)),
        // analysis.breakpoints is the graphDebug LEADER (first group member in catalog order).
        new(
            "analysis.breakpoints", FeatureScope.SubFeature, "Graph breakpoints",
            "Rule-graph conditional breakpoints. Developer-only.",
            "tab.analysis", GroupGraphDebug, false,
            Defaults(false, false, true)),

        // ---------------- 2D PLAYBACK v2 SUB-FEATURES ----------------
        // One contiguous block so the rows read as one group in Settings. Every entry keeps GroupId = null,
        // so the parserDeepDive / graphDebug leader-lock ordering above is untouched. Later v2 phases insert
        // their own rows HERE (final order: annotations · timeline · levels.auto · follow · export) — the ids
        // are persisted override keys and must never be renamed.
        new(
            "playback2d.annotations", FeatureScope.SubFeature, "Annotations",
            "Draw and erase over the 2D playback surface; static or clock-anchored.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.timeline", FeatureScope.SubFeature, "Playback timeline",
            "Scrubbable round / kill / bomb timeline under the 2D playback view.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.follow", FeatureScope.SubFeature, "Follow player",
            "Select a player card to follow them in the 2D camera (and in CS2 while Live Sync is active).",
            "tab.playback2d", null, false, Defaults(true, true, true)),

        // ---------------- CHROME (global; no ParentId → never cascaded) ----------------
        new(
            "chrome.debugger", FeatureScope.Chrome, "Debugger rail",
            "Frame/tick/event breakpoint management rail. Developer chrome.",
            null, GroupGraphDebug, false, Defaults(false, false, true)),
        new(
            "chrome.output", FeatureScope.Chrome, "Output panel",
            "Unknown-message / decode-error output drawer. Power-user+.",
            null, null, false, Defaults(false, true, true)),
        new(
            "chrome.parseChain", FeatureScope.Chrome, "Parse-chain toolbar",
            "Toolbar Parse-Chain toggle. Parser deep-dive chrome.",
            null, GroupParserDeepDive, false, Defaults(false, false, true)),
        new(
            "chrome.breakpointNav", FeatureScope.Chrome, "Breakpoint nav",
            "NavStrip TO-BREAKPOINT continue/step cluster. Developer chrome.",
            null, GroupGraphDebug, false, Defaults(false, false, true)),
        // chrome.livesync governs the Live Sync (CS2) status chip + flyout and the NavStrip speed-lock
        // affordance. No GroupId → appended last so it does not
        // disturb the parserDeepDive / graphDebug leader-lock ordering. Developer default; the shell shim
        // also ANDs !OperatingSystem.IsBrowser() so a WASM build never surfaces it. Only the
        // ID is a persisted key; the description is display-only help text.
        new(
            "chrome.livesync", FeatureScope.Chrome, "Live Sync (CS2)",
            "Two-way playback sync with a live CS2 game via CSVG. Launches a full CS2 instance (~2 min) and " +
            "temporarily modifies your CS2 install. Developer default; enable in Settings to use it.",
            null, null, false, Defaults(false, false, true)),
        // chrome.processingQueue governs the status-strip demo-processing chip + flyout
        //: the live surface for the global background parse/analyse queue (pause/resume, per-item remove,
        // status). EVERY category sees it (consumer:true / power:true / dev:true) so all users stay aware of
        // background work happening on their behalf — the chip only appears WHEN the queue is active, so an idle
        // queue still adds no clutter for anyone. No GroupId → appended last so it does not disturb the
        // parserDeepDive / graphDebug leader-lock ordering. The shell shim also ANDs !OperatingSystem.IsBrowser()
        // (background work needs a filesystem — none on the WASM head). Only the ID is a persisted key; the
        // description is display-only help text.
        new(
            "chrome.processingQueue", FeatureScope.Chrome, "Processing queue",
            "See and manage the global background demo-processing queue — what is being parsed, plus " +
            "pause/resume and remove queued demos. Visible to all users; opening a demo never requires it.",
            null, null, false, Defaults(true, true, true))
    ];

    private static readonly Dictionary<string, FeatureDescriptor> _byId =
        _catalog.ToDictionary(d => d.Id, StringComparer.Ordinal);

    /// <summary>Every gate descriptor, in a stable order (which also fixes each group's leader).</summary>
    public static IReadOnlyList<FeatureDescriptor> All => _catalog;

    /// <summary>The group ids this catalog defines.</summary>
    public static IReadOnlyList<string> GroupIds { get; } = [GroupParserDeepDive, GroupGraphDebug];

    /// <summary>Looks up a descriptor by its stable id, or <c>null</c> if the id is not in the catalog.</summary>
    public static FeatureDescriptor? ById(string id) =>
        id is not null && _byId.TryGetValue(id, out FeatureDescriptor? d) ? d : null;

    /// <summary>The sub-features owned by <paramref name="tabId" /> (its cascade children), in catalog order.</summary>
    public static IEnumerable<FeatureDescriptor> Children(string tabId) =>
        _catalog.Where(d => d.ParentId == tabId);

    /// <summary>The members of <paramref name="groupId" />, in catalog order (first = the leader).</summary>
    public static IEnumerable<FeatureDescriptor> GroupMembers(string groupId) =>
        _catalog.Where(d => d.GroupId == groupId);

    /// <summary>
    ///     The deterministic leader of <paramref name="groupId" /> — its FIRST member in <see cref="All" />
    ///     order — whose resolved own-state every member of the group adopts. <c>null</c> for an unknown group.
    /// </summary>
    public static FeatureDescriptor? GroupLeader(string groupId) =>
        _catalog.FirstOrDefault(d => d.GroupId == groupId);

    // Builds a category→default map without a constant-array argument (CA1861-clean) and reads left-to-right.
    // Concrete return type per CA1859; the descriptor's IReadOnlyDictionary param accepts it directly.
    private static Dictionary<UserCategory, bool> Defaults(bool consumer, bool power, bool dev) =>
        new()
        {
            [UserCategory.Consumer] = consumer,
            [UserCategory.PowerUser] = power,
            [UserCategory.Developer] = dev
        };
}
