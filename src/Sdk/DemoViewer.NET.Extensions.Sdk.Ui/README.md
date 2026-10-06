# DemoViewer.NET Extensions SDK: UI kit

The controls, theme names and view-model base an extension's views use so they look like the app they run
in. It builds on `DemoViewer.NET.Extensions.Sdk` and is versioned with it.

```xml
<ItemGroup>
  <PackageReference Include="DemoViewer.NET.Extensions.Sdk" Version="1.1.*" />
  <PackageReference Include="DemoViewer.NET.Extensions.Sdk.Ui" Version="1.1.*" />
  <PackageReference Include="Avalonia" Version="12.1.2" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
</ItemGroup>
```

Reference Avalonia and CommunityToolkit.Mvvm directly: the XAML compiler and the MVVM source generators run
only in a project that references their package itself.
The app ships this assembly, so a copy in your extension's folder is never loaded.

## View models and views

Derive view models from `ExtensionViewModel`, or implement `IExtensionViewModel` from the SDK on your own
`ObservableObject`. The host then builds the view for one by naming wherever it shows it: a pane, a panel, a
status chip flyout or any `ContentControl` in your own views. `MyExtension.ViewModels.StatsViewModel`
resolves to `MyExtension.Views.StatsView`, looked up in the view model's own assembly; the view needs a
public parameterless constructor. A view model without the marker renders as its type name.

## Controls

Namespace `DemoViewer.NET.Extensions.Sdk.Ui.Controls`, in XAML
`xmlns:ui="using:DemoViewer.NET.Extensions.Sdk.Ui.Controls"`.

| Control | What it is |
|---|---|
| `StatusChip`, `StatusChipViewModel` | A dot and a label that opens a flyout. Set `DotState` (`Off`, `Working`, `Good`, `Degraded`, `Error`), `IsPulsing`, `IsHollow`, `Label`, `Tooltip` and `FlyoutContent`; the colour follows the theme. |
| `KeyValueTable`, `KvpRow` | A two-column key/value list; delta rows show the previous value struck through. |
| `MarkdownBlock` | Headings, paragraphs, lists, bold and inline code from a Markdown string. |
| `GameIcon` | A CS2 icon by key (`equipment/ak47`, `modifier/headshot`), sized by `IconHeight` and tinted by `Foreground`. |
| `GifView` | An animated GIF from a file path. |
| `ParseLinkChip`, `OpenExternal` | A monospace link that opens a file in the user's editor or a URL in the browser. |
| `MapView` | A CS2 map with pan and zoom, your layers and your pointer tool. See below. |
| `SceneView`, `ISceneSource` | The 2D scene over frames of your own: markers, trails, utility, ink with the drawing tools, your layers and tools. See below. |
| `SceneTimeline`, `TimelineView` | A scrub bar over a frame clock of your own, with band and marker rows from your tracks. See below. |

### MapView

`MapView` shows a map with no demo behind it: set `MapName` (`de_mirage`) and the app loads the map's radar
art and floors; `HasMap` is false where it has none, so say so beside the view. You add to it by composition:

```csharp
MapView map = new() { MapName = "de_nuke" };
IDisposable heat = map.AddLayer("myext.heat", () => new HeatLayer(document));
PickTool pick = new(document);
IDisposable tool = map.AddTool(pick);
map.SetPrimaryTool(pick);
```

A layer is an `ISceneLayer` and a tool an `IMapTool`, both from `DemoViewer.NET.Playback2D.Scene`. The primary
tool gets every press the view does not pan; a left press it refuses pans the map, and Space, Control, the
middle button and the wheel always pan and zoom. `EscapePressed` fires for Escape with no gesture to cancel.
`PaneAt(point)`, `Panes` and `Space` give the panes and floors for a hit test of your own, and `Invalidate`
repaints after something a layer draws changed. The radar's and the floor label's ids are the view's own.
Layers and tools survive the view leaving and re-entering the tree; the factories run again when it rebuilds.

### SceneView and SceneTimeline

`SceneView` draws what moves: set `Source` to an `ISceneSource`, usually your view model, and raise its
`FrameUpdated` on the UI thread whenever `Frame` or anything else it exposes changes. Publish a new
`Scene2DFrame` each time rather than changing one in place, since the render thread replays the frame it was
handed. `MapAsset` gives the floors and radar art (from `MapAssets.TryLoad`, or null for the grid), and `Ink`
an `AnnotationSession` the drawing tools edit; select a tool with `SetActiveTool` and drive hold-to-pan and
cancel from your keys with `SetHoldPan` and `CancelGesture`. A press goes to `ISceneSource.OnPress` first and
to the active tool only if you refuse it. The text tool raises `TextEditRequested`; show an editor there and
hand the result to `CompleteTextEdit`. `AddLayer` and `AddTool` work as on `MapView`, and the scene's own
layer ids are refused.

`SceneTimeline` is the model of a scrub bar, owned by the view model: register `ITimelineTrack`s, `Rebuild`
with the clock's `ITimelineData`, move the playhead with `UpdatePlayhead` and seek when `SeekRequested` fires.
`TimelineView` shows it; bind its `Timeline` to yours. Both controls need the app, and outside it they draw
nothing and a timeline holds nothing.

To resolve keys on a surface of your own, ask `IExtensionContext.Keymap.ActionFor(scope, key, modifiers)`,
the tool scope first while a tool is active, and name gestures in your hints with `GestureText`. Refresh the
hints on `Changed`, and subscribe only while your view is on screen: the keymap lives as long as your
extension.

To draw a map without a view, `MapAssets.TryLoad("de_mirage")` loads the map's floors, radar images and places
as an `IMapAsset` you dispose, and `MapAssets.RenderPng` draws a `Scene2DFrame` to a PNG the way the app's
export does, for a thumbnail. `MapIcons` hands a layer the game's weapon and grenade art. Both work from any
thread; outside the app nothing loads and a picture throws.

`DisplayText.Sanitize` strips the invisible Unicode format characters that make Avalonia's line wrapping
throw on some player names; run every player name through it before showing it.
`BulkObservableCollection<T>` replaces or extends its contents with one `Reset` instead of an event per item.

## Theme

The app's palette and shared styles are loaded once for the whole process, so your XAML uses them directly:
`{DynamicResource TextMid}` and `Classes="ghost"`. A misspelt or renamed name resolves to nothing without an
error, so the names are published as constants in `DemoViewer.NET.Extensions.Sdk.Ui.Theming`:

- `ThemeTokens` lists every palette key with its dark and light value. Every key exists in every theme,
  built-in or a user's.
- `StyleClasses` lists every class the shared styles select on, with the controls it styles: `Primary`,
  `Ghost`, `Chip`, `Mono`, `Card`, `SectionLabel`, `ColLabel`, `DataList` and the rest.

```xml
<Border Background="{DynamicResource {x:Static theme:ThemeTokens.PanelBg}}" />
```

Both are generated from the app's own palette and style files when the SDK is built, so they match the app
release the package version ships with. A control that draws itself resolves a token with
`ThemeColors.Get(ThemeTokens.AccentInteractive, ActualThemeVariant, fallback)`.

## Testing your views

`src/Testing/DemoViewer.NET.Extensions.Testing` in the DemoViewer.NET repository holds the headless test
support the app's own suites use: a shared headless session (`HeadlessSession.RunOnUi`) and a recording
`IModuleContext` fake. Import its `DemoViewer.NET.Extensions.Testing.props` from a TUnit test project that
references Avalonia.Headless and Avalonia.Skia, and name your app builder with
`[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`.
