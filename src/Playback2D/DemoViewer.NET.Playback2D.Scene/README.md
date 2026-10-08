# DemoViewer.NET.Playback2D.Scene

The scene contract of DemoViewer.NET's 2D map: what one frame holds, how a layer draws, how the map's floors
and panes are laid out, how a world point maps to the screen and to a named place. SkiaSharp and the BCL
only, no UI framework. It is versioned with `DemoViewer.NET.Extensions.Sdk`.

```xml
<ItemGroup>
  <PackageReference Include="DemoViewer.NET.Extensions.Sdk" Version="1.1.*" />
  <PackageReference Include="DemoViewer.NET.Playback2D.Scene" Version="1.1.*" />
</ItemGroup>
```

The app ships this assembly and SkiaSharp, so copies in your extension's folder are never loaded. Build
against the SkiaSharp version this package depends on.

The types keep the namespaces the app uses for them: most are under `DemoViewer.NET.Playback2D.Core`, and the
zone loaders, the sidecar identity records and the content key under `DemoViewer.NET.Playback2D.Pipeline`.

## What is in it

| Namespace | Types |
|---|---|
| `Playback2D.Core` | `Scene2DFrame` and what it carries (`PlayerMarker`, `GrenadeTrail`, `AreaEffect`, `BombMarker`, `KillFeedRow`, `SceneGameInfo`, `SceneMapInfo`, `SceneVision`), `SceneTime`, `ViewportTransform`, `SliceCamera`, `ScenePalette`, `WorldBounds`, `TextBlobCache` |
| `Playback2D.Core.Compositing` | `ISceneLayer`, `LayerSlot`, `LayerCacheHint`, `SceneRenderContext`, `SceneSubmission`, `SceneCompositor`, `SceneRenderGate` |
| `Playback2D.Core.Layers` | `SceneLayerIds`, the ids of the app's own layers |
| `Playback2D.Core.Levels` | `MapSpace`, `MapLevel`, `MapLevelId`, `PaneSet`, `LevelPane`, `StackedLayout`, `SingleLayout`, `IMapAsset` (a loaded map's floors, radar images and places) |
| `Playback2D.Core.Zones` | `PlaceResolver`, `ZoneSet`, `PlaceHit` and the zone geometry |
| `Playback2D.Core.Cameras` | `ICameraRig` and the built-in rigs |
| `Playback2D.Core.Tools` | `IMapTool`, `MapToolEvent`, `IMapToolContext`, `MapToolButton`, `MapToolModifiers` |
| `Playback2D.Core.Layers` (drawing) | `OverlayHeatmapLayer`, `QueryTokenLayer`, `UtilityMapLayer` |
| `Playback2D.Core.Overlay`, `.Query`, `.Utility` | the documents those three layers draw |
| `Playback2D.Core.Annotations` | `AnnotationDocument`, `AnnotationElement`, `AnnotationSession`, `DocDelta` and the styles and timing they carry |
| `Playback2D.Core.Timeline` | `ITimelineTrack`, `ITimelineData`, `TimelineMarker`, `TimelineBand`, `AnnotationTrack` |
| `Playback2D.Core.Input` | `IPointerTool`, `ToolPointerEvent`, `IToolServices`, `ITokenEditor`: the playback tab's own tool contract, which a tool that edits tokens or annotations needs |
| `Playback2D.Core.Hud` | `IHudDataSource`, `HudSnapshot`, `IIconSource` |
| `Playback2D.Core.Vision` | `IVisionSolver`, `VisionSolution` |
| `Playback2D.Core.Zones` (overlays) | `ZoneOverlayDocument`, `ZoneOverlayApplier`: a user's zone edits over the baked set |
| `Playback2D.Pipeline.Assets` | `ZoneAssetPipeline`, `ZoneSetReader`, `ZoneOverlayReader`: a map's places loaded from its bundle directory |
| `Playback2D.Pipeline.Annotations` | `DemoIdentity`, `ClockIdentity`: which demo and which parse a sidecar was written against |
| `Playback2D.Pipeline` | `DemoContentHash`, the demo content key every store joins on |
| `Playback2D.Core` (more) | `MarkerSmoother`, `SceneGuides`, `TrailGeometry`, `SceneDefaults`; `Levels.MapSpaceFactory`; `Zones.NavPathfinder` |

The app's own layers (radar, markers, roster and the rest), the drawing tools, video export, keyframes and the
render backends are not published. `IMapTool` is the contract to write a new tool against; `IPointerTool`
is published for tools that hold the playback tab's token editor or annotation session.

## Frames and layers

A `Scene2DFrame` is valid only for the call it is handed to: the app refills frames in place, so never keep
one. Its properties are init-only, which lets a test or a fixture build one with an object initializer.

An `ISceneLayer` does its work in two steps. `Advance` runs on the UI thread before each frame is submitted
and is the only place a layer may change its own state. `Render` draws one pane onto the `SKCanvas` it is
given, already clipped and translated, using only the `SceneRenderContext`: `Transform` maps world to pane
pixels, `BelongsHere(z)` says whether a world height belongs on the pane's floor, and `Palette` has the
theme's colours. `Render` runs once per pane and may run on the render thread, so it must not change
anything. Layer ids beginning `playback2d.` or `hud.` are the app's.

## Tools

An `IMapTool` handles a pointer gesture on a map. `OnPressed` decides: return true to own the gesture, and the
tool then sees each `OnMoved` and the `OnReleased`, or `OnCancelled` when the gesture is abandoned (Escape, a
lost capture). A `MapToolEvent` carries the pane under the pointer, the point in the view, in the pane and in
the world, the button and the keys held. The `IMapToolContext` finds the pane at a point, projects a world
point through a pane's camera, gives the world units one pixel covers (for hit radii that stay the same size
on screen) and asks for a repaint.
