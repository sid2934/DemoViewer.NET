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
///         stays user-toggleable via <c>AppSettings.Features.Overrides</c> regardless of category:
///         this matrix only sets DEFAULTS.
///     </para>
/// </summary>
public static class FeatureCatalog
{
    // --- Stable feature ids (never renamed once shipped: they are persisted override keys) ---

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
        // BROWSER; it is an authoring surface now. Per-game exploration moved to Match Overview. Still
        // default-visible to every category: gating reel generation to power-users would hide the feature's
        // headline payoff from the audience most excited by it.
        //
        // Warning: the id "tab.highlights" is a PERSISTED KEY (settings write Features:Overrides:{id}) and must not
        // change with the rename. Doing so would silently reset every user's override for this tab. The label
        // and description are display-only and are free to be reworded.
        new(
            "tab.highlights", FeatureScope.Tab, "Reels",
            "Build and customise highlight reels — stage clips from any match and render them to video. "
            + "Explore a match's own highlights on Match Overview.",
            null, null, false, Defaults(true, true, true)),
        // The Situations tab: Situation Search over the round index. Default-visible to every category
        // like Reels, for the same reason: the flagship's payoff must not hide from the audience that
        // wants it. Only the id is a persisted key; the label is display text.
        new(
            "tab.situations", FeatureScope.Tab, "Situations",
            "Find rounds by where the players stood — search the library's round index for a setup, "
            + "an execute or a retake and walk the hits.",
            null, null, false, Defaults(true, true, true)),
        // The Teams tab: who played in which demo, which team is us, the opponent per demo. Default-visible
        // like Situations: the Library's team filter and every "our / their" surface read what is decided
        // here. Only the id is a persisted key; the label is display text.
        new(
            "tab.teams", FeatureScope.Tab, "Teams",
            "The teams found across your demos: name them, say which one is you, merge or split rosters, "
            + "and confirm your own accounts so every demo knows which side is ours.",
            null, null, false, Defaults(true, true, true)),
        // The Review tab: the Review Queue every surface sends clips to, the Reels tray's included.
        // Default-visible like Teams: the Reels tray stages into it whether or not it shows. Only the id
        // is a persisted key; the label is display text.
        new(
            "tab.review", FeatureScope.Tab, "Review",
            "One queue of clips from any demo: staged highlights, situation search results and picks at the "
            + "playhead, in sections with a question per clip.",
            null, null, false, Defaults(true, true, true)),
        // The Round Tagger's Matrix tab: codes by labels across the library's tags. Default-visible like
        // Situations and Teams. The module ships ahead of the tab, so until The Matrix lands this row
        // gates nothing it can show. Only the id is a persisted key; the label is display text.
        new(
            "tab.tagger", FeatureScope.Tab, "Round Tagger",
            "Tag stretches of a round with your own codes and labels, then pivot them across every demo "
            + "in the Matrix.",
            null, null, false, Defaults(true, true, true)),
        // The Strat Book tab: strats per book (a team or you) on the round clock, with slots, steps and
        // branches. Default-visible like the Matrix, and on both hosts: the browser keeps strats for the
        // session and says so. Only the id is a persisted key; the label is display text.
        new(
            "tab.stratbook", FeatureScope.Tab, "Strat Book",
            "Write your team's strats on the round clock: five slots, the steps each one takes, and the "
            + "branches when the plan changes.",
            null, null, false, Defaults(true, true, true)),
        // The Utility Book tab: the Grenade Index, every indexed grenade clustered by where it landed.
        // Default-visible like the Strat Book, and on both hosts: the browser indexes the open demo for the
        // session and says so. Only the id is a persisted key; the label is display text.
        new(
            "tab.utilitybook", FeatureScope.Tab, "Utility Book",
            "Every grenade in your indexed demos, grouped by where it landed: pick a map, a grenade and a "
            + "landing place to see every position it was thrown from.",
            null, null, false, Defaults(true, true, true)),
        // The Opponent Dossier tab: the Map Pool Record and, later, the rest of the Dossier sections,
        // keyed by a Team Identity team. Default-visible like the Strat Book and the Utility Book, and
        // on both hosts: the browser keeps teams for the session and the Teams tab already says so. Only
        // the id is a persisted key; the label is display text.
        new(
            "tab.dossier", FeatureScope.Tab, "Dossier",
            "A scouting page per team: maps played, win rate, side wins and the decider record where "
            + "it is inferable, plus a veto history you enter by hand.",
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
        // touch: the textbook "hidden but enableable" tier, so consumer:false / power:true / dev:true. It is
        // hidden, NOT removed: every category can switch it on in Settings, and the consumer-facing path to a
        // finished reel (tray → Default/No-HUD → folder + name → Generate) never routes through it.
        //
        // Careful: consumed via ReelConfig.IsEncodingVisible, re-resolved on IFeatureGate.Changed, NOT a one-shot
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
        // their own rows HERE (final order: annotations · timeline · levels.auto · follow · export · tagger · suggestedtags): the ids
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
            "playback2d.levels.auto", FeatureScope.SubFeature, "Auto level switching",
            "On a multi-floor map, show the floor the followed player is on (with hysteresis). " +
            "Manual floor picking and the level strip stay available with this off.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        new(
            "playback2d.follow", FeatureScope.SubFeature, "Follow player",
            "Select a player card to follow them in the 2D camera (and in CS2 while Live Sync is active).",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        // Desktop only: an export writes a file and drives an ffmpeg subprocess, and the WASM head has
        // neither. That AND lives in exactly ONE place, ShellModuleFeatureGate.DesktopOnlyIds, which is
        // the same treatment chrome.livesync gets. Only the ID is a persisted key.
        new(
            "playback2d.export", FeatureScope.SubFeature, "Video export",
            "Render the 2D playback to webm/mp4/gif. Desktop only.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        // The Round Tagger's palette docked in the 2D tab (tag-store.md §3.11). Works on both hosts: the
        // browser keeps tags for the session and the palette says so.
        new(
            "playback2d.tagger", FeatureScope.SubFeature, "Tag palette",
            "Tag the round you are watching with a hotkey palette; tags are saved per demo.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        // Suggested Tags (suggested-tags.md §3.6): the Suggested track, the proposal queue and the
        // evaluator. On for both hosts; the browser keeps proposals and verdicts for the session.
        new(
            "playback2d.suggestedtags", FeatureScope.SubFeature, "Suggested tags",
            "Offer tags found by detectors (execute, default, fake, opener, retake) to accept, edit or reject.",
            "tab.playback2d", null, false, Defaults(true, true, true)),
        // Strat Export (step-authoring.md §3.6): the open strat to GIF or video with no demo behind it. Desktop
        // only for playback2d.export's reason, through the same ShellModuleFeatureGate.DesktopOnlyIds. Only
        // the ID is a persisted key.
        new(
            "stratbook.export", FeatureScope.SubFeature, "Strat export",
            "Render a strat to gif/webm/mp4 from the Strat Book canvas. Desktop only.",
            "tab.stratbook", null, false, Defaults(true, true, true)),

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
        // background work happening on their behalf: the chip only appears WHEN the queue is active, so an idle
        // queue still adds no clutter for anyone. No GroupId → appended last so it does not disturb the
        // parserDeepDive / graphDebug leader-lock ordering. The shell shim also ANDs !OperatingSystem.IsBrowser()
        // (background work needs a filesystem: none on the WASM head). Only the ID is a persisted key; the
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
    ///     The deterministic leader of <paramref name="groupId" />, its FIRST member in <see cref="All" />
    ///     order, whose resolved own-state every member of the group adopts. <c>null</c> for an unknown group.
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
