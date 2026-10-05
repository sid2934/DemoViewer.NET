#region

using System.Reflection;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The layering rules that make Core and the scene contract a runtime instead of a UI helper, and the
///     scene contract's published surface. These are
///     asserted against assembly metadata rather than csproj text, because a transitive edge added three
///     projects away is exactly the kind of regression a reference-graph reading of the csproj misses.
/// </summary>
public class ArchitectureTests
{
    // Everything the scene contract is allowed to reference directly. SkiaSharp plus the BCL, and nothing else.
    private static readonly string[] _sceneAllowedPrefixes =
    [
        "SkiaSharp", "System", "netstandard", "mscorlib", "Microsoft.CSharp"
    ];

    // Core adds the scene contract it is built over, and nothing else.
    private static readonly string[] _coreAllowedPrefixes =
    [
        .. _sceneAllowedPrefixes, "DemoViewer.NET.Playback2D.Scene"
    ];

    // Published types that kept the namespace they had in Pipeline: the demo content key, the sidecar
    // identity records and the zone loaders. Nothing else in a Pipeline namespace is published.
    private static readonly string[] _keptPipelineNamespaceTypes =
    [
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.ClockIdentity",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.DemoIdentity",
        "DemoViewer.NET.Playback2D.Pipeline.Assets.ZoneAssetPipeline",
        "DemoViewer.NET.Playback2D.Pipeline.Assets.ZoneLoadResult",
        "DemoViewer.NET.Playback2D.Pipeline.Assets.ZoneOverlayReader",
        "DemoViewer.NET.Playback2D.Pipeline.Assets.ZoneSetReader",
        "DemoViewer.NET.Playback2D.Pipeline.DemoContentHash",
    ];

