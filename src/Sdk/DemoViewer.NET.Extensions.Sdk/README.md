# DemoViewer.NET Extensions SDK

Build extensions for DemoViewer.NET: tabs, demo evaluators, 2D Playback contributions, Library filters and
badges, Match Overview actions and Settings pages. An extension is a .NET 10 class library that references
this package and nothing else of the app's.

`samples/Extensions/HelloExtension` in the DemoViewer.NET repository is a complete, minimal extension.

Views that should look like the app take `DemoViewer.NET.Extensions.Sdk.Ui` as well: the app's shared controls,
the palette token and style class names, and the view-model base the host resolves views for.

The 2D map's scene types are in `DemoViewer.NET.Playback2D.Scene`: the frame, the layer contract, floors,
places and the world-to-screen transform, over SkiaSharp.

## Project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DemoViewer.NET.Extensions.Sdk" Version="1.1.*" />
    <PackageReference Include="Avalonia" Version="12.1.2" />
  </ItemGroup>
</Project>
```

Put `extension.json` beside the csproj. The package's build targets stamp `{version}` with the project's
package version, embed the result in the assembly and copy it beside the DLL:

```json
{
  "id": "com.example.myextension",
  "name": "My Extension",
  "version": "{version}",
  "assembly": "MyExtension.dll",
  "entryType": "MyExtension.MyExtension",
  "requiresHost": "^1.1",
  "requiresCs2DemoKit": "*"
}
```

- `id` is reverse-DNS and permanent. Ids starting with `net.demoviewer.` are reserved for the app's own
  extensions and never load from a third party.
- `requiresHost` is the SDK range you built against. The app refuses an extension whose range it does not
  satisfy.
- `requiresCs2DemoKit` matters only if you read `ParsedDemo` beyond what the SDK version you build against
  ships. Use `*` otherwise.
- `minAppVersion` is optional: the oldest app release you support.

## The entry type

```csharp
public sealed class MyExtension : IExtension
{
    public string Id => "com.example.myextension";
    public string FeatureId => "pack.myextension";

    public IEnumerable<ExtensionFeature> Features =>
    [
        new("pack.myextension", ExtensionFeatureKind.Extension, "My Extension", "What it does.", null, AudienceDefaults.Everyone),
        new("tab.myextension", ExtensionFeatureKind.Tab, "My tab", "What the tab shows.", "pack.myextension", AudienceDefaults.Everyone)
    ];

    public void Register(IServiceCollection services) =>
        services.AddSingleton(sp => new MyTabViewModel(sp.GetExtensionContext(Id)));

