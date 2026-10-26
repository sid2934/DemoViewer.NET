#region

using System.Reflection;
using System.Text.RegularExpressions;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Pipeline;
using SysAssembly = System.Reflection.Assembly;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The layering rules that make the scene contract a runtime instead of a UI helper, and its published
///     surface. These are asserted against assembly metadata rather than csproj text, because a transitive
///     edge added three projects away is exactly the kind of regression a reference-graph reading of the
///     csproj misses.
/// </summary>
public class ArchitectureTests
{
    // Everything the scene contract is allowed to reference directly. SkiaSharp plus the BCL, and nothing else.
    private static readonly string[] _sceneAllowedPrefixes =
    [
        "SkiaSharp", "System", "netstandard", "mscorlib", "Microsoft.CSharp"
    ];

    // The scene contract's public types: what DemoViewer.NET.Playback2D.Scene promises to third parties.
    // A type joins or leaves this list on purpose, never as a side effect of moving code between assemblies.
    private static readonly string[] _publishedSceneTypes =
    [
        "DemoViewer.NET.Playback2D.Annotations.AnnotationDocument",
        "DemoViewer.NET.Playback2D.Annotations.AnnotationElement",
        "DemoViewer.NET.Playback2D.Annotations.AnnotationKind",
        "DemoViewer.NET.Playback2D.Annotations.AnnotationSession",
        "DemoViewer.NET.Playback2D.Annotations.AnnotationStyle",
        "DemoViewer.NET.Playback2D.Annotations.ClockIdentity",
        "DemoViewer.NET.Playback2D.Annotations.DemoIdentity",
        "DemoViewer.NET.Playback2D.Annotations.DocDelta",
        "DemoViewer.NET.Playback2D.Annotations.DocDelta+Add",
        "DemoViewer.NET.Playback2D.Annotations.DocDelta+Batch",
        "DemoViewer.NET.Playback2D.Annotations.DocDelta+Remove",
        "DemoViewer.NET.Playback2D.Annotations.DocDelta+Replace",
        "DemoViewer.NET.Playback2D.Annotations.EnvelopeMode",
        "DemoViewer.NET.Playback2D.Annotations.InkPoint",
        "DemoViewer.NET.Playback2D.Annotations.SpaceRef",
        "DemoViewer.NET.Playback2D.Annotations.SpaceRef+Entity",
        "DemoViewer.NET.Playback2D.Annotations.SpaceRef+World",
        "DemoViewer.NET.Playback2D.Annotations.StrokeTiming",
        "DemoViewer.NET.Playback2D.Annotations.TimeEnvelope",
        "DemoViewer.NET.Playback2D.Annotations.TimingRun",
        "DemoViewer.NET.Playback2D.AreaEffect",
        "DemoViewer.NET.Playback2D.AreaEffectKind",
        "DemoViewer.NET.Playback2D.Assets.ZoneAssetPipeline",
        "DemoViewer.NET.Playback2D.Assets.ZoneLoadResult",
        "DemoViewer.NET.Playback2D.BombMarker",
        "DemoViewer.NET.Playback2D.Cameras.FitAliveRig",
        "DemoViewer.NET.Playback2D.Cameras.FitMapRig",
        "DemoViewer.NET.Playback2D.Cameras.FollowPlayerRig",
        "DemoViewer.NET.Playback2D.Cameras.ICameraRig",
        "DemoViewer.NET.Playback2D.Cameras.ManualRig",
        "DemoViewer.NET.Playback2D.Compositing.ISceneLayer",
        "DemoViewer.NET.Playback2D.Compositing.LayerCacheHint",
        "DemoViewer.NET.Playback2D.Compositing.LayerSlot",
        "DemoViewer.NET.Playback2D.Compositing.SceneCompositor",
        "DemoViewer.NET.Playback2D.Compositing.SceneRenderContext",
        "DemoViewer.NET.Playback2D.ConePoint",
        "DemoViewer.NET.Playback2D.DemoContentHash",
        "DemoViewer.NET.Playback2D.GrenadeKind",
        "DemoViewer.NET.Playback2D.GrenadeTrail",
        "DemoViewer.NET.Playback2D.GrenadeTrailPoint",
        "DemoViewer.NET.Playback2D.GuidePin",
        "DemoViewer.NET.Playback2D.GuideToken",
        "DemoViewer.NET.Playback2D.Hud.HudPlayerRow",
        "DemoViewer.NET.Playback2D.Hud.HudSnapshot",
        "DemoViewer.NET.Playback2D.Hud.HudStyle",
        "DemoViewer.NET.Playback2D.Hud.IHudDataSource",
        "DemoViewer.NET.Playback2D.Hud.IIconSource",
        "DemoViewer.NET.Playback2D.ISmoothedPositionSource",
        "DemoViewer.NET.Playback2D.Input.IPointerTool",
        "DemoViewer.NET.Playback2D.Input.ITokenEditor",
        "DemoViewer.NET.Playback2D.Input.IToolServices",
        "DemoViewer.NET.Playback2D.Input.TokenGrip",
        "DemoViewer.NET.Playback2D.Input.TokenHitTest",
        "DemoViewer.NET.Playback2D.Input.ToolKind",
        "DemoViewer.NET.Playback2D.Input.ToolModifiers",
        "DemoViewer.NET.Playback2D.Input.ToolPointerButton",
        "DemoViewer.NET.Playback2D.Input.ToolPointerEvent",
        "DemoViewer.NET.Playback2D.Input.ToolWheelEvent",
        "DemoViewer.NET.Playback2D.KillFeedRow",
        "DemoViewer.NET.Playback2D.Layers.OverlayHeatmapLayer",
        "DemoViewer.NET.Playback2D.Layers.QueryTokenLayer",
        "DemoViewer.NET.Playback2D.Layers.SceneLayerIds",
        "DemoViewer.NET.Playback2D.Levels.FloorSlice",
        "DemoViewer.NET.Playback2D.Levels.ILevelLayoutPolicy",
        "DemoViewer.NET.Playback2D.Levels.ILevelRadarBinder",
        "DemoViewer.NET.Playback2D.Levels.IMapAsset",
        "DemoViewer.NET.Playback2D.Levels.LevelCrossingTracker",
        "DemoViewer.NET.Playback2D.Levels.LevelDisplayMode",
        "DemoViewer.NET.Playback2D.Levels.LevelHysteresis",
        "DemoViewer.NET.Playback2D.Levels.LevelHysteresisOptions",
        "DemoViewer.NET.Playback2D.Levels.LevelLayouts",
        "DemoViewer.NET.Playback2D.Levels.LevelPane",
        "DemoViewer.NET.Playback2D.Levels.LevelPaneSnapshot",
        "DemoViewer.NET.Playback2D.Levels.LevelSetChange",
        "DemoViewer.NET.Playback2D.Levels.MapLevel",
        "DemoViewer.NET.Playback2D.Levels.MapLevelId",
        "DemoViewer.NET.Playback2D.Levels.MapSpace",
        "DemoViewer.NET.Playback2D.Levels.MapSpaceFactory",
        "DemoViewer.NET.Playback2D.Levels.PaneSet",
        "DemoViewer.NET.Playback2D.Levels.RadarBindingQuality",
        "DemoViewer.NET.Playback2D.Levels.SingleLayout",
        "DemoViewer.NET.Playback2D.Levels.StackedLayout",
        "DemoViewer.NET.Playback2D.MapRadarImage",
        "DemoViewer.NET.Playback2D.MarkerSmoother",
        "DemoViewer.NET.Playback2D.Overlay.OverlayDocument",
        "DemoViewer.NET.Playback2D.Overlay.OverlayPoint",
        "DemoViewer.NET.Playback2D.PlayerMarker",
        "DemoViewer.NET.Playback2D.Query.QueryCanvasDocument",
        "DemoViewer.NET.Playback2D.Query.QuerySide",
        "DemoViewer.NET.Playback2D.Query.QueryToken",
        "DemoViewer.NET.Playback2D.RenderPurpose",
        "DemoViewer.NET.Playback2D.RingState",
        "DemoViewer.NET.Playback2D.Scene2DFrame",
        "DemoViewer.NET.Playback2D.SceneDefaults",
        "DemoViewer.NET.Playback2D.SceneGameInfo",
        "DemoViewer.NET.Playback2D.SceneGuides",
        "DemoViewer.NET.Playback2D.SceneMapInfo",
        "DemoViewer.NET.Playback2D.ScenePalette",
        "DemoViewer.NET.Playback2D.SceneStrokeWidths",
        "DemoViewer.NET.Playback2D.SceneTime",
        "DemoViewer.NET.Playback2D.SceneVision",
        "DemoViewer.NET.Playback2D.ShapedText",
        "DemoViewer.NET.Playback2D.Sightline",
        "DemoViewer.NET.Playback2D.SliceCamera",
        "DemoViewer.NET.Playback2D.TextBlobCache",
        "DemoViewer.NET.Playback2D.Timeline.AnnotationTrack",
        "DemoViewer.NET.Playback2D.Timeline.ITimelineData",
        "DemoViewer.NET.Playback2D.Timeline.ITimelineTrack",
        "DemoViewer.NET.Playback2D.Timeline.TimelineBand",
        "DemoViewer.NET.Playback2D.Timeline.TimelineEventKeys",
        "DemoViewer.NET.Playback2D.Timeline.TimelineEventRecord",
        "DemoViewer.NET.Playback2D.Timeline.TimelineMarker",
        "DemoViewer.NET.Playback2D.Timeline.TimelineMarkerKind",
        "DemoViewer.NET.Playback2D.TokenRouteLine",
        "DemoViewer.NET.Playback2D.Tools.IMapTool",
        "DemoViewer.NET.Playback2D.Tools.IMapToolContext",
        "DemoViewer.NET.Playback2D.Tools.MapToolButton",
        "DemoViewer.NET.Playback2D.Tools.MapToolEvent",
        "DemoViewer.NET.Playback2D.Tools.MapToolModifiers",
        "DemoViewer.NET.Playback2D.TrailGeometry",
        "DemoViewer.NET.Playback2D.ViewportTransform",
        "DemoViewer.NET.Playback2D.Vision.ConePolygon",
        "DemoViewer.NET.Playback2D.Vision.IVisionSolver",
        "DemoViewer.NET.Playback2D.Vision.SightlineSegment",
        "DemoViewer.NET.Playback2D.Vision.VisionOptions",
        "DemoViewer.NET.Playback2D.Vision.VisionSolution",
        "DemoViewer.NET.Playback2D.VisionCone",
        "DemoViewer.NET.Playback2D.WorldBounds",
        "DemoViewer.NET.Playback2D.Zones.Bombsite",
        "DemoViewer.NET.Playback2D.Zones.NavPathfinder",
        "DemoViewer.NET.Playback2D.Zones.NavWaypoint",
        "DemoViewer.NET.Playback2D.Zones.PlaceHit",
        "DemoViewer.NET.Playback2D.Zones.PlaceHitKind",
        "DemoViewer.NET.Playback2D.Zones.PlaceOrigin",
        "DemoViewer.NET.Playback2D.Zones.PlaceOutline",
        "DemoViewer.NET.Playback2D.Zones.PlaceResolver",
        "DemoViewer.NET.Playback2D.Zones.ZoneArea",
        "DemoViewer.NET.Playback2D.Zones.ZoneDiagnostic",
        "DemoViewer.NET.Playback2D.Zones.ZoneFloor",
        "DemoViewer.NET.Playback2D.Zones.ZoneOverlayApplier",
        "DemoViewer.NET.Playback2D.Zones.ZoneOverlayDocument",
        "DemoViewer.NET.Playback2D.Zones.ZoneOverlayMerge",
        "DemoViewer.NET.Playback2D.Zones.ZoneOverlayResult",
        "DemoViewer.NET.Playback2D.Zones.ZoneOverlayZone",
        "DemoViewer.NET.Playback2D.Zones.ZonePlace",
        "DemoViewer.NET.Playback2D.Zones.ZoneSet",
        "DemoViewer.NET.Playback2D.Zones.ZoneVolume",
        "DemoViewer.NET.Playback2D.Zones.ZoneVolumeKind",
    ];