    // The scene contract's public types: what DemoViewer.NET.Playback2D.Scene promises to third parties.
    // A type joins or leaves this list on purpose, never as a side effect of moving code between assemblies.
    private static readonly string[] _publishedSceneTypes =
    [
        "DemoViewer.NET.Playback2D.Core.Annotations.AnnotationDocument",
        "DemoViewer.NET.Playback2D.Core.Annotations.AnnotationElement",
        "DemoViewer.NET.Playback2D.Core.Annotations.AnnotationKind",
        "DemoViewer.NET.Playback2D.Core.Annotations.AnnotationSession",
        "DemoViewer.NET.Playback2D.Core.Annotations.AnnotationStyle",
        "DemoViewer.NET.Playback2D.Core.Annotations.DocDelta",
        "DemoViewer.NET.Playback2D.Core.Annotations.DocDelta+Add",
        "DemoViewer.NET.Playback2D.Core.Annotations.DocDelta+Batch",
        "DemoViewer.NET.Playback2D.Core.Annotations.DocDelta+Remove",
        "DemoViewer.NET.Playback2D.Core.Annotations.DocDelta+Replace",
        "DemoViewer.NET.Playback2D.Core.Annotations.EnvelopeMode",
        "DemoViewer.NET.Playback2D.Core.Annotations.InkPoint",
        "DemoViewer.NET.Playback2D.Core.Annotations.SpaceRef",
        "DemoViewer.NET.Playback2D.Core.Annotations.SpaceRef+Entity",
        "DemoViewer.NET.Playback2D.Core.Annotations.SpaceRef+World",
        "DemoViewer.NET.Playback2D.Core.Annotations.StrokeTiming",
        "DemoViewer.NET.Playback2D.Core.Annotations.TimeEnvelope",
        "DemoViewer.NET.Playback2D.Core.Annotations.TimingRun",
        "DemoViewer.NET.Playback2D.Core.Annotations.WetStroke",
        "DemoViewer.NET.Playback2D.Core.AreaEffect",
        "DemoViewer.NET.Playback2D.Core.AreaEffectKind",
        "DemoViewer.NET.Playback2D.Core.BombMarker",
        "DemoViewer.NET.Playback2D.Core.Cameras.FitAliveRig",
        "DemoViewer.NET.Playback2D.Core.Cameras.FitMapRig",
        "DemoViewer.NET.Playback2D.Core.Cameras.FollowPlayerRig",
        "DemoViewer.NET.Playback2D.Core.Cameras.ICameraRig",
        "DemoViewer.NET.Playback2D.Core.Cameras.ManualRig",
        "DemoViewer.NET.Playback2D.Core.Compositing.ISceneLayer",
        "DemoViewer.NET.Playback2D.Core.Compositing.LayerCacheHint",
        "DemoViewer.NET.Playback2D.Core.Compositing.LayerSlot",
        "DemoViewer.NET.Playback2D.Core.Compositing.SceneCompositor",
        "DemoViewer.NET.Playback2D.Core.Compositing.SceneRenderContext",
        "DemoViewer.NET.Playback2D.Core.Compositing.SceneRenderGate",
        "DemoViewer.NET.Playback2D.Core.Compositing.SceneSubmission",
        "DemoViewer.NET.Playback2D.Core.ConePoint",
        "DemoViewer.NET.Playback2D.Core.GrenadeKind",
        "DemoViewer.NET.Playback2D.Core.GrenadeTrail",
        "DemoViewer.NET.Playback2D.Core.GrenadeTrailPoint",
        "DemoViewer.NET.Playback2D.Core.GuidePin",
        "DemoViewer.NET.Playback2D.Core.GuideToken",
        "DemoViewer.NET.Playback2D.Core.Hud.HudPlayerRow",
        "DemoViewer.NET.Playback2D.Core.Hud.HudSnapshot",
        "DemoViewer.NET.Playback2D.Core.Hud.HudStyle",
        "DemoViewer.NET.Playback2D.Core.Hud.IHudDataSource",
        "DemoViewer.NET.Playback2D.Core.Hud.IIconSource",
        "DemoViewer.NET.Playback2D.Core.ISmoothedPositionSource",
        "DemoViewer.NET.Playback2D.Core.Input.IPointerTool",
        "DemoViewer.NET.Playback2D.Core.Input.ITokenEditor",
        "DemoViewer.NET.Playback2D.Core.Input.IToolServices",
        "DemoViewer.NET.Playback2D.Core.Input.TokenGrip",
        "DemoViewer.NET.Playback2D.Core.Input.TokenHitTest",
        "DemoViewer.NET.Playback2D.Core.Input.ToolKind",
        "DemoViewer.NET.Playback2D.Core.Input.ToolKinds",
        "DemoViewer.NET.Playback2D.Core.Input.ToolModifiers",
        "DemoViewer.NET.Playback2D.Core.Input.ToolPointerButton",
        "DemoViewer.NET.Playback2D.Core.Input.ToolPointerEvent",
        "DemoViewer.NET.Playback2D.Core.Input.ToolWheelEvent",
        "DemoViewer.NET.Playback2D.Core.KillFeedRow",
        "DemoViewer.NET.Playback2D.Core.Layers.OverlayHeatmapLayer",
        "DemoViewer.NET.Playback2D.Core.Layers.QueryTokenLayer",
        "DemoViewer.NET.Playback2D.Core.Layers.SceneLayerIds",
        "DemoViewer.NET.Playback2D.Core.Layers.UtilityMapLayer",
        "DemoViewer.NET.Playback2D.Core.Layers.UtilityMapLayer+Disc",
        "DemoViewer.NET.Playback2D.Core.Levels.FloorSlice",
        "DemoViewer.NET.Playback2D.Core.Levels.FloorSplitter",
        "DemoViewer.NET.Playback2D.Core.Levels.ILevelLayoutPolicy",
        "DemoViewer.NET.Playback2D.Core.Levels.ILevelRadarBinder",
        "DemoViewer.NET.Playback2D.Core.Levels.IMapAsset",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelCrossingTracker",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelDisplayMode",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelHysteresis",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelHysteresisOptions",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelLayouts",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelPane",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelPaneSnapshot",
        "DemoViewer.NET.Playback2D.Core.Levels.LevelSetChange",
        "DemoViewer.NET.Playback2D.Core.Levels.MapLevel",
        "DemoViewer.NET.Playback2D.Core.Levels.MapLevelId",
        "DemoViewer.NET.Playback2D.Core.Levels.MapSpace",
        "DemoViewer.NET.Playback2D.Core.Levels.MapSpaceFactory",
        "DemoViewer.NET.Playback2D.Core.Levels.PaneSet",
        "DemoViewer.NET.Playback2D.Core.Levels.RadarBindingQuality",
        "DemoViewer.NET.Playback2D.Core.Levels.SingleLayout",
        "DemoViewer.NET.Playback2D.Core.Levels.StackedLayout",
        "DemoViewer.NET.Playback2D.Core.MapRadarImage",
        "DemoViewer.NET.Playback2D.Core.MarkerSmoother",
        "DemoViewer.NET.Playback2D.Core.Overlay.OverlayDocument",
        "DemoViewer.NET.Playback2D.Core.Overlay.OverlayPoint",
        "DemoViewer.NET.Playback2D.Core.PlayerMarker",
        "DemoViewer.NET.Playback2D.Core.Query.QueryCanvasDocument",
        "DemoViewer.NET.Playback2D.Core.Query.QuerySide",
        "DemoViewer.NET.Playback2D.Core.Query.QueryToken",
        "DemoViewer.NET.Playback2D.Core.RenderPurpose",
        "DemoViewer.NET.Playback2D.Core.RingState",
        "DemoViewer.NET.Playback2D.Core.Scene2DFrame",
        "DemoViewer.NET.Playback2D.Core.SceneDefaults",
        "DemoViewer.NET.Playback2D.Core.SceneGameInfo",
        "DemoViewer.NET.Playback2D.Core.SceneGuides",
        "DemoViewer.NET.Playback2D.Core.SceneMapInfo",
        "DemoViewer.NET.Playback2D.Core.ScenePalette",
        "DemoViewer.NET.Playback2D.Core.SceneStrokeWidths",
        "DemoViewer.NET.Playback2D.Core.SceneTime",
        "DemoViewer.NET.Playback2D.Core.SceneVision",
        "DemoViewer.NET.Playback2D.Core.ShapedText",
        "DemoViewer.NET.Playback2D.Core.Sightline",
        "DemoViewer.NET.Playback2D.Core.SliceCamera",
        "DemoViewer.NET.Playback2D.Core.TextBlobCache",
        "DemoViewer.NET.Playback2D.Core.Timeline.AnnotationTrack",
        "DemoViewer.NET.Playback2D.Core.Timeline.ITimelineData",
        "DemoViewer.NET.Playback2D.Core.Timeline.ITimelineTrack",
        "DemoViewer.NET.Playback2D.Core.Timeline.TimelineBand",
        "DemoViewer.NET.Playback2D.Core.Timeline.TimelineEventKeys",
        "DemoViewer.NET.Playback2D.Core.Timeline.TimelineEventRecord",
        "DemoViewer.NET.Playback2D.Core.Timeline.TimelineMarker",
        "DemoViewer.NET.Playback2D.Core.Timeline.TimelineMarkerKind",
        "DemoViewer.NET.Playback2D.Core.TokenRouteLine",
        "DemoViewer.NET.Playback2D.Core.Tools.IMapTool",
        "DemoViewer.NET.Playback2D.Core.Tools.IMapToolContext",
        "DemoViewer.NET.Playback2D.Core.Tools.MapToolButton",
        "DemoViewer.NET.Playback2D.Core.Tools.MapToolEvent",
        "DemoViewer.NET.Playback2D.Core.Tools.MapToolModifiers",
        "DemoViewer.NET.Playback2D.Core.TrailGeometry",
        "DemoViewer.NET.Playback2D.Core.Utility.UtilityLanding",
        "DemoViewer.NET.Playback2D.Core.Utility.UtilityMapDocument",
        "DemoViewer.NET.Playback2D.Core.Utility.UtilityThrow",
        "DemoViewer.NET.Playback2D.Core.ViewportTransform",
        "DemoViewer.NET.Playback2D.Core.Vision.ConePolygon",
        "DemoViewer.NET.Playback2D.Core.Vision.IVisionSolver",
        "DemoViewer.NET.Playback2D.Core.Vision.SightlineSegment",
        "DemoViewer.NET.Playback2D.Core.Vision.VisionOptions",
        "DemoViewer.NET.Playback2D.Core.Vision.VisionSolution",
        "DemoViewer.NET.Playback2D.Core.VisionCone",
        "DemoViewer.NET.Playback2D.Core.WorldBounds",
        "DemoViewer.NET.Playback2D.Core.Zones.Bombsite",
        "DemoViewer.NET.Playback2D.Core.Zones.NavPathfinder",
        "DemoViewer.NET.Playback2D.Core.Zones.NavWaypoint",
        "DemoViewer.NET.Playback2D.Core.Zones.PlaceHit",
        "DemoViewer.NET.Playback2D.Core.Zones.PlaceHitKind",
        "DemoViewer.NET.Playback2D.Core.Zones.PlaceOrigin",
        "DemoViewer.NET.Playback2D.Core.Zones.PlaceOutline",
        "DemoViewer.NET.Playback2D.Core.Zones.PlaceResolver",
        "DemoViewer.NET.Playback2D.Core.Zones.VolumeTieRule",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneArea",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneDiagnostic",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneFloor",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneHull",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneOverlayApplier",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneOverlayDocument",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneOverlayMerge",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneOverlayResult",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneOverlayZone",
        "DemoViewer.NET.Playback2D.Core.Zones.ZonePlace",
        "DemoViewer.NET.Playback2D.Core.Zones.ZonePlane",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneSet",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneVolume",
        "DemoViewer.NET.Playback2D.Core.Zones.ZoneVolumeKind",
        .. _keptPipelineNamespaceTypes,
    ];

