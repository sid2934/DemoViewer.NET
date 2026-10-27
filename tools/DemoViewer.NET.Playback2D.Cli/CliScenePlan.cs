#region

using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Overlay;
using DemoViewer.NET.Playback2D.Query;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Overlay;
using DemoViewer.NET.Playback2D.Pipeline.Query;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The tool's half of <see cref="SceneRenderPlan" />: which flags mean what, and the two refusals
///     that exist because a request arrived on a command line rather than from code.
///     <para>
///         Everything here reads <see cref="CliArgs" /> or raises a usage error. The plan itself takes a
///         settled <see cref="ScenePlanOptions" />, which is what lets the golden test lane build the
///         same stack without a parser.
///     </para>
/// </summary>
internal static class CliScenePlan
{
    /// <summary>
    ///     Maps the shared flags onto a plan and builds it. Consumes <c>--cpu/--gpu/--strict-backend</c>,
    ///     <c>--assets/--no-radar</c>, <c>--layers/--exclude-layers</c>, <c>--size</c>,
    ///     <c>--layout/--level</c>.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="defaultSize">The size to use when <c>--size</c> is absent.</param>
    /// <param name="mapName">The map whose art to load, or null for none.</param>
    /// <param name="entryLayers">Layer ids from a corpus entry, overridden by <c>--layers</c>.</param>
    /// <param name="allowSizeOverride">
    ///     False leaves <c>--size</c> unconsumed, which turns it into the usage error it is on a lane
    ///     whose output size is not the caller's to pick.
    /// </param>
    /// <param name="defaultBackend">What "no backend was requested" means. See <see cref="BackendResolver.Preference" />.</param>
    /// <param name="annotations">
    ///     Ink to burn in, or null. A single-frame render has no demo and therefore no sidecar of its
    ///     own, so this arrives from <c>--ink</c> (<c>render</c>) or from the corpus convention; see
    ///     <see cref="FixtureInk" />.
    /// </param>
    /// <param name="zonesOverlay">
    ///     A user zones overlay to apply over the map's <c>zones.json</c>, or null for the baked set.
    ///     From <c>--zones-overlay</c> (<c>render</c>) or the corpus convention; see
    ///     <see cref="FixtureZones" />.
    /// </param>
    /// <param name="query">
    ///     The Query Canvas tokens to draw, or null. Arrives the same two ways the ink does; see
    ///     <see cref="FixtureQuery" />.
    /// </param>
    /// <param name="overlay">
    ///     The Overlay View points to draw, or null. Arrives the same two ways again; see
    ///     <see cref="FixtureOverlay" />.
    /// </param>
    public static SceneRenderPlan Build(CliArgs args, SKSizeI defaultSize, string? mapName,
        IReadOnlyList<string>? entryLayers = null, bool allowSizeOverride = true,
        RenderBackendPreference defaultBackend = RenderBackendPreference.Auto,
        AnnotationSession? annotations = null, string? zonesOverlay = null,
        QueryCanvasDocument? query = null, OverlayDocument? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        SKSizeI size = allowSizeOverride ? args.Size("size", defaultSize) : defaultSize;
        RequireSingleLevelLayout(args);

        return Build(new ScenePlanOptions
        {
            Size = size,
            MapName = mapName,
            IncludeLayers = args.List("layers") ?? entryLayers,
            ExcludeLayers = args.List("exclude-layers"),
            Backend = BackendResolver.Preference(args, defaultBackend),
            Assets = AssetsRootResolver.Resolve(args),
            Annotations = annotations,
            ZonesOverlayPath = zonesOverlay,
            Query = query,
            Overlay = overlay,
            Log = ConsoleOut.Info
        });
    }

    /// <summary>
    ///     Builds a settled request, reporting what the plan found and refusing what the tool must not
    ///     silently accept. The plan is the caller's to dispose.
    /// </summary>
    /// <param name="options">The resolved request.</param>
    /// <exception cref="CliUsageException">An unknown layer id, or an opt-in layer nothing can feed.</exception>
    /// <exception cref="BackendUnavailableException">A strict backend request cannot be satisfied.</exception>
    public static SceneRenderPlan Build(ScenePlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        SceneRenderPlan plan;
        try
        {
            plan = SceneRenderPlan.Build(options);
        }
        catch (ArgumentException e)
        {
            // An unknown id in --layers or --exclude-layers is something the caller typed, so it is a
            // usage error (exit 1) rather than the generic runtime failure (exit 3).
            throw new CliUsageException(e.Message, e);
        }

        try
        {
            foreach (ZoneDiagnostic diagnostic in plan.Zones.Diagnostics)
            {
                ConsoleOut.Warn($"zones overlay: {diagnostic.Code}: {diagnostic.Message}");
            }

            RequireFeedableOptIns(options.IncludeLayers, options.Annotations, plan.Zones.Resolver,
                options.Query, options.Overlay);
        }
        catch
        {
            plan.Dispose();
            throw;
        }

        return plan;
    }