    // The ex-Core namespaces, under the names they took when .Core was dropped from Pipeline. Listed
    // one by one rather than matched on a prefix so a namespace rename cannot make the guards below
    // match nothing and pass: ExCoreNamespaces_AllStillExist fails and names the entry to re-key.
    private static readonly string[] _exCoreNamespaces =
    [
        "DemoViewer.NET.Playback2D.Pipeline.Annotations",
        "DemoViewer.NET.Playback2D.Pipeline.Cameras",
        "DemoViewer.NET.Playback2D.Pipeline.Export",
        "DemoViewer.NET.Playback2D.Pipeline.Ink",
        "DemoViewer.NET.Playback2D.Pipeline.Input",
        "DemoViewer.NET.Playback2D.Pipeline.Layers",
        "DemoViewer.NET.Playback2D.Pipeline.Levels",
        "DemoViewer.NET.Playback2D.Pipeline.Rendering",
        "DemoViewer.NET.Playback2D.Pipeline.Rendering.Interop",
        "DemoViewer.NET.Playback2D.Pipeline.Timeline"
    ];

    // Pipeline's own types in the two namespaces that absorbed ex-Core code when .Core was dropped:
    // Annotations took the sidecar store and its DTOs, Export took the sinks, the session and the app's
    // render request. They sit in an ex-Core namespace without being ex-Core, and they reach ffmpeg and
    // the parser legitimately, so the namespace alone can no longer decide membership. A type added to
    // either namespace counts as ex-Core until it is listed here: the guards refuse it by default.
    private static readonly string[] _mergedNamespaceOwnTypes =
    [
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationDocumentDto",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationElementDto",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationJsonContext",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationLoadResult",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationStore",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationStoreLocation",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationTimingDto",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.ClockIdentityDto",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.DemoIdentityDto",
        "DemoViewer.NET.Playback2D.Pipeline.Export.ChannelVideoFrameSource",
        "DemoViewer.NET.Playback2D.Pipeline.Export.ExportClock",
        "DemoViewer.NET.Playback2D.Pipeline.Export.ExportOutputPath",
        "DemoViewer.NET.Playback2D.Pipeline.Export.FfmpegEncodeException",
        "DemoViewer.NET.Playback2D.Pipeline.Export.FfmpegFrameSink",
        "DemoViewer.NET.Playback2D.Pipeline.Export.FfmpegSinkOptions",
        "DemoViewer.NET.Playback2D.Pipeline.Export.HashingFrameSink",
        "DemoViewer.NET.Playback2D.Pipeline.Export.ManagedGifSink",
        "DemoViewer.NET.Playback2D.Pipeline.Export.PooledRgbaFrame",
        "DemoViewer.NET.Playback2D.Pipeline.Export.Scene2DExportRequest",
        "DemoViewer.NET.Playback2D.Pipeline.Export.SceneExportSession",
    ];