    // Tooling, export, rendering backends and keyframes: none of it is published.
    private static readonly string[] _unpublishedNamespaces =
    [
        "DemoViewer.NET.Playback2D.Core.Export", "DemoViewer.NET.Playback2D.Core.Keyframes",
        "DemoViewer.NET.Playback2D.Core.Rendering", "DemoViewer.NET.Playback2D.Pipeline"
    ];

    private static SysAssembly Scene => typeof(Scene2DFrame).Assembly;

    private static SysAssembly Core => typeof(RadarLayer).Assembly;

    [Test]
    public async Task Scene_ReferencesOnlySkiaSharpAndBcl() =>
        await Assert.That(DisallowedReferences(Scene, _sceneAllowedPrefixes)).IsEmpty();

    [Test]
    public async Task Core_ReferencesOnlySkiaSharpBclAndTheScene() =>
        await Assert.That(DisallowedReferences(Core, _coreAllowedPrefixes)).IsEmpty();

    [Test]
    public async Task Scene_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(Scene);

    [Test]
    public async Task Core_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(Core);

    [Test]
    public async Task Scene_PublicTypes_AreExactlyThePublishedList()
    {
        string[] exported = [.. Scene.GetExportedTypes().Select(t => t.FullName!).Order(StringComparer.Ordinal)];

        using (Assert.Multiple())
        {
            await Assert.That(exported.Except(_publishedSceneTypes, StringComparer.Ordinal)).IsEmpty()
                .Because("a type made public in the scene contract is published; add it to the list on purpose");
            await Assert.That(_publishedSceneTypes.Except(exported, StringComparer.Ordinal)).IsEmpty()
                .Because("a published type was removed or made internal");
        }
    }

