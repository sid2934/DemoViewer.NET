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

The types keep the namespaces the app uses for them, under `DemoViewer.NET.Playback2D.Core`.

## What is in it

| Namespace | Types |
|---|---|
| `Playback2D.Core` | `Scene2DFrame` and what it carries (`PlayerMarker`, `GrenadeTrail`, `AreaEffect`, `BombMarker`, `KillFeedRow`, `SceneGameInfo`, `SceneMapInfo`, `SceneVision`), `SceneTime`, `ViewportTransform`, `SliceCamera`, `ScenePalette`, `WorldBounds`, `TextBlobCache` |
| `Playback2D.Core.Compositing` | `ISceneLayer`, `LayerSlot`, `LayerCacheHint`, `SceneRenderContext`, `SceneSubmission`, `SceneCompositor`, `SceneRenderGate` |
| `Playback2D.Core.Layers` | `SceneLayerIds`, the ids of the app's own layers |
| `Playback2D.Core.Levels` | `MapSpace`, `MapLevel`, `MapLevelId`, `PaneSet`, `LevelPane`, `StackedLayout`, `SingleLayout` |
| `Playback2D.Core.Zones` | `PlaceResolver`, `ZoneSet`, `PlaceHit` and the zone geometry |
| `Playback2D.Core.Cameras` | `ICameraRig` and the built-in rigs |

The concrete layers, the drawing tools, annotations, video export and the render backends are not
published.

## Frames and layers

A `Scene2DFrame` is valid only for the call it is handed to: the app refills frames in place, so never keep
one. Its properties are init-only, which lets a test or a fixture build one with an object initializer.

An `ISceneLayer` does its work in two steps. `Advance` runs on the UI thread before each frame is submitted
and is the only place a layer may change its own state. `Render` draws one pane onto the `SKCanvas` it is
given, already clipped and translated, using only the `SceneRenderContext`: `Transform` maps world to pane
pixels, `BelongsHere(z)` says whether a world height belongs on the pane's floor, and `Palette` has the
theme's colours. `Render` runs once per pane and may run on the render thread, so it must not change
anything. Layer ids beginning `playback2d.` or `hud.` are the app's.
