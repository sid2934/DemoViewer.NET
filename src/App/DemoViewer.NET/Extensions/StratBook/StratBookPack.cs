#region

using System.Globalization;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Dossier;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Modules.RoundTagger;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.Teams;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.Export.Pack;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.ViewModels.RoundTagger;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.ViewModels.SuggestedTags;
using DemoViewer.NET.ViewModels.Teams;
using DemoViewer.NET.ViewModels.UtilityBook;
using DemoViewer.NET.Views.StratBook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The Strat Book extension as a feature pack. Round Index, Teams, Provenance, the Tag Store and the
///     Review Queue are registered by the composition root, not here: core surfaces read them. Round Facts
///     is registered here (decision 1): its ruleset rides the merged build only while the pack is on.
/// </summary>
public sealed class StratBookPack : IFeaturePack
{
    /// <summary>The umbrella gate id. A persisted override key.</summary>
    public const string PackFeatureId = "pack.stratbook";

    /// <inheritdoc />
    public string Id => "net.demoviewer.pack.stratbook";

    /// <inheritdoc />
    public string FeatureId => PackFeatureId;

    /// <inheritdoc />
    public IEnumerable<FeatureDescriptor> Features => _features;

    /// <inheritdoc />
    public IEnumerable<CommandDescriptor> Commands => StratBookCommands.All;

    /// <inheritdoc />
    public IEnumerable<JobKindDescriptor> JobKinds => StratBookJobKinds.All;

    /// <summary>
    ///     The Strat Book hub as a host tab: one strip tab whose rail lists every section that names
    ///     <see cref="StratBookHubViewModel.HostId" />. Order 4 sits after 2D Playback and before Authoring.
    ///     Public so a shell test can host sections on the real hub without the rest of the pack.
    /// </summary>
    /// <param name="layout">Resolves the collapsed panes the hub shares with the Strats section, when the shell builds the hub.</param>
    public static HostTabContribution HubHostTab(Func<StratBookLayout?> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new HostTabContribution(
            StratBookHubViewModel.HostId, StratBookHubViewModel.TabId, "Strat Book", 4, "STRAT BOOK",
            () => new StratBookHubViewModel(layout()), () => new StratBookHubView(), PackFeatureId);
    }