    [Test]
    public async Task Scene_PublishesNothingFromTheToolingNamespaces()
    {
        string[] leaked =
        [
            .. Scene.GetExportedTypes().Select(t => t.FullName!)
                .Where(name => _unpublishedNamespaces.Any(ns => name.StartsWith(ns + ".", StringComparison.Ordinal)))
                .Except(_keptPipelineNamespaceTypes, StringComparer.Ordinal)
        ];

        await Assert.That(leaked).IsEmpty();
    }

    /// <summary>
    ///     The pooled refill path stays first-party: a published frame is immutable to a third party, which
    ///     sees init-only properties and no writable field.
    /// </summary>
    [Test]
    public async Task Scene2DFrame_ExposesNoWritableState()
    {
        FieldInfo[] writableFields =
        [
            .. typeof(Scene2DFrame).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(f => !f.IsInitOnly)
        ];
        string[] setters =
        [
            .. typeof(Scene2DFrame).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true } set
                            && !set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
                .Select(p => p.Name)
        ];

        using (Assert.Multiple())
        {
            await Assert.That(writableFields).IsEmpty();
            await Assert.That(setters).IsEmpty();
        }
    }

    /// <summary>
    ///     Profiling, render counters and the caching policy are first-party benchmarking hooks. A third
    ///     party constructs the compositor with defaults and sees none of them.
    /// </summary>
    [Test]
    public async Task SceneCompositor_PublishesNoMeasurementOrCachingHooks()
    {
        string[] properties =
        [
            .. typeof(SceneCompositor).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(name => name is nameof(SceneCompositor.Profiler) or nameof(SceneCompositor.Stats))
        ];
        int[] constructorArities =
        [
            .. typeof(SceneCompositor).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Select(c => c.GetParameters().Length)
        ];

        using (Assert.Multiple())
        {
            await Assert.That(properties).IsEmpty();
            await Assert.That(constructorArities).IsEquivalentTo([0]);
        }
    }

    [Test]
    public async Task Pipeline_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(typeof(SceneFrameBuilder).Assembly);

    /// <summary>
    ///     Pipeline consumes <c>IPlaybackSnapshot</c> / <c>IPlayerState</c> /
    ///     <c>IReadOnlyEntityView</c> from this assembly, so an Avalonia reference creeping back into it
    ///     would drag Avalonia into every headless consumer: export, the CLI, and CI.
    /// </summary>
    [Test]
    public async Task ModulesAbstractions_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(typeof(IPlayerState).Assembly);

    [Test]
    public async Task Core_DoesNotReferencePipeline()
    {
        string pipeline = typeof(SceneFrameBuilder).Assembly.GetName().Name!;
        bool referenced = Core.GetReferencedAssemblies()
            .Any(a => string.Equals(a.Name, pipeline, StringComparison.Ordinal));

        await Assert.That(referenced).IsFalse();
    }

    private static List<string> DisallowedReferences(SysAssembly assembly, string[] allowedPrefixes) =>
    [
        .. assembly.GetReferencedAssemblies().Select(r => r.Name ?? "")
            .Where(name => !allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
    ];

    private static async Task AssertNoAvaloniaIn(SysAssembly root)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        List<string> offenders = [];
        Walk(root, visited, offenders);

        await Assert.That(offenders).IsEmpty();
    }

    private static void Walk(SysAssembly assembly, HashSet<string> visited, List<string> offenders)
    {
        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            string name = reference.Name ?? "";
            if (!visited.Add(name))
            {
                continue;
            }

            if (name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{assembly.GetName().Name} -> {name}");
                continue;
            }

            SysAssembly loaded;
            try
            {
                loaded = SysAssembly.Load(reference);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // A reference this test host cannot resolve cannot be Avalonia's parent either: the name
                // check above already ran, and an unresolvable assembly contributes no further edges.
                continue;
            }

            Walk(loaded, visited, offenders);
        }
    }
}