    // `single` and a per-level selection need the level model's single-level layout, which is not
    // implemented. Accepting either and quietly rendering the stacked set gives you a golden captured
    // "with --level 1" that in fact shows every level, so anything but the default stacked layout
    // refuses with exit 6.
    internal static void RequireSingleLevelLayout(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string layout = args.String("layout") ?? "stacked";
        string? level = args.String("level");

        if (!string.Equals(layout, "stacked", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendUnavailableException(
                $"--layout {layout} needs the level model's single-level half (SingleLayout), which " +
                "is not in this build. Only --layout stacked renders today.");
        }

        if (level is not null)
        {
            throw new BackendUnavailableException(
                "--level needs the level model's single-level half (SingleLayout), which is not in " +
                "this build.");
        }
    }

    /// <summary>
    ///     Refuses an opt-in layer this command cannot feed. <c>CreateSceneStack</c> drops a starved
    ///     opt-in id on purpose: an export request that names <c>hud.clock</c> against a source with no
    ///     clock should draw no HUD rather than an empty box. But on a command line, asking for a layer
    ///     and getting a PNG must not mean it was not drawn. Both refusals name the command that can
    ///     draw the layer.
    /// </summary>
    /// <param name="include">The resolved <c>--layers</c> / corpus-entry id set, or null.</param>
    /// <param name="annotations">The ink actually loaded, or null.</param>
    /// <param name="zones">The place resolver actually loaded, or null.</param>
    /// <param name="query">The query fixture actually loaded, or null.</param>
    /// <param name="overlay">The overlay fixture actually loaded, or null.</param>
    internal static void RequireFeedableOptIns(IReadOnlyList<string>? include,
        AnnotationSession? annotations, PlaceResolver? zones, QueryCanvasDocument? query,
        OverlayDocument? overlay)
    {
        if (include is null)
        {
            return;
        }

        foreach (string raw in include)
        {
            string id = SceneLayerCatalog.Normalize(raw);

            if (string.Equals(id, SceneLayerIds.Annotations, StringComparison.Ordinal))
            {
                if (annotations is null)
                {
                    throw new CliUsageException(
                        $"--layers {raw} needs ink to draw. Pass --ink <file{AnnotationStore.SidecarExtension}>, " +
                        $"or name a corpus entry with an annotations/<name>{AnnotationStore.SidecarExtension} " +
                        "sidecar beside its scene.");
                }

                continue; // fed, so it is not one of the three that cannot be
            }

            if (string.Equals(id, SceneLayerIds.Zones, StringComparison.Ordinal))
            {
                if (zones is null)
                {
                    throw new CliUsageException(
                        $"--layers {raw} needs a map bundle with a {ZoneAssetPipeline.FileName} to draw: the scene " +
                        "must name a map, and --assets (or DV2D_ASSETS) must reach a directory holding that " +
                        "map's baked zones. --no-radar disables the asset root, zones included.");
                }

                continue;
            }

            if (string.Equals(id, SceneLayerIds.Query, StringComparison.Ordinal))
            {
                if (query is null)
                {
                    throw new CliUsageException(
                        $"--layers {raw} needs a query to draw. Pass --query <file{QueryFixtureStore.SidecarExtension}>, " +
                        $"or name a corpus entry with a {FixtureQuery.CorpusDirectoryName}/<name>{QueryFixtureStore.SidecarExtension} " +
                        "fixture beside its scene.");
                }

                continue;
            }

            if (string.Equals(id, SceneLayerIds.Overlay, StringComparison.Ordinal))
            {
                if (overlay is null)
                {
                    throw new CliUsageException(
                        $"--layers {raw} needs an overlay to draw. Pass --overlay <file{OverlayFixtureStore.SidecarExtension}>, " +
                        $"or name a corpus entry with an {FixtureOverlay.CorpusDirectoryName}/<name>{OverlayFixtureStore.SidecarExtension} " +
                        "fixture beside its scene.");
                }

                continue;
            }

            // The three HUD ids feed from an IHudDataSource, which is built over a demo's tracker
            // (ExportCommand.BuildHud): a clock, a scoreboard and a kill window are functions of a
            // parsed match, not of a single serialized frame. A fixture carries none of it, so
            // render/golden/bench cannot draw them.
            if (SceneLayerIds.OptIn.Contains(id))
            {
                throw new CliUsageException(
                    $"--layers {raw} is a HUD layer, and a HUD needs a demo's clock, scoreboard and kill " +
                    "timeline, none of which a fixture carries. Only 'dv2d export --hud' can feed it.");
            }
        }
    }
}
