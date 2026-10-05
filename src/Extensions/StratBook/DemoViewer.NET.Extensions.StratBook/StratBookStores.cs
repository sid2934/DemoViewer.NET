#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Every store and cache path the pack owns outside its own folders, read by both
///     <see cref="StratBookPack.Contribute" /> (the loop that reports them) and the pack test's writer-coverage
///     check. The user's own work stays where users have it. The cache entries marked "old layout" are where
///     older builds kept what the extension's per-demo data holds now; nothing writes them, and "delete
///     extension data" still removes them. <c>review-queue.json</c> is not here: the Review Queue is core, since
///     Reels uses it.
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
        new("strat-mining-cache", "Strat Mining detections", StoreRoot.Cache, ["strat-mining"], IsUserWork: false),
        new("team-index", "Team index", StoreRoot.Cache, ["team-index.json"], IsUserWork: false),

        // ── Cache root: the old layout, read once or not at all ─────────────────
        new("round-index", "Round Index (old layout)", StoreRoot.Cache, ["round-index"], IsUserWork: false),
        new("suggestions", "Suggested Tags proposals (old layout)", StoreRoot.Cache, ["suggestions"], IsUserWork: false),
        new("grenade-lineups", "Grenade lineups (old layout)", StoreRoot.Cache, ["grenade-lineups.json.gz"], IsUserWork: false),
        new("grenades-v3-attempts", "Grenade migration attempts (old layout)", StoreRoot.Cache, ["grenades-v3.attempts.json"],
            IsUserWork: false),
        new("grenade-sidecars", "Grenade sidecars (old layout)", StoreRoot.Cache,
        [
            "demos/*.grenades.json.gz", "demos/*.grenades.paths.json.gz", "demos/*.grenades.log.gz",
            "demos/*.grenades.json", "demos/*.grenades.paths.json"
        ], IsUserWork: false)
    ];

    /// <summary>
    ///     After "delete extension data": the stores that stay loaded while the pack is off re-read what is
    ///     left, so switching back on in the same session shows nothing that was deleted and the next edit
    ///     does not save it back.
    /// </summary>
    /// <param name="sp">The composition root.</param>
    public static void ReloadLiveStores(IServiceProvider sp)
    {
        sp.GetService<StratStore>()?.RebuildIndexFromDisk();
        sp.GetService<TagStore>()?.RebuildIndexFromDisk();
        sp.GetService<TagPaletteStore>()?.Reload();
        sp.GetService<DossierNotesStore>()?.Reload();
        sp.GetService<VetoHistoryStore>()?.Reload();
        sp.GetService<WatchedSituationsService>()?.Reload();
        sp.GetService<StratMiningService>()?.ResetState();
        sp.GetService<ProfileStore>()?.Reload();
    }
}
