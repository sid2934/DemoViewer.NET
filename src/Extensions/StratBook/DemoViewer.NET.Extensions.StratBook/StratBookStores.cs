#region

using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Every store and cache path the pack owns (item 24), read by both <see cref="StratBookPack.Contribute" />
///     (the loop that reports them) and the pack test's writer-coverage check. Verified against the writer,
///     not the architecture doc's §3.6 inventory, which predates two corrections kept here as the record
///     of what changed and why:
///     <list type="bullet">
///         <item>
///             <c>grenade-lineups.json.gz</c> and <c>grenades-v3.attempts.json</c> are under the CACHE root:
///             <c>GrenadeLineupStore</c> and <c>GrenadeStoreMigration</c> both combine with
///             <c>DemoCacheStore.CacheRoot</c>, never <c>AppPaths.ConfigRoot</c>.
///         </item>
///         <item>
///             <c>review-queue.json</c> is dropped. <c>Services/Review/ReviewQueue.cs</c> is core (decision
///             10.1: "the Review Queue stays core, since Reels uses it"), registered by the composition
///             root and live with the pack off; deleting it would take Reels' own queue with it, and its
///             next save would write it back anyway.
///         </item>
///     </list>
/// </summary>
internal static class StratBookStores
{
    public static readonly StoreDescriptor[] All =
    [
        // ── Config root: user truth ─────────────────────────────────────────────
        new("strats", "Strats", StoreRoot.Config, ["strats"], IsUserWork: true),
        new("tags", "Tags", StoreRoot.Config, ["tags"], IsUserWork: true),
        new("palettes", "Tag palettes", StoreRoot.Config, ["palettes"], IsUserWork: true),
        new("suggested-tags", "Suggested Tags tuning", StoreRoot.Config, ["suggested-tags"], IsUserWork: true),
        new("lineup-clips", "Lineup clips", StoreRoot.Config, ["lineup-clips"], IsUserWork: true),
        new("teams", "Teams", StoreRoot.Config, ["teams.json"], IsUserWork: true),
        new("watched-situations", "Watched Situations", StoreRoot.Config, ["watched-situations.json"], IsUserWork: true),
        new("veto-history", "Veto history", StoreRoot.Config, ["veto-history.json"], IsUserWork: true),
        new("dossier-notes", "Dossier notes", StoreRoot.Config, ["dossier-notes.json"], IsUserWork: true),
        new("strat-mining-state", "Strat Mining (dismissed and promoted)", StoreRoot.Config, ["strat-mining.json"], IsUserWork: true),

        // ── Cache root: regenerable ──────────────────────────────────────────────
        new("round-index", "Round Index", StoreRoot.Cache, ["round-index"], IsUserWork: false),
        new("suggestions", "Suggested Tags proposals", StoreRoot.Cache, ["suggestions"], IsUserWork: false),
        new("strat-mining-cache", "Strat Mining detections", StoreRoot.Cache, ["strat-mining"], IsUserWork: false),
        new("team-index", "Team index", StoreRoot.Cache, ["team-index.json"], IsUserWork: false),
        new("grenade-lineups", "Grenade lineups", StoreRoot.Cache, ["grenade-lineups.json.gz"], IsUserWork: false),
        new("grenades-v3-attempts", "Grenade migration attempts", StoreRoot.Cache, ["grenades-v3.attempts.json"], IsUserWork: false),
        new("grenade-sidecars", "Grenade sidecars", StoreRoot.Cache,
        [
            "demos/*.grenades.json.gz", "demos/*.grenades.paths.json.gz", "demos/*.grenades.log.gz",
            "demos/*.grenades.json", "demos/*.grenades.paths.json"
        ], IsUserWork: false)
    ];
}
