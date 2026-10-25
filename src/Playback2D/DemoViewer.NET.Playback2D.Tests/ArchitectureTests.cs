#region

using System.Reflection;
using System.Text.RegularExpressions;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
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

    // Published types that kept the namespace they had in Pipeline: the demo content key, the sidecar
    // identity records, the zone pipeline and its result. Nothing else in a Pipeline namespace is published.
    private static readonly string[] _keptPipelineNamespaceTypes =
    [
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.ClockIdentity",
        "DemoViewer.NET.Playback2D.Pipeline.Annotations.DemoIdentity",
        "DemoViewer.NET.Playback2D.Pipeline.Assets.ZoneAssetPipeline",
        "DemoViewer.NET.Playback2D.Pipeline.Assets.ZoneLoadResult",
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
        "DemoViewer.NET.Playback2D.Core.Input.ToolModifiers",
        "DemoViewer.NET.Playback2D.Core.Input.ToolPointerButton",
        "DemoViewer.NET.Playback2D.Core.Input.ToolPointerEvent",
        "DemoViewer.NET.Playback2D.Core.Input.ToolWheelEvent",
        "DemoViewer.NET.Playback2D.Core.KillFeedRow",
        "DemoViewer.NET.Playback2D.Core.Layers.OverlayHeatmapLayer",
        "DemoViewer.NET.Playback2D.Core.Layers.QueryTokenLayer",
        "DemoViewer.NET.Playback2D.Core.Layers.SceneLayerIds",
        "DemoViewer.NET.Playback2D.Core.Levels.FloorSlice",
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
    // The ex-Core namespaces, which the Core/Pipeline merge left intact inside Pipeline. Listed one by
    // one rather than matched on a ".Core." prefix so a namespace rename cannot make the guard below
    // match nothing and pass: ExCoreNamespaces_AllStillExist fails and names the entry to re-key.
    private static readonly string[] _exCoreNamespaces =
    [
        "DemoViewer.NET.Playback2D.Core.Annotations",
        "DemoViewer.NET.Playback2D.Core.Cameras",
        "DemoViewer.NET.Playback2D.Core.Export",
        "DemoViewer.NET.Playback2D.Core.Ink",
        "DemoViewer.NET.Playback2D.Core.Input",
        "DemoViewer.NET.Playback2D.Core.Layers",
        "DemoViewer.NET.Playback2D.Core.Levels",
        "DemoViewer.NET.Playback2D.Core.Rendering",
        "DemoViewer.NET.Playback2D.Core.Rendering.Interop",
        "DemoViewer.NET.Playback2D.Core.Timeline"
    ];

    // What the render code may touch. An allow-list rather than a list of banned names, so a dependency
    // nobody has thought of yet is refused by default: CS2OpenDev arriving under a new name would pass a
    // denylist. Pipeline itself is absent on purpose, which is what stops the one-hop bypass, a Core type
    // calling a Pipeline type that wraps the parser.
    private static readonly string[] _exCoreAllowedAssemblies =
    [
        .. _sceneAllowedPrefixes, "DemoViewer.NET.Playback2D.Scene"
    ];

    // Roots the ex-Core sources must not name. The allow-list above cannot see a call inside a method
    // body, because reflection reads signatures; this half reads the sources, so between them a field,
    // a signature, a generic argument and a call are all covered. DemoViewer.NET.Playback2D.Pipeline. is
    // here for the one-hop, with the trailing dot so the assembly's own name in a comment-free using
    // does not count.
    private static readonly string[] _exCoreForbiddenRoots =
    [
        "CS2DemoKit", "CS2OpenDev", "FFMpegCore", "Avalonia",
        "DemoViewer.NET.Modules.Abstractions", "DemoViewer.NET.Playback2D.Pipeline."
    ];

    private static readonly string[] _unpublishedNamespaces =
    [
        "DemoViewer.NET.Playback2D.Core.Export", "DemoViewer.NET.Playback2D.Core.Keyframes",
        "DemoViewer.NET.Playback2D.Core.Rendering", "DemoViewer.NET.Playback2D.Pipeline"
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
            await Assert.That(constructorArities).IsEquivalentTo([0], CollectionOrdering.Matching);
        }
    }

    /// <summary>
    ///     Every entry in <see cref="_exCoreNamespaces" /> still names types. The guards below are keyed on
    ///     that list, so a rename that empties one would silently stop checking it.
    /// </summary>
    [Test]
    public async Task ExCoreNamespaces_AllStillExist()
    {
        List<string> empty = [.. _exCoreNamespaces.Where(ns => !Pipeline.GetTypes().Any(t => t.Namespace == ns))];

        await Assert.That(empty).IsEmpty()
            .Because("an ex-Core namespace was renamed or emptied; re-key the arch guards on its new name in the same commit");
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
        foreach (Type type in Pipeline.GetTypes().Where(t => _exCoreNamespaces.Contains(t.Namespace, StringComparer.Ordinal)))
        {
            foreach (Type referenced in SurfaceTypes(type))
            {
                string name = referenced.Assembly.GetName().Name ?? "";
                if (name == PipelineAssemblyName
                    && _exCoreNamespaces.Contains(referenced.Namespace, StringComparer.Ordinal))
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
    ///     reads signatures, so a parser call in a body leaves no trace in the surface above.
    /// </summary>
    [Test]
    public async Task ExCoreSources_NameNoneOfTheForbiddenRoots()
    {
        string repoRoot = TestTiers.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");
        string pipelineRoot = Path.Combine(repoRoot, "src", "Playback2D", "DemoViewer.NET.Playback2D.Pipeline");

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

            foreach (string root in _exCoreForbiddenRoots.Where(r => code.Contains(r, StringComparison.Ordinal)))
            {
                offenders.Add($"{Path.GetRelativePath(repoRoot, file).Replace('\\', '/')} -> {root}");
            }
        }

        await Assert.That(offenders.Order(StringComparer.Ordinal).ToList()).IsEmpty()
            .Because("an ex-Core source names the parser, the encoder, Avalonia, the module abstractions or a Pipeline namespace");
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