    // What the render code may touch. An allow-list rather than a list of banned names, so a dependency
    // nobody has thought of yet is refused by default: CS2OpenDev arriving under a new name would pass a
    // denylist. Pipeline itself is absent on purpose, which is what stops the one-hop bypass, an ex-Core
    // type calling a Pipeline type that wraps the parser.
    private static readonly string[] _exCoreAllowedAssemblies =
    [
        .. _sceneAllowedPrefixes, "DemoViewer.NET.Playback2D.Scene"
    ];

    // Roots the ex-Core sources must not name. The allow-list above cannot see a call inside a method
    // body, because reflection reads signatures; this half reads the sources, so between them a field,
    // a signature, a generic argument and a call are all covered. The one-hop is not in this list: ex-Core
    // code now sits under DemoViewer.NET.Playback2D.Pipeline.*, so that string is in every ex-Core file's
    // own namespace line, and enclosing-namespace lookup reaches SceneFrameBuilder with no import at all.
    // ExCoreSources_NameNoneOfTheForbiddenRoots matches Pipeline's own type names instead.
    private static readonly string[] _exCoreForbiddenRoots =
    [
        "CS2DemoKit", "CS2OpenDev", "FFMpegCore", "Avalonia",
        "DemoViewer.NET.Modules.Abstractions"
    ];