    // Every id is a persisted override key and must never be renamed; labels and descriptions are display
    // text. Tabs are parented to the pack; sub-features keep their tab parent, so the two docked in 2D
    // Playback cascade from tab.playback2d, not from the pack, until they are reparented.
    private static readonly FeatureDescriptor[] _features =
    [
        new(
            PackFeatureId, FeatureScope.Pack, "Strat Book extension",
            "Strats, situations, tags, utility, review, dossier and teams: the whole Strat Book, its "
            + "background indexing included. Off hides every section and stops its work.",
            null, null, false, FeatureCatalog.Defaults(true, true, true)),

        // ---------------- TABS (ParentId = the pack) ----------------
        // The Situations tab: Situation Search over the round index. Default-visible to every category
        // like Reels, for the same reason: the flagship's payoff must not hide from the audience that
        // wants it.
        new(
            "tab.situations", FeatureScope.Tab, "Situations",
            "Find rounds by where the players stood — search the library's round index for a setup, "
            + "an execute or a retake and walk the hits.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Teams tab: who played in which demo, which team is us, the opponent per demo. Default-visible
        // like Situations: the Library's team filter and every "our / their" surface read what is decided
        // here.
        new(
            "tab.teams", FeatureScope.Tab, "Teams",
            "The teams found across your demos: name them, say which one is you, merge or split rosters, "
            + "and confirm your own accounts so every demo knows which side is ours.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Review tab: the Review Queue every surface sends clips to, the Reels tray's included.
        // Default-visible like Teams: the Reels tray stages into it whether or not it shows.
        new(
            ReviewQueueModule.TabFeatureId, FeatureScope.Tab, "Review",
            "One queue of clips from any demo: staged highlights, situation search results and picks at the "
            + "playhead, in sections with a question per clip.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Strat Book's Suggested section: every demo's tag suggestions in one inbox. Default-visible like
        // Review.
        new(
            SuggestedInboxModule.TabFeatureId, FeatureScope.Tab, "Suggested",
            "Every demo's tag suggestions in one list: accept them into the demo's tags, dismiss them, or open "
            + "one in 2D Playback.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Round Tagger's Matrix tab: codes by labels across the library's tags. Default-visible like
        // Situations and Teams.
        new(
            RoundTaggerModule.TabFeatureId, FeatureScope.Tab, "Round Tagger",
            "Tag stretches of a round with your own codes and labels, then pivot them across every demo "
            + "in the Matrix.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Strat Book tab: strats per book (a team or you) on the round clock, with slots, steps and
        // branches. Default-visible like the Matrix, and on both hosts: the browser keeps strats for the
        // session and says so.
        new(
            StratBookModule.TabFeatureId, FeatureScope.Tab, "Strat Book",
            "Write your team's strats on the round clock: five slots, the steps each one takes, and the "
            + "branches when the plan changes.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Utility Book tab: the Grenade Index, every indexed grenade clustered by where it landed.
        // Default-visible like the Strat Book, and on both hosts: the browser indexes the open demo for the
        // session and says so.
        new(
            UtilityBookModule.TabFeatureId, FeatureScope.Tab, "Utility Book",
            "Every grenade in your indexed demos, grouped by where it landed: pick a map, a grenade and a "
            + "landing place to see every position it was thrown from.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // The Opponent Dossier tab: the Map Pool Record and, later, the rest of the Dossier sections,
        // keyed by a Team Identity team. Default-visible like the Strat Book and the Utility Book, and
        // on both hosts: the browser keeps teams for the session and the Teams tab already says so.
        new(
            DossierModule.TabFeatureId, FeatureScope.Tab, "Dossier",
            "A scouting page per team: maps played, win rate, side wins and the decider record where "
            + "it is inferable, plus a veto history you enter by hand.",
            PackFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),

        // ---------------- SUB-FEATURES (ParentId = owning tab) ----------------
        // The Round Tagger's palette docked in the 2D tab (tag-store.md §3.11). Works on both hosts: the
        // browser keeps tags for the session and the palette says so.
        new(
            RoundTaggerModule.PaletteFeatureId, FeatureScope.SubFeature, "Tag palette",
            "Tag the round you are watching with a hotkey palette; tags are saved per demo.",
            "tab.playback2d", null, false, FeatureCatalog.Defaults(true, true, true)),
        // Suggested Tags (suggested-tags.md §3.6): the Suggested track, the proposal queue and the
        // evaluator. On for both hosts; the browser keeps proposals and verdicts for the session.
        new(
            SuggestedTagsService.FeatureId, FeatureScope.SubFeature, "Suggested tags",
            "Offer tags found by detectors (execute, default, fake, opener, retake) to accept, edit or reject.",
            "tab.playback2d", null, false, FeatureCatalog.Defaults(true, true, true)),
        // Strat Export (step-authoring.md §3.6): the open strat to GIF or video with no demo behind it. Desktop
        // only for playback2d.export's reason, through the same ShellModuleFeatureGate.DesktopOnlyIds.
        new(
            StratBookTabViewModel.ExportFeatureId, FeatureScope.SubFeature, "Strat export",
            "Render a strat to gif/webm/mp4 from the Strat Book canvas. Desktop only.",
            StratBookModule.TabFeatureId, null, false, FeatureCatalog.Defaults(true, true, true)),
        // Token routing (docs/strat-book/token-pathing.md): strat tokens walk the map's nav round walls instead of in
        // straight lines, on the canvas, the Detected preview and an export. On by default; off is the straight lines
        // and timing strats had before. Both hosts: the graph is built from the map's zones.json.
        new(
            FeatureCatalog.StratRoutingFeatureId, FeatureScope.SubFeature, "Token routing",
            "Move strat tokens along the map's walkways instead of in straight lines through walls.",
            StratBookModule.TabFeatureId, null, false, FeatureCatalog.Defaults(true, true, true))
    ];

    /// <inheritdoc />
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The was-built tracker (item 3): one instance, read by StratBookLifecycle.OnShutdown and by the
        // pack-off composition-root test. Set only by the factories below, never by a caller asking for it.
        services.AddSingleton<StratBookPackInstances>();

        // SituationIndex, TeamIdentityService and TagFactsRefresher are registered by the composition root
        // (core: other surfaces read them), so their factories are wrapped here rather than written here.
        // The wrap changes nothing about what is registered (same type, same Singleton lifetime), only
        // which object is told it was built.
        TrackBuilt<SituationIndex>(services);
        TrackBuilt<TeamIdentityService>(services);
        TrackBuilt<TagFactsRefresher>(services);
        // Round Index is core-registered too (SituationIndex depends on it directly), so it is wrapped
        // the same way, through the non-resident tracker (item 11): it has no Attach/Release, just a
        // was-it-constructed field for a test. The other three evaluators below are registered here, so
        // they record themselves inline instead.
        TrackBuiltEvaluator<RoundIndexEvaluator>(services, (instances, built) => instances.RoundIndex = built);

        // Round Facts: the per-round, per-side record every Strat Room feature filters on. An evaluator on
        // the tier-2 fan-out (no second parse) writing into the unified cache's Analysis tier under the
        // round_facts ruleset's own fingerprint, and the read API over those rows. The row source and the
        // identity share the composition root's merged build, so the rows are always stored under the
        // fingerprint of the doc that produced them. The evaluator is gated on the pack; the ruleset
        // itself leaves the merged set through the Contribute claim below.
        services.AddSingleton(sp => new RulesRoundFactsRulesetIdentity(sp.GetRequiredService<MergedRulesBuild>()));
        services.AddSingleton<IRoundFactsRulesetIdentity>(sp => sp.GetRequiredService<RulesRoundFactsRulesetIdentity>());
        services.AddSingleton<IRoundFactsRowSource>(sp =>
            new EngineRoundFactsRowSource(sp.GetRequiredService<RulesRoundFactsRulesetIdentity>()));
        services.AddSingleton(sp =>
        {
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            RoundFactsEvaluator built = new(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<IRoundFactsRowSource>(),
                sp.GetRequiredService<IRoundFactsRulesetIdentity>(),
                action => Dispatcher.UIThread.Post(action),
                enabled: () => features?.IsEnabled(PackFeatureId) ?? true);
            sp.GetRequiredService<StratBookPackInstances>().RoundFacts = built;
            return built;
        });
        // The reader is gated too, so off looks off: rows written while on stop surfacing until the pack
        // comes back. They stay on disk.
        services.AddSingleton<IRoundFactsSource>(sp =>
        {
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            return new RoundFactsSource(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<RoundFactsEvaluator>(),
                enabled: () => features?.IsEnabled(PackFeatureId) ?? true);
        });

        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            return new SituationsTabViewModel(
                sp.GetRequiredService<ISituationIndex>(),
                sp.GetRequiredService<RoundIndexEvaluator>(),
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<RoundIndexPlaceSources>(),
                () => monitor?.CurrentValue.Situations.TokenSource ?? RoundIndexTokenSource.Pawn,
                playback: () => sp.GetService<ISituationPlayback>(),
                sidecars: sp.GetRequiredService<RoundIndexStore>(),
                // The filter rail's opponent and our-side fields join through Team Identity; its source
                // field through Demo Provenance Labels.
                teams: sp.GetRequiredService<TeamIdentityService>(),
                provenance: sp.GetRequiredService<IDemoProvenanceSource>(),
                watched: sp.GetRequiredService<WatchedSituationsService>(),
                review: sp.GetRequiredService<ReviewQueue>(),
                // The canvas shows the "us" team's callouts (Callout Aliases, strat-model.md §3.7) over
                // the stored canonical place names; no team marked falls back to the me book, same as the
                // Strat Book's own default.
                callouts: sp.GetRequiredService<CalloutResolverSource>(),
                run: part => work => QueueWork.Run(sp.GetRequiredService<IDemoProcessingQueue>(),
                    QueueJobKind.SectionCompute, "Situations: " + part, "situations", _ => work(), key: "section:situations:" + part,
                    preemptible: true));
        });

        // The Review tab VM: a container singleton resolved lazily on first activation, opening clips
        // through the same seek seam the Result Cards use.
        // Export pack renders the queue as one video (Pack Export): a private parse per demo, each demo's
        // saved ink, one encode for the whole pack. A user-requested processing queue item that marks an export
        // session on the heavy-job gate for its run, as a 2D export does; the browser has no ffmpeg and no
        // files, so it gets no pack row.
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            HeavyJobGate gate = sp.GetRequiredService<HeavyJobGate>();
            IDemoProcessingQueue queue = sp.GetRequiredService<IDemoProcessingQueue>();
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            TeamIdentityService teams = sp.GetRequiredService<TeamIdentityService>();
            Func<PackPlan, IProgress<PackProgress>, CancellationToken, Task<PackResult>>? exportPack = null;
            if (!OperatingSystem.IsBrowser())
            {
                ILogger log = DiagnosticsLog.CreateLogger(PackExportLog.Category);
                exportPack = (plan, progress, ct) => PackExportQueue.RunAsync(queue,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Pack export: {plan.Segments.Count} segments to {Path.GetFileName(plan.Settings.OutputPath)}"),
                    plan.Settings.OutputPath, async (relay, token) =>
                    {
                        using IDisposable session = await gate.EnterExportSessionAsync(token).ConfigureAwait(false);
                        using PackClipRenderer clips = new(gate, new AnnotationStore(AppPaths.ConfigRoot),
                            log: line => PackExportLog.Line(log, line));
                        PackExporter exporter = new(clips, new PackEncoder(log: line => PackExportLog.Encoder(log, line)),
                            log: line => PackExportLog.Line(log, line));
                        return await exporter.ExportAsync(plan, relay, token).ConfigureAwait(false);
                    }, progress, ct);
            }

            return new ReviewQueueTabViewModel(
                sp.GetRequiredService<ReviewQueue>(),
                () => sp.GetService<ISituationPlayback>(),
                exportPack: exportPack,
                packDirectory: monitor?.CurrentValue.Playback2D.ExportOutputDirectory,
                mapOf: clip => (clip.Sha256 is { } sha ? cache.TryGetIndexBySha256(sha) : null)?.Map
                               ?? cache.TryGetIndex(clip.DemoPath)?.Map,
                teamName: id => teams.AllTeams.FirstOrDefault(t => t.Id == id || t.MergedFrom.Contains(id))?.Name);
        });

        // The Strat Book's Suggested section: every demo's tag suggestions in one inbox, read as a queue item.
        services.AddSingleton(sp => new SuggestedInboxService(
            sp.GetRequiredService<SuggestedTagsService>(),
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<IDemoProcessingQueue>(),
            run: work => QueueWork.Run(sp.GetRequiredService<IDemoProcessingQueue>(), QueueJobKind.SectionCompute,
                "Suggested: read a demo", "suggested", _ => work()),
            post: action => Dispatcher.UIThread.Post(action)));
        services.AddSingleton(sp => new SuggestedInboxViewModel(
            sp.GetService<SuggestedInboxService>(),
            () => sp.GetService<ISituationPlayback>()));

        // Watched Situations: the saved queries in watched-situations.json beside teams.json, re-run
        // over one demo on the index's Indexed hook and over the library at the watermark on every
        // other change. A container singleton so the module's badge and the tab's list share one
        // state; null config root (the browser) keeps the list for the session.
        services.AddSingleton(sp =>
        {
            WatchedSituationsService watched = new(
                AppPaths.ConfigRoot,
                sp.GetRequiredService<ISituationIndex>(),
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<TeamIdentityService>(),
                sp.GetRequiredService<IDemoProvenanceSource>(),
                action => Dispatcher.UIThread.Post(action));
            sp.GetRequiredService<StratBookPackInstances>().Record(watched);
            return watched;
        });

        // The Matrix: tag instances pivoted over the store, a container singleton resolved lazily on first
        // activation. The cache index is the hash-to-path join a cell's clips need, Team Identity the scope
        // of the multi-demo mode, and the tab switch after a send reaches the shell at call time.
        services.AddSingleton(sp =>
        {
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            return new TagMatrixTabViewModel(
                sp.GetRequiredService<TagStore>(),
                sp.GetRequiredService<ReviewQueue>(),
                cache.TryGetIndexBySha256,
                sp.GetRequiredService<TeamIdentityService>(),
                tabId => App.Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false,
                action => Dispatcher.UIThread.Post(action),
                run: work => QueueWork.Run(sp.GetRequiredService<IDemoProcessingQueue>(), QueueJobKind.SectionCompute,
                    "Tags: matrix", "tags", _ => work(), key: "section:tag-matrix", preemptible: true));
        });

        // Suggested Tags: the detectors as an evaluator one place after the Round Index, reading the index
        // it wrote in the same pass (overview correction 19). Proposals go to cache/suggestions/ beside
        // demos/, verdicts to the Tag Store under the tags root (correction 2); the learned site regions
        // come from <config>/suggested-tags/. The library sweep is its own opt-in, off by default
        // (correction 20); the open demo, resolved at call time, is always built. Null roots (the
        // browser) keep all of it for the session.
        services.AddSingleton(sp => new ProposalStore(AppPaths.DemoCacheDir, sp.GetRequiredService<DemoCacheStore>()));
        services.AddSingleton(_ => new SiteRegionStore(AppPaths.SuggestedTagsDirectory));
        // The parameter profile: <config>/suggested-tags/profile.json, seeded with the shipped default
        // on first read the way a theme drop-in folder is (§3.7). A singleton so the evaluator's Func
        // and the tuning view's save reach the same in-memory Current.
        services.AddSingleton(_ => new ProfileStore(AppPaths.SuggestedTagsDirectory));
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            SuggestedTagsService built = new(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<ProposalStore>(),
                sp.GetRequiredService<TagStore>(),
                sp.GetRequiredService<SiteRegionStore>(),
                () => sp.GetRequiredService<ProfileStore>().Current,
                () => features?.IsEnabled(SuggestedTagsService.FeatureId) ?? true,
                () => monitor?.CurrentValue.Playback2D.SuggestedTagsBackground ?? false,
                sp.GetRequiredService<RoundIndexStore>(),
                sp.GetRequiredService<RoundIndexPlaceSources>(),
                sp.GetRequiredService<IZonePlaceResolverSource>(),
                map => sp.GetRequiredService<ISituationIndex>().Places(map),
                () => App.Services?.GetService<MainViewModel>()?.LoadedDemoPath,
                action => Dispatcher.UIThread.Post(action));
            sp.GetRequiredService<StratBookPackInstances>().SuggestedTags = built;
            // Set here, not by the evaluator registry's lazy wrapper, which only runs once something has
            // already polled the coordinator.
            built.Coordinator = sp.GetRequiredService<DemoEvaluationCoordinator>();
            return built;
        });
        // The tuning view's harness: stored counts for free, an in-memory re-run over a candidate
        // profile for recall/precision (§3.7). Shares the evaluator's store and region table so a
        // preview scores exactly what the queue already built.
        services.AddSingleton(sp => new SuggestedTagsTuningService(
            sp.GetRequiredService<DemoCacheStore>(),
            sp.GetRequiredService<SuggestedTagsService>(),
            sp.GetRequiredService<TagStore>(),
            sp.GetRequiredService<SiteRegionStore>()));

        // The Strat Book's store: one folder per book under <config>/strats. One per process, because CheckOut's
        // single-writer guarantee is only as wide as the instance that holds it. Null root (the browser) keeps
        // strats in memory for the session. The tab VM is a container singleton resolved lazily on first
        // activation; its books are Team Identity's teams plus me.
        services.AddSingleton(_ => new StratStore(AppPaths.StratsDir, action => Dispatcher.UIThread.Post(action)));
        // Callout Aliases (strat-model.md §3.7): one resolver builder over the store's tables and the map's
        // baked-plus-overlay zones, shared by the Strat Book and anything else that turns a team's word into
        // a nav place.
        services.AddSingleton(sp => new CalloutResolverSource(sp.GetRequiredService<StratStore>()));
        // Strat Record Panel (strat-model.md §3.6): the evidence rule over the Tag Store and Demo
        // Provenance Labels, one instance so the panel's live rebuild and any other future reader of a
        // strat's record agree on what "run / won / aborted" means.
        services.AddSingleton(sp => new StratEvidenceService(
            sp.GetRequiredService<TagStore>(),
            sp.GetRequiredService<IDemoProvenanceSource>()));
        // Strat Mining: repeated setups and executes found from cached files, offered in the Strats
        // section's Detected inbox and written to a book only when the user adds one.
        services.AddSingleton(sp =>
        {
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            StratMiningService mining = new(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<RoundIndexStore>(),
                sp.GetRequiredService<RoundIndexPlaceSources>().FingerprintFor,
                sp.GetRequiredService<GrenadeIndex>(),
                sp.GetRequiredService<TeamIdentityService>(),
                sp.GetRequiredService<StratStore>(),
                sp.GetRequiredService<TagStore>(),
                AppPaths.DemoCacheDir,
                AppPaths.ConfigRoot,
                action => Dispatcher.UIThread.Post(action),
                queue: sp.GetRequiredService<IDemoProcessingQueue>(),
                enabled: () => features?.IsEnabled(PackFeatureId) ?? true);
            sp.GetRequiredService<StratBookPackInstances>().Record(mining);
            return mining;
        });
        services.AddSingleton<StratBookLayout>();
        services.AddSingleton(sp =>
        {
            DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
            // The canvas (and the Detected preview's) fallback when nobody passes placesFor/routing
            // explicitly: the real gate and zone source, not App.Services.
            StratCanvasServices canvasServices = new(
                sp.GetService<IFeatureGate>(), sp.GetService<IZonePlaceResolverSource>(), sp.GetRequiredService<SettingsService>());
            return new StratBookTabViewModel(
                sp.GetRequiredService<StratStore>(),
                sp.GetRequiredService<TeamIdentityService>(),
                action => Dispatcher.UIThread.Post(action),
                calloutResolvers: sp.GetRequiredService<CalloutResolverSource>(),
                tags: sp.GetRequiredService<TagStore>(),
                evidence: sp.GetRequiredService<StratEvidenceService>(),
                review: sp.GetRequiredService<ReviewQueue>(),
                indexBySha: cache.TryGetIndexBySha256,
                selectTab: tabId => App.Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false,
                grenades: sp.GetRequiredService<GrenadeIndex>(),
                mining: sp.GetRequiredService<StratMiningService>(),
                playback: () => sp.GetService<ISituationPlayback>(),
                spawns: new StratSpawnSource(),
                layout: sp.GetRequiredService<StratBookLayout>(),
                lineupMap: (map, asset) => UtilityBookFor(sp, map, asset),
                canvasServices: canvasServices);
        });

        // Create Strat From Round (step-authoring.md §3.9). Transient: the gate is read fresh on every
        // resolve. A Singleton would cache a null from a startup-time off state forever.
        services.AddTransient<IStratCapture>(sp =>
        {
            IFeatureGate? gate = sp.GetService<IFeatureGate>();
            if (!(gate?.IsEnabled(PackFeatureId) ?? true))
            {
                return null!;
            }

            return new StratCaptureHost(
                () => sp.GetService<MainViewModel>()?.ModuleContext is ICurrentDemoSource source ? source.CurrentDemo : null,
                sp.GetRequiredService<StratStore>(),
                sp.GetService<TeamIdentityService>(),
                id =>
                {
                    // The tab first: activation refreshes its list, which the open strat is then selected in.
                    sp.GetService<MainViewModel>()?.TrySelectTab(StratBookModule.BrowserTabId);
                    sp.GetRequiredService<StratBookTabViewModel>().OpenStrat(id);
                });
        });

        // Strat Export (step-authoring.md §3.6). No export on the browser: no ffmpeg, no files.
        services.AddTransient<IStratExport>(sp =>
        {
            IFeatureGate? gate = sp.GetService<IFeatureGate>();
            if (!(gate?.IsEnabled(PackFeatureId) ?? true) || OperatingSystem.IsBrowser())
            {
                return null!;
            }

            SettingsService settings = sp.GetRequiredService<SettingsService>();
            return new StratExportHost(
                sp.GetRequiredService<HeavyJobGate>(),
                () => sp.GetService<MainViewModel>()?.LiveSync?.State.IsSessionActive == true,
                () => sp.GetService<MainViewModel>()?.ReelJob?.Status.IsRunning == true,
                () => settings.Current,
                settings.Write,
                status => sp.GetService<MainViewModel>()?.AttachStratExportStatus(status),
                path => sp.GetService<MainViewModel>()?.OpenOutputFolder(path));
        });

        // J / K in 2D playback walk the Situations result set: the same lazy resolution as Find Rounds
        // Like This, so the set the keys walk is the set the tab shows.
        services.AddSingleton<ISituationResultWalk>(sp => new SituationResultWalk(
            sp.GetRequiredService<SituationsTabViewModel>));

        // Find Rounds Like This: the 2D tab's Ctrl+F hands its current tick through this seam. The tab
        // VM resolves lazily (the same container singleton the module activates, so the canvas the key
        // fills is the one the tab shows), and the tab switch reaches the shell at call time, the way
        // the Settings factory reaches StartWalkthrough, never at construction.
        services.AddSingleton<IFindRoundsLikeThis>(sp => new FindRoundsLikeThis(
            sp.GetRequiredService<SituationsTabViewModel>,
            sp.GetRequiredService<RoundIndexPlaceSources>(),
            tabId => App.Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false));

        // The Grenade Walk (grenade-walk.md §3.8): every throw in a demo as one row, written as two siblings of
        // the demo's cache record. An evaluator on the same fan-out, last because it reads nothing the others
        // write. The library sweep is its own opt-in, off by default (D4); the open demo, resolved at call
        // time, is always walked on the parse its open paid for. Null cache root (the browser) keeps the
        // rows in memory for the session.
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            IFeatureGate? features = sp.GetService<IFeatureGate>();
            GrenadeIndexEvaluator built = new(
                sp.GetRequiredService<DemoCacheStore>(),
                () => monitor?.CurrentValue.Grenades.BackgroundIndex ?? false,
                () => App.Services?.GetService<MainViewModel>()?.LoadedDemoPath,
                () => monitor?.CurrentValue.Grenades.TrajectoryStride ?? 4,
                enabled: () => features?.IsEnabled(PackFeatureId) ?? true);
            sp.GetRequiredService<StratBookPackInstances>().GrenadeWalk = built;
            // Set here, not by the evaluator registry's lazy wrapper: GrenadeIndex resolves this directly
            // at StartPacks time, before anything has polled the coordinator, so a wrapper-only assignment
            // would leave Coordinator null and Request silently no-op until the first poll.
            built.Coordinator = sp.GetRequiredService<DemoEvaluationCoordinator>();
            return built;
        });

        // The Grenade Index: every current rows sibling in the library, clustered by landing cell with the
        // origins deduplicated, the landing place through the same zone resolver source the round index
        // uses. It loads once at startup off the UI thread and merges each demo as the evaluator writes it.
        services.AddSingleton(sp =>
        {
            GrenadeIndex index = new(
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<IZonePlaceResolverSource>(),
                sp.GetRequiredService<GrenadeIndexEvaluator>(),
                action => Dispatcher.UIThread.Post(action),
                scheduleSave: QueueWork.Saves(sp.GetRequiredService<IDemoProcessingQueue>(), "Save: grenade lineups",
                    "utility", "save:grenade-lineups"));
            sp.GetRequiredService<StratBookPackInstances>().Record(index);
            return index;
        });
        services.AddSingleton(sp => UtilityBookFor(sp, null, null));

        // The Opponent Dossier's veto history (F12, D5): manual entry only, beside teams.json. Null
        // config root (the browser) keeps entries in memory for the session.
        services.AddSingleton(_ => new VetoHistoryStore(AppPaths.ConfigRoot));
        // Dossier Editing And Export: the user's stars, rewritten lines, notes and summary per team, beside
        // the veto history; session-only on the browser the same way.
        services.AddSingleton(_ => new DossierNotesStore(AppPaths.ConfigRoot));
        // The Dossier tab VM: a container singleton resolved lazily on first activation, over Team
        // Identity's own teams and the unified cache the Map Pool Record reads. The Setup Heatmaps read
        // the round index's positions files under the same fingerprint the Situations tab trusts, and
        // open their rounds in the Review Queue. The Opening Tendencies join the Grenade Index to Round
        // Facts and read the same positions files for the lurk; the Post-Plant And Retake read them for the
        // plant spots, the holds and the retakes. The Situational Behaviour reads Round Facts alone.
        services.AddSingleton(sp =>
        {
            RoundIndexPlaceSources sources = sp.GetRequiredService<RoundIndexPlaceSources>();
            return new DossierTabViewModel(
                sp.GetRequiredService<TeamIdentityService>(),
                sp.GetRequiredService<DemoCacheStore>(),
                sp.GetRequiredService<VetoHistoryStore>(),
                heatmaps: new SetupHeatmapService(
                    sp.GetRequiredService<TeamIdentityService>(),
                    sp.GetRequiredService<DemoCacheStore>(),
                    sp.GetRequiredService<RoundIndexStore>(),
                    sources.FingerprintFor),
                review: sp.GetRequiredService<ReviewQueue>(),
                selectTab: tabId => App.Services?.GetService<MainViewModel>()?.TrySelectTab(tabId) ?? false,
                openings: new OpeningTendenciesService(
                    sp.GetRequiredService<TeamIdentityService>(),
                    sp.GetRequiredService<DemoCacheStore>(),
                    sp.GetRequiredService<GrenadeIndex>(),
                    sp.GetRequiredService<RoundIndexStore>(),
                    sources.FingerprintFor),
                postPlant: new PostPlantService(
                    sp.GetRequiredService<TeamIdentityService>(),
                    sp.GetRequiredService<DemoCacheStore>(),
                    sp.GetRequiredService<RoundIndexStore>(),
                    sources.FingerprintFor),
                situational: new SituationalBehaviourService(
                    sp.GetRequiredService<TeamIdentityService>(),
                    sp.GetRequiredService<DemoCacheStore>()),
                notes: sp.GetRequiredService<DossierNotesStore>(),
                grenades: sp.GetRequiredService<GrenadeIndex>(),
                runSection: section => work => QueueWork.Run(sp.GetRequiredService<IDemoProcessingQueue>(),
                    QueueJobKind.SectionCompute, "Dossier: " + section, "dossier", _ => work(), key: "section:dossier:" + section,
                    preemptible: true));
        });

        // Lineup Clip Render: every repeated throw position and technique gets a GIF and its setpos line,
        // rendered one demo at a time as processing queue items and shown on the Utility Book's position card.
        // Planned whenever the index changes; a null directory (the browser) plans nothing.
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<AppSettings>? monitor = sp.GetService<IOptionsMonitor<AppSettings>>();
            GrenadeIndex index = sp.GetRequiredService<GrenadeIndex>();
            ILogger log = DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category);
            LineupClipService clips = new(
                () => [.. index.Maps().SelectMany(map => index.Query(new GrenadeQuery(map)))],
                AppPaths.ConfigRoot is { } root ? Path.Combine(root, App.LineupClipDirectoryName) : null,
                () => monitor?.CurrentValue.Grenades.RenderLineupClips ?? true,
                new LineupClipRenderer(log: line => GrenadeIndexLog.LineupClip(log, line)),
                log: line => GrenadeIndexLog.LineupClip(log, line),
                complete: () => index.IsReady,
                maxBytes: () => (monitor?.CurrentValue.Grenades.LineupClipsMaxMegabytes ?? 1024) * 1024L * 1024L,
                processing: sp.GetRequiredService<IDemoProcessingQueue>());
            index.Changed += () => clips.PlanSoon();
            sp.GetRequiredService<StratBookPackInstances>().Record(clips);
            return clips;
        });

        // The pack's lifecycle (item 3): resolved by the app only while pack.stratbook resolves on,
        // keyed by the pack's own id so a future second pack's lifecycle never collides with this one.
        services.AddKeyedSingleton<IPackLifecycle, StratBookLifecycle>(Id);
    }

    // Wraps an existing registration's factory so the built instance is also recorded on the tracker,
    // without adding, dropping or re-scoping the registration itself (StratBookPackTests pins the set).
    private static void TrackBuilt<T>(IServiceCollection services)
        where T : class, IPackResident
    {
        ServiceDescriptor original = services.First(d => d.ServiceType == typeof(T));
        Func<IServiceProvider, object> factory = original.ImplementationFactory
            ?? throw new InvalidOperationException($"{typeof(T)} must be registered with a factory to track it.");
        services.Replace(ServiceDescriptor.Singleton(typeof(T), sp =>
        {
            T built = (T)factory(sp);
            sp.GetRequiredService<StratBookPackInstances>().Record(built);
            return built;
        }));
    }

    // Like TrackBuilt, for a core-registered evaluator that is not an IPackResident (item 11): no
    // Attach/Release, just a was-it-constructed field a test reads.
    private static void TrackBuiltEvaluator<T>(IServiceCollection services, Action<StratBookPackInstances, T> record)
        where T : class
    {
        ServiceDescriptor original = services.First(d => d.ServiceType == typeof(T));
        Func<IServiceProvider, object> factory = original.ImplementationFactory
            ?? throw new InvalidOperationException($"{typeof(T)} must be registered with a factory to track it.");
        services.Replace(ServiceDescriptor.Singleton(typeof(T), sp =>
        {
            T built = (T)factory(sp);
            record(sp.GetRequiredService<StratBookPackInstances>(), built);
            return built;
        }));
    }

    /// <inheritdoc />
    public void Contribute(IPackContributions contributions, IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(sp);

        contributions.Commands(StratBookCommands.All);

        // The hub every section below sits on. The shell builds the hub VM when it builds the strip, so the
        // layout singleton resolves then, pack on or off, as it did when the shell took it by constructor.
        contributions.HostTab(HubHostTab(sp.GetRequiredService<StratBookLayout>));

        // Every module is registered on both hosts; each degrades to session-only state in the browser and
        // says so. The VMs are container singletons resolved lazily on first activation, so nothing here
        // constructs one. The order is the shell's registration order and is pinned by a test.

        // The round_facts ruleset in the rules directories is the pack's: MergedRulesBuild runs it only
        // while the pack is on and keeps it out of the highlights fingerprint. Not a doc of its own, so
        // the user overlay and the Workbench keep working on it.
        contributions.Ruleset(RoundFactsFingerprint.RulesetId);

        // The four pack evaluators on the demo fan-out (item 11), ordered to match the dependency chain
        // each one reads: Round Facts after the library write, Round Index after Round Facts' rows,
        // Suggested Tags after the index it queries, Grenades after the library write (it reads nothing
        // the others write). The registry resolves this only while the pack is on, so these factories are
        // never invoked, and these services never constructed, with the pack off.
        string libraryId = sp.GetRequiredService<DemoLibraryService>().Id;
        contributions.Evaluator(RoundFactsEvaluator.EvaluatorId, () => sp.GetRequiredService<RoundFactsEvaluator>(),
            libraryId);
        contributions.Evaluator(RoundIndexEvaluator.EvaluatorId, () => sp.GetRequiredService<RoundIndexEvaluator>(),
            RoundFactsEvaluator.EvaluatorId);
        contributions.Evaluator(SuggestedTagsService.EvaluatorId, () => sp.GetRequiredService<SuggestedTagsService>(),
            RoundIndexEvaluator.EvaluatorId);
        contributions.Evaluator(GrenadeIndexEvaluator.EvaluatorId, () => sp.GetRequiredService<GrenadeIndexEvaluator>(),
            libraryId);

        // The pack's five job kinds (item 13), from the same array JobKinds below exposes DI-free.
        foreach (JobKindDescriptor kind in StratBookJobKinds.All)
        {
            contributions.JobKind(kind);
        }

        // Create Strat From Round in 2D Playback (item 16): the round band's entry and the review pane, one
        // contribution. It resolves IStratCapture through the tab's context when a band is pressed, so the
        // gate above decides what the band offers; nothing is constructed here.
        contributions.Playback(new CreateStratPlaybackContribution(action => Dispatcher.UIThread.Post(action)));

        // The Situations tab. The badge reads Watched Situations, so the service resolves now, but only
        // while the section's own id is on: enabled/gate read sp directly, not the App.Services locator
        // (Contribute runs inside BuildServiceProvider, before App.Services is assigned), and resolving
        // WatchedSituationsService unconditionally would build it (and, through its own ctor, the
        // situation index and Team Identity) on every launch regardless of the pack's gate.
        IFeatureGate? situationsGate = sp.GetService<IFeatureGate>();
        bool situationsOn = situationsGate?.IsEnabled(SituationsModule.TabFeatureId) ?? false;
        contributions.Module(new SituationsModule(sp.GetRequiredService<SituationsTabViewModel>,
            situationsOn ? sp.GetRequiredService<WatchedSituationsService>() : null,
            enabled: () => situationsGate?.IsEnabled(SituationsModule.TabFeatureId) ?? false,
            gate: situationsGate));

        // The Teams tab, hosted inside the Library.
        contributions.Module(new TeamsModule(sp.GetRequiredService<TeamsTabViewModel>));

        // The Review tab. Same gated-resolve shape as Situations: ReviewQueue is core (Reels uses it too),
        // but resolving it here regardless of the gate still queued its startup load on every launch.
        IFeatureGate? reviewGate = sp.GetService<IFeatureGate>();
        bool reviewOn = reviewGate?.IsEnabled(ReviewQueueModule.TabFeatureId) ?? false;
        contributions.Module(new ReviewQueueModule(sp.GetRequiredService<ReviewQueueTabViewModel>,
            reviewOn ? sp.GetRequiredService<ReviewQueue>() : null,
            enabled: () => reviewGate?.IsEnabled(ReviewQueueModule.TabFeatureId) ?? false,
            gate: reviewGate));

        // The Suggested section. The badge reads the demo index, so it counts before the section opens.
        // enabled/gate read sp directly, not the App.Services locator: Contribute runs inside
        // BuildServiceProvider, before App.Services is assigned, so the static fallback would see null here.
        IFeatureGate? suggestedGate = sp.GetService<IFeatureGate>();
        contributions.Module(new SuggestedInboxModule(sp.GetRequiredService<SuggestedInboxViewModel>,
            sp.GetService<DemoCacheStore>(),
            enabled: () => suggestedGate?.IsEnabled(SuggestedInboxModule.TabFeatureId) ?? false,
            gate: suggestedGate));

        // The Round Tagger's Matrix tab.
        contributions.Module(new RoundTaggerModule(sp.GetRequiredService<TagMatrixTabViewModel>));

        // The Strat Book tab.
        contributions.Module(new StratBookModule(sp.GetRequiredService<StratBookTabViewModel>));

        // The Utility Book tab.
        contributions.Module(new UtilityBookModule(sp.GetRequiredService<UtilityBookTabViewModel>));

        // The Opponent Dossier tab.
        contributions.Module(new DossierModule(sp.GetRequiredService<DossierTabViewModel>));
    }

    // The Utility Book tab, and the Strat Book's lineup picker (locked to the strat's map): the same index,
    // clip directory and queue section, so the picker shows what the tab shows. The picker draws the strat
    // canvas's bundle instead of decoding its own; the canvas keeps it.
    private static UtilityBookTabViewModel UtilityBookFor(IServiceProvider sp, string? lockedMap, Playback2D.Pipeline.Assets.LoadedMapAsset? sharedAsset)
    {
        DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
        return new UtilityBookTabViewModel(
            sp.GetRequiredService<GrenadeIndex>(),
            sp.GetRequiredService<ISituationPlayback>(),
            demoDate: path => cache.TryGetIndex(path) is { ModifiedTicks: > 0 } entry ? new DateTime(entry.ModifiedTicks) : null,
            clipDirectory: AppPaths.ConfigRoot is { } root ? Path.Combine(root, App.LineupClipDirectoryName) : null,
            background: lockedMap is null
                ? QueueWork.Section(sp.GetRequiredService<IDemoProcessingQueue>(), "Utility Book", "utility", "section:utility")
                : QueueWork.Section(sp.GetRequiredService<IDemoProcessingQueue>(), "Lineup picker", "utility", "section:lineup-picker"),
            post: work => Dispatcher.UIThread.Post(work),
            lockedMap: lockedMap,
            loadMapAsset: lockedMap is null ? null : _ => sharedAsset,
            ownsMapAsset: lockedMap is null);
    }

}
