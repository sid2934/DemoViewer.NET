#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     The single source of truth for the set of gatable features and their per-category default
///     visibility: the core descriptors below plus every <see cref="IFeaturePack.Features" />, composed once
///     by <see cref="Compose" /> (or from <see cref="FeaturePacks.Compatible" /> on first use) and immutable
///     after. <see cref="IFeatureGate" /> resolves a live on/off decision from these descriptors plus the
///     user's category and explicit overrides; nothing else defines a feature.
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

    /// <summary>Strat tokens follow the map's nav round walls; off moves them in straight lines.</summary>
    public const string StratRoutingFeatureId = "stratbook.routing";

    /// <summary>The prefix every pack umbrella id carries. The gate never fails open on it.</summary>
    public const string PackIdPrefix = "pack.";

    // The catalog order is load-bearing: a group's LEADER is its FIRST member in All (see GroupLeader).
    // parser.hex precedes parser.parseChain + chrome.parseChain → parserDeepDive leader = parser.hex.
    // analysis.breakpoints precedes chrome.debugger + chrome.breakpointNav → graphDebug leader =
    // analysis.breakpoints. Do not reorder without re-checking the leader-lock test. Pack descriptors are
    // appended after this array, so a pack row can never become a leader of a core group.
    private static readonly FeatureDescriptor[] _core =
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
        // their own rows HERE (final order: annotations · timeline · levels.auto · follow · export; the Strat Book pack
        // appends tagger · suggestedtags): the ids
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

    private static readonly Lock _composeLock = new();
    private static volatile FeatureDescriptor[]? _all;
    private static volatile Dictionary<string, FeatureDescriptor>? _byId;

    /// <summary>
    ///     Composes the catalog from the core descriptors plus <paramref name="packs" />' descriptors, in
    ///     that order. The first call fixes the catalog; a later call with the same ids is a no-op and one
    ///     with a different set throws, so the catalog never changes under a live gate.
    /// </summary>
    public static void Compose(IEnumerable<IFeaturePack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        lock (_composeLock)
        {
            FeatureDescriptor[] composed = Build(packs);
            if (_all is null)
            {
                _byId = composed.ToDictionary(d => d.Id, StringComparer.Ordinal);
                _all = composed;
                return;
            }

            if (!_all.Select(d => d.Id).SequenceEqual(composed.Select(d => d.Id), StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "FeatureCatalog is already composed with a different set of feature ids; it is composed once per process.");
            }
        }
    }

    // Validates the composed set: unique ids, a parent that exists, the parent rule per scope (a pack may
    // parent a tab; a tab parents a sub-feature; chrome and packs have none), nothing under a pack is
    // Required (Required would defeat the pack switch), no pack row in a core group (it could become the
    // leader), and each pack's FeatureId names exactly one Pack-scope row of its own.
    internal static FeatureDescriptor[] Build(IEnumerable<IFeaturePack> packs)
    {
        List<FeatureDescriptor> fromPacks = [];
        foreach (IFeaturePack pack in packs)
        {
            FeatureDescriptor[] features = [.. pack.Features];
            int umbrellas = features.Count(f => f.Id == pack.FeatureId && f.Scope == FeatureScope.Pack);
            if (umbrellas != 1)
            {
                throw new InvalidOperationException(
                    $"Pack '{pack.Id}' must declare exactly one Pack-scope descriptor with id '{pack.FeatureId}'; found {umbrellas}.");
            }

            foreach (FeatureDescriptor f in features)
            {
                if (f.GroupId is { } groupId && GroupIds.Contains(groupId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Pack feature '{f.Id}' may not join core group '{groupId}'.");
                }

                // Every contribution carries its pack's id implicitly (7.2), independent of ParentId: the
                // gate cascades it off with the pack even when it is parented to a core tab. The pack's own
                // Pack-scope row is not owned by itself.
                fromPacks.Add(f.Scope == FeatureScope.Pack ? f : f with { OwnerPackId = pack.FeatureId });
            }
        }

        FeatureDescriptor[] all = [.. _core, .. fromPacks];
        Dictionary<string, FeatureDescriptor> byId = new(StringComparer.Ordinal);
        foreach (FeatureDescriptor d in all)
        {
            if (!byId.TryAdd(d.Id, d))
            {
                throw new InvalidOperationException($"Duplicate feature id '{d.Id}' in the composed catalog.");
            }
        }

        foreach (FeatureDescriptor d in all)
        {
            if (d.Scope == FeatureScope.Pack)
            {
                if (!IsPackId(d.Id))
                {
                    throw new InvalidOperationException($"Pack feature '{d.Id}' must start with '{PackIdPrefix}'.");
                }

                if (d.Required)
                {
                    throw new InvalidOperationException($"Pack feature '{d.Id}' may not be Required.");
                }
            }

            if (d.ParentId is null)
            {
                continue;
            }

            if (!byId.TryGetValue(d.ParentId, out FeatureDescriptor? parent))
            {
                throw new InvalidOperationException($"Feature '{d.Id}' names an unknown parent '{d.ParentId}'.");
            }

            bool allowed = d.Scope switch
            {
                FeatureScope.SubFeature => parent.Scope == FeatureScope.Tab,
                FeatureScope.Tab => parent.Scope == FeatureScope.Pack,
                _ => false
            };
            if (!allowed)
            {
                throw new InvalidOperationException(
                    $"Feature '{d.Id}' ({d.Scope}) may not have '{d.ParentId}' ({parent.Scope}) as its parent.");
            }
        }

        // Nothing a pack owns may be Required, not just a tab parented to it directly: a sub-feature
        // docked in a core tab (playback2d.tagger) is just as pack-owned via OwnerPackId, and Required
        // would defeat that pack's switch the same way.
        foreach (FeatureDescriptor d in all)
        {
            if (d.OwnerPackId is not null && d.Required)
            {
                throw new InvalidOperationException($"Feature '{d.Id}' owned by pack '{d.OwnerPackId}' may not be Required.");
            }
        }

        return all;
    }

    // The composed catalog, composing from the compatible pack list when nothing composed it first.
    private static FeatureDescriptor[] Composed
    {
        get
        {
            if (_all is { } all)
            {
                return all;
            }

            Compose(FeaturePacks.Compatible);
            return _all!;
        }
    }

    private static Dictionary<string, FeatureDescriptor> Index
    {
        get
        {
            _ = Composed;
            return _byId!;
        }
    }

    /// <summary>Every gate descriptor, in a stable order (which also fixes each group's leader).</summary>
    public static IReadOnlyList<FeatureDescriptor> All => Composed;

    /// <summary>The group ids this catalog defines.</summary>
    public static IReadOnlyList<string> GroupIds { get; } = [GroupParserDeepDive, GroupGraphDebug];

    /// <summary>Looks up a descriptor by its stable id, or <c>null</c> if the id is not in the catalog.</summary>
    public static FeatureDescriptor? ById(string id) =>
        id is not null && Index.TryGetValue(id, out FeatureDescriptor? d) ? d : null;

    /// <summary>True for an id that names a pack, known or not: such an id never fails open.</summary>
    public static bool IsPackId(string? id) =>
        id is not null && id.StartsWith(PackIdPrefix, StringComparison.Ordinal);

    /// <summary>The features whose parent is <paramref name="parentId" /> (its cascade children), in catalog order.</summary>
    public static IEnumerable<FeatureDescriptor> Children(string parentId) =>
        Composed.Where(d => d.ParentId == parentId);

    /// <summary>The members of <paramref name="groupId" />, in catalog order (first = the leader).</summary>
    public static IEnumerable<FeatureDescriptor> GroupMembers(string groupId) =>
        Composed.Where(d => d.GroupId == groupId);

    /// <summary>
    ///     The deterministic leader of <paramref name="groupId" />, its FIRST member in <see cref="All" />
    ///     order, whose resolved own-state every member of the group adopts. <c>null</c> for an unknown group.
    /// </summary>
    public static FeatureDescriptor? GroupLeader(string groupId) =>
        Composed.FirstOrDefault(d => d.GroupId == groupId);

    // Builds a category→default map without a constant-array argument (CA1861-clean) and reads left-to-right.
    // Concrete return type per CA1859; the descriptor's IReadOnlyDictionary param accepts it directly.
    internal static Dictionary<UserCategory, bool> Defaults(bool consumer, bool power, bool dev) =>
        new()
        {
            [UserCategory.Consumer] = consumer,
            [UserCategory.PowerUser] = power,
            [UserCategory.Developer] = dev
        };
}