    // Tooling, export, rendering backends and keyframes: none of it is published. The first three carry
    // the SCENE naming, because that is what a tooling type moved into this assembly would be given now;
    // under the Pipeline naming they would each be subsumed by the fourth entry and never fire again. The
    // fourth keeps the Pipeline root, for a file moved across verbatim, and must not be Scene-mapped:
    // DemoViewer.NET.Playback2D is the root of every Scene type.
    private static readonly string[] _unpublishedNamespaces =
    [
        "DemoViewer.NET.Playback2D.Export", "DemoViewer.NET.Playback2D.Keyframes",
        "DemoViewer.NET.Playback2D.Rendering", "DemoViewer.NET.Playback2D.Pipeline"
    ];

    private static SysAssembly Scene => typeof(Scene2DFrame).Assembly;

    // The same assembly Pipeline_TransitiveClosure_ContainsNoAvalonia reads, named once.
    private static SysAssembly Pipeline => typeof(SceneFrameBuilder).Assembly;

    [Test]
    public async Task Scene_ReferencesOnlySkiaSharpAndBcl() =>
        await Assert.That(DisallowedReferences(Scene, _sceneAllowedPrefixes)).IsEmpty();

    [Test]
    public async Task Scene_TransitiveClosure_ContainsNoAvalonia() =>
        await AssertNoAvaloniaIn(Scene);

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
            await Assert.That(constructorArities).IsEquivalentTo([0], CollectionOrdering.Matching);
        }
    }

    /// <summary>
    ///     The layer ids must not be constants. A const is copied into every assembly that reads it, so
    ///     an extension compiled against one keeps passing the old string after an id changes here: the
    ///     layer never registers, and the consumer holds no reference to this type for a binding test to
    ///     notice. Verified on the real extension when the ids were converted: StratBook.dll went from
    ///     carrying its own copies of the literals to carrying none and a reference to SceneLayerIds.
    ///     This is the guard against someone tidying them back.
    /// </summary>
    [Test]
    public async Task SceneLayerIds_AreNotConstants()
    {
        FieldInfo[] fields = typeof(SceneLayerIds).GetFields(BindingFlags.Public | BindingFlags.Static);
        List<string> literals = [.. fields.Where(f => f.IsLiteral).Select(f => f.Name).Order(StringComparer.Ordinal)];

        using (Assert.Multiple())
        {
            await Assert.That(fields.Length).IsGreaterThan(0).Because("reflection found no ids, so the check below is vacuous");
            await Assert.That(literals).IsEmpty()
                .Because("a const id is inlined into every consumer, so an extension keeps the old string after a rename");
        }
    }

    /// <summary>
    ///     Every entry in <see cref="_exCoreNamespaces" /> still names types, and every entry in
    ///     <see cref="_mergedNamespaceOwnTypes" /> still names one. The guards below are keyed on both
    ///     lists, so a rename that empties an entry would silently stop checking it.
    /// </summary>
    [Test]
    public async Task ExCoreNamespaces_AllStillExist()
    {
        Type[] all = Pipeline.GetTypes();
        List<string> empty = [.. _exCoreNamespaces.Where(ns => !all.Any(t => t.Namespace == ns))];
        List<string> gone = [.. _mergedNamespaceOwnTypes.Where(n => !all.Any(t => t.FullName == n))];

        using (Assert.Multiple())
        {
            await Assert.That(empty).IsEmpty()
                .Because("an ex-Core namespace was renamed or emptied; re-key the arch guards on its new name in the same commit");
            await Assert.That(gone).IsEmpty()
                .Because("a type excluded from the ex-Core guards was renamed or moved; re-key the entry in the same commit");
        }
    }

    /// <summary>
    ///     Restores, as a namespace rule, the one guard the Core/Pipeline merge dropped. Core used to be its
    ///     own assembly with a reference allow-list, which is what kept the render layers off the parser, the
    ///     analysis layer and the encoder. Merging it into Pipeline deleted that, and Pipeline references all
    ///     of them because its frame sources and its export path legitimately need them. So the rule cannot
    ///     be an allow-list on Pipeline: it is an allow-list on the types still in the ex-Core namespaces.
    ///     <para>
    ///         Reflection half: every assembly reachable from those types' base types, interfaces, generic
    ///         arguments, fields, signatures and attributes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ExCoreTypes_TouchOnlySkiaSharpBclAndTheScene()
    {
        List<string> offenders = [];
        foreach (Type type in Pipeline.GetTypes().Where(IsExCore))
        {
            foreach (Type referenced in SurfaceTypes(type))
            {
                string name = referenced.Assembly.GetName().Name ?? "";
                if (name == PipelineAssemblyName && IsExCore(referenced))
                {
                    continue;
                }

                if (!_exCoreAllowedAssemblies.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.FullName} -> {referenced.FullName} ({name})");
                }
            }
        }

        await Assert.That(offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()).IsEmpty()
            .Because("a type in an ex-Core namespace reached outside SkiaSharp, the BCL and the scene contract");
    }

    /// <summary>
    ///     The source half of the same rule, which is what catches a call inside a method body: reflection
    ///     reads signatures, so a parser call in a body leaves no trace in the surface above. It also carries
    ///     the one-hop rule, which has no namespace left to match on: ex-Core code is under
    ///     DemoViewer.NET.Playback2D.Pipeline.* now, so enclosing-namespace lookup resolves SceneFrameBuilder
    ///     and the rest of Pipeline with no import and no qualification. The simple names stand in for it.
    /// </summary>
    [Test]
    public async Task ExCoreSources_NameNoneOfTheForbiddenRoots()
    {
        string repoRoot = TestTiers.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");
        string pipelineRoot = Path.Combine(repoRoot, "src", "Playback2D", "DemoViewer.NET.Playback2D.Pipeline");

        // Derived, not hand-listed: a Pipeline type added outside the ex-Core namespaces is covered the
        // moment it is written. Generic arity is stripped so Foo<T> is matched as Foo. A name an ex-Core
        // type also carries is dropped, because an ex-Core file naming its own sibling is not a hop.
        HashSet<string> exCoreNames = [.. Pipeline.GetTypes().Where(IsExCore).Select(BareName)];
        string[] forbiddenNames =
        [
            .. Pipeline.GetTypes().Where(t => !t.IsNested && !IsExCore(t)).Select(BareName)
                .Where(n => n.Length > 0 && !n.Contains('<') && !exCoreNames.Contains(n))
                .Distinct(StringComparer.Ordinal)
        ];
        string[] ownTypeNames = [.. _mergedNamespaceOwnTypes.Select(n => n[(n.LastIndexOf('.') + 1)..])];

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(pipelineRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            Match declared = Regex.Match(text, @"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline);
            if (!declared.Success || !_exCoreNamespaces.Contains(declared.Groups[1].Value, StringComparer.Ordinal))
            {
                continue;
            }

            // Comments are stripped first. VisionLayer's own summary explains that VisibilityEngine is a
            // CS2DemoKit type and that IVisionSolver exists to keep it out, which is the opposite of a
            // violation and would otherwise be read as one.
            string code = Regex.Replace(text, @"//.*?$", "", RegexOptions.Multiline);
            code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);

            // A file in one of the two merged namespaces that declares one of Pipeline's own types is not
            // ex-Core source; see _mergedNamespaceOwnTypes. Read off the stripped code, so a doc comment
            // that merely names one of them cannot skip a real ex-Core file.
            if (ownTypeNames.Any(n => Regex.IsMatch(code, $@"\b(?:class|record|struct|interface|enum)\s+{n}\b")))
            {
                continue;
            }

            string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            foreach (string root in _exCoreForbiddenRoots.Where(r => code.Contains(r, StringComparison.Ordinal)))
            {
                offenders.Add($"{relative} -> {root}");
            }

            foreach (string name in forbiddenNames.Where(n => Regex.IsMatch(code, $@"\b{n}\b")))
            {
                offenders.Add($"{relative} -> {name} (Pipeline type, outside the ex-Core namespaces)");
            }
        }

        await Assert.That(offenders.Order(StringComparer.Ordinal).ToList()).IsEmpty()
            .Because("an ex-Core source names the parser, the encoder, Avalonia, the module abstractions or a Pipeline type outside the ex-Core namespaces");
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

    private static string PipelineAssemblyName => Pipeline.GetName().Name ?? "";

    // An ex-Core namespace minus the types Pipeline already had there. A nested type follows the type it
    // is declared in, which is what keeps FfmpegFrameSink.StubFrame out with its parent.
    private static bool IsExCore(Type type)
    {
        Type outer = type;
        while (outer.DeclaringType is { } parent)
        {
            outer = parent;
        }

        return _exCoreNamespaces.Contains(outer.Namespace, StringComparer.Ordinal)
               && !_mergedNamespaceOwnTypes.Contains(outer.FullName, StringComparer.Ordinal);
    }

    // A type's simple name without generic arity: List`1 reads as List.
    private static string BareName(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    // Every type a type's own surface names: base, interfaces, fields, signatures, attributes, and the
    // generic arguments of each, recursively. new List<ParsedDemo>() declares on CoreLib, so without the
    // recursion the interesting type is the one that gets missed.
    private static List<Type> SurfaceTypes(Type type)
    {
        List<Type> found = [];
        void Add(Type? t)
        {
            if (t is null)
            {
                return;
            }

            Type bare = t.IsByRef || t.IsPointer || t.IsArray ? t.GetElementType() ?? t : t;
            if (bare.IsGenericParameter)
            {
                return;
            }

            found.Add(bare);
            foreach (Type argument in bare.IsGenericType ? bare.GetGenericArguments() : [])
            {
                Add(argument);
            }
        }

        Add(type.BaseType);
        foreach (Type i in type.GetInterfaces())
        {
            Add(i);
        }

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (FieldInfo f in type.GetFields(All))
        {
            Add(f.FieldType);
        }

        foreach (PropertyInfo p in type.GetProperties(All))
        {
            Add(p.PropertyType);
        }

        foreach (EventInfo e in type.GetEvents(All))
        {
            Add(e.EventHandlerType);
        }

        foreach (MethodBase m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            Add((m as MethodInfo)?.ReturnType);
            foreach (ParameterInfo parameter in m.GetParameters())
            {
                Add(parameter.ParameterType);
            }
        }

        foreach (CustomAttributeData a in type.GetCustomAttributesData())
        {
            Add(a.AttributeType);
        }

        return found;
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