    public void Contribute(IExtensionContributions contributions, IServiceProvider services) =>
        contributions.Tabs(new MyModule(services.GetRequiredService<MyTabViewModel>));
}
```

The app creates the entry type with its public parameterless constructor. `Register` runs whether the
extension is on or off, so register factories and do no work there. Feature ids are persisted with the
user's choices: never rename one. The master switch's id must start with `pack.`.

## What an extension can add

| Call | Adds |
|---|---|
| `Tabs` | An `IWorkspaceModule` whose tabs join the strip, or a hub's rail when a descriptor names a `HostId`. |
| `HubTab` | A main tab whose body is a rail of sections. The host draws the tab and the rail; sections name its id. |
| `StatusChip` | A chip on the status strip. You supply its state through `IStatusChipSource`; the host draws it. |
| `Evaluator` | An `IExtensionEvaluator` that reads every demo the Library indexes, on the shared parse. |
| `Commands` / `IExtension.Commands` | Key-bound commands the user can rebind, and `IExtension.CommandScopes` for their focus scopes. |
| `SettingsPage` | A page under Settings, Extensions. |
| `Playback` | Lanes, panes, panels, toolbar items, mode toggles and key or action handlers in 2D Playback. |
| `Library` | A Library filter, a per-demo badge, or both. |
| `DemoAction` | A button on Match Overview for the open demo. |
| `Store` / `DataRemoval` | The files "Delete extension data" lists and removes. |
| `ReindexEstimate` | The count behind "N demos will be re-indexed" when the extension is switched back on. |

Each contribution shows only while the extension's master switch is on, and while its own feature id is on
when it names one.

A section can also join a hub the app owns: name `HostIds.LibraryTab` (the Library's view switch) or
`HostIds.StratBookHub` (the Strat Book rail) as its `HostId`. A hub id you declare must not repeat one the app
or another extension uses; a second declaration of an id is left out and logged.

## Keys

The app has one keymap. A command's id starts with your extension's id and a dot (`dev.example.hello.where`);
core actions keep bare ids such as `TogglePlay`. A command whose id lacks the prefix, or repeats an id already
taken (ignoring case), is left out and listed in Settings with the reason. The user's rebinding is stored
against the id, so never rename one.

The scope `playback2d` applies whenever the 2D Playback map has focus, and `playback2d.tool` while a drawing
tool is active. For a panel of your own that takes the keyboard, declare a `CommandScope` in
`IExtension.CommandScopes` (its id carries the same prefix, its label is what Settings shows), and from a key
handler added with `IPlaybackSurface.AddKeyHandler` ask `IPlaybackSurface.ActionFor(scope, key, modifiers)`
while the panel has focus.

A key bound to your command reaches your action handler (`IPlaybackSurface.AddActionHandler`), or runs the
toolbar item or mode toggle whose action id names it, and only while the extension is on.
`IPlaybackSurface.ActionIds` lists every id the keymap knows, the other extensions' included.

## The map

`IPlaybackSurface.Levels` lists the shown map's floors, lowest first. `Places` lists its named places, and
`PlaceAt(level, x, y)` answers the place at a world point on a floor, by the floor's name, or null for the
map's only floor. Both are empty or null on a map with no zones. A pointer handler's `PlaybackPointer.PlaceAt`
gives the same answer for the point pressed.

## The host

`services.GetExtensionContext(Id)` returns the extension's `IExtensionContext`:

- `Shell`: the open demo, opening demos, seeking, selecting tabs, revealing files. `CurrentDemo` is the open
  demo's parse, shared with the shell, so read it and drop it on `CurrentDemoChanged`. `CurrentDemoHash` is the
  demo's content key (lowercase hex SHA-256 of the file), the one to key per-demo data on. `CurrentTick`,
  `IsPlaying` and `PlayheadChanged` follow playback; the event fires at most once per rendered frame.
- `Features`: the feature switches, live.
- `Jobs`: the processing queue. Run every off-UI-thread job through it, so the user sees it and can pause or
  remove it. Jobs carry the extension's id, and switching the extension off cancels its queued jobs. Declare
  your own job kinds in `IExtension.JobKinds`, or use `BuiltInJobKinds`.
- `Storage`: a config folder for the user's work and a cache folder for what you can rebuild. Both are null
  in the browser build. Write files with `WriteAtomicAsync` and read them with `ReadAsync`: the path must stay
  inside the folder, and a crash mid-write leaves the previous file instead of a torn one.
- `CreateLogger` and `Post`.

Register an `IExtensionLifecycle` as a keyed singleton under your id to start loads when the extension is
switched on and to release memory when it is switched off.

## Installing

Copy the build output to `extensions/<id>/<version>/` under the app's config folder:

- macOS: `~/Library/Application Support/DemoViewer.NET/extensions/`
- Windows: `%APPDATA%\DemoViewer.NET\extensions\`
- Linux: `~/.config/DemoViewer.NET/extensions/`

The newest compatible version of each id loads. Ship your own dependencies in the folder. Assemblies the app
already ships (this SDK, Avalonia, CS2DemoKit, the runtime) always come from the app, so a copy in your
folder is ignored.

An extension the app's publisher has not signed is unverified. Unverified extensions load only after the
user turns on "Allow unverified and potentially dangerous extensions" in Settings and restarts. An extension
runs inside the app with the app's access to files and the network; that setting records the user's consent,
it does not sandbox anything. A copy whose signature is present but broken never loads.

## When your code throws

The host runs every callback it makes into an extension under a guard: a throw is logged against the
extension in the diagnostics log and the host carries on with a fallback (a filter keeps the demo, a view
shows a placeholder, a handler is skipped). Three errors inside a minute, or ten in a session, turn the
extension off until the app restarts; Settings says so and offers to turn it back on. A handler that runs on
every frame or keypress counts once a minute for the same error. If `Register` throws, or a lifecycle
cannot be built while the app starts, the app starts without the extension. Binding errors in your views are
logged against the extension and never counted.

## Safe mode

Start the app with `--safe-mode` to load no extension at all. The app also starts in safe mode by itself
after a launch that never finished starting, a session that crashed inside an extension, or a session that
stopped responding until it was closed. Settings, Extensions lists every installed extension and why any of
them did not load.

## Compatibility

The SDK follows semantic versioning, and the app never makes a breaking change to it outside a new major
version of the app. The assembly version is `Major.0.0.0`, so an extension built against any 1.x SDK binds to
every 1.x host. The SDK starts at 1.1 because 1.0.0 is what the app reported for its earlier, internal
extension contract; `^1.1` keeps those builds from loading SDK extensions.
