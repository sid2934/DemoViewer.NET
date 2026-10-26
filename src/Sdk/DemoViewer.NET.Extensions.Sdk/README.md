# DemoViewer.NET Extensions SDK

Build extensions for DemoViewer.NET: tabs, demo passes and jobs, rulesets, 2D Playback contributions, Library
filters and badges, Match Overview actions, settings and per-demo data. An extension is a .NET 10 class library that references
this package and nothing else of the app's.

`samples/Extensions/HelloExtension` in the DemoViewer.NET repository is a complete, minimal extension.

Views that should look like the app take `DemoViewer.NET.Extensions.Sdk.Ui` as well: the app's shared controls,
the palette token and style class names, and the view-model base the host resolves views for.

The 2D map's scene types come with the `DemoViewer.NET.Playback2D.Scene` package, under the
`DemoViewer.NET.Playback2D` namespaces (`.Compositing`, `.Levels`, `.Tools` and others): the frame, the
layer contract, floors, places and the world-to-screen transform, over SkiaSharp.

## Project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DemoViewer.NET.Extensions.Sdk" Version="1.2.*" />
    <PackageReference Include="Avalonia" Version="12.1.4" />
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
  "requiresHost": "^2.0",
  "requiresCs2DemoKit": "*"
}
```

- `id` is lowercase reverse-DNS (`[a-z0-9._-]`) and permanent. Ids starting with `net.demoviewer.` are reserved for the app's own
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

`Commands`, `CommandScopes`, `JobKinds` and `Stores` are read the same way, without DI. `Stores` lists the
folders and files you keep outside the folders the host gives you (see `Stores` under "The host"): each path's
first segment must be your id, your id and `.json`, or start with your id and `-`, and "Delete extension
data" removes them with the rest. The extensions the app ships are exempt from that naming rule and may name
other paths; the folders the host keeps, and the demo cache's own records under the cache root, refuse every
extension alike.

## What an extension can add

| Call | Adds |
|---|---|
| `Tabs` | An `IWorkspaceModule` whose tabs join the strip, or a hub's rail when a descriptor names a `HostId`. |
| `HubTab` | A main tab whose body is a rail of sections. The host draws the tab and the rail; sections name its id. |
| `StatusChip` | A chip on the status strip. You supply its state through `IStatusChipSource`; the host draws it. |
| `Pass` | An `IExtensionPass` that runs on every demo the Library visits, on the one parse the visit reads. |
| `RecordPass` | An `IExtensionRecordPass` that runs over what the library already holds for a demo, without a parse. |
| `Collector` | A per-frame reader for one of your passes, run inside the host's rules run of a demo instead of a walk of its own. |
| `Ruleset` | A ruleset as YAML, run with the highlights on every demo; its tables become library facts, or one pass's input. |
| `SettingsSchema` | A page under Settings, Extensions that the host renders from a list of settings. |
| `SettingsPage` | A page under Settings, Extensions with controls of your own. |
| `Playback` | Lanes, panes, panels, toolbar items, mode toggles, map layers, map tools and key or action handlers in 2D Playback. |
| `Library` | A Library filter, a per-demo badge, or both. |
| `DemoAction` | A button on Match Overview for the open demo. |
| `DataRemoval` / `DataDeleted` | A "Delete extension data" of your own, and a callback after the host's delete. |
| `ReindexEstimate` | The count behind "N demos will be re-indexed" when the extension is switched back on. |

Each contribution shows only while the extension's master switch is on, and while its own feature id is on
when it names one. That feature must be one your extension declares; naming another is logged and the
contribution follows your master switch instead.

Hub, status chip, demo action and toolbar item ids are shared across every extension, so each starts with your
extension's id and a dot (`dev.example.hello.chip`). One that does not is left out and logged.

A section can also join a published hub: name `HostIds.LibraryTab` (the Library's view switch, which the app
owns) or `HostIds.StratBookHub` (the Strat Book rail) as its `HostId`. The Strat Book rail belongs to the Strat
Book extension, so a section on it is hidden while the Strat Book is off and left out, with a log line, when the
Strat Book is not loaded. A hub id you declare must not repeat one the app or another extension uses; a second
declaration of an id is left out and logged.

## Keys

The app has one keymap. Declare your commands in `IExtension.Commands`, the only place the keymap reads them
from. A command's id starts with your extension's id and a dot (`dev.example.hello.where`);
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
gives the same answer for the point pressed. `PlacesVersion` changes when the user edits the map's zones; keep
it beside a place you store to know when to look the place up again.

A toolbar item's `PlaybackMoment` and a pointer handler's `PlaybackPointer` carry the frame on screen
(`Frame`, a `DemoViewer.NET.Playback2D.Scene2DFrame`): the players' markers, grenades and clock.
It is valid only during the call; the tab refills it for the next frame.

`AddLayer(id, factory, displayName, enabled)` draws an `ISceneLayer` on the tab's map among its own layers. The host files it
as `ext.<your extension id>.<id>`, so it can never take one of the tab's layers or another extension's, and
the built layer's own `Id` is not used. `displayName` labels the layer's own visibility row in the tab's overlay
strip, and a null or blank one labels it with your extension's name; `enabled` is where that row starts. The
host keeps the user's choice for the session only, so pass back what you stored through `IExtensionSettings`
if you want it remembered. The factory runs again whenever the map rebuilds its scene. The layer sees
only what it is handed: the frame in `Advance`, the canvas and the `SceneRenderContext` in `Render`, which
runs on the render thread. The canvas is restored after each call, and a layer that throws stops drawing for
the session. `AddTool(tool)` adds an `IMapTool` that is offered every primary press the tab does not pan
(Space, Control and the middle button pan), after the pointer handlers; the tool that takes a press owns the
gesture until the release.

A lane's track is an `ITimelineTrack` from `DemoViewer.NET.Playback2D.Scene`, the same contract the tab's own
tracks use. It can draw marks as well as bands: `BuildMarkers` puts a glyph on the timeline at a frame, and
the `TimelineMarkerKind` it asks for is what the host themes the glyph by. The host files every band and mark
under the lane's id, so there is no point setting `TrackId` yourself.

For a map in your own tab, use `MapView` from the UI kit (`DemoViewer.NET.Extensions.Sdk.Ui`).

## Exporting a scene

`IExtensionContext.SceneExport` is the host's 2D export. It is null where the host exports nothing, which is
the browser build: hide your export surface while it is.

There are two ways in. `OpenPane` opens the host's export pane over your scene and gives you back an
`ISceneExportPane`: put its `ViewModel` in a `ContentControl` in your own view, subscribe `Started` if you
want to close the pane when the user starts, and dispose the handle to close it. An export that has started
keeps running, and its progress stays on a status chip the host files under your extension. `RenderAsync`
renders one `SceneExportClip` with no pane and no chip, for a caller already inside its own job.

Either way you describe the scene with a `SceneExportScene`, and its frames come from one of two places. Set
`Frames` and `TickRate` and the host replays a parsed demo through its own private tracker. Set `Source` and
the host asks your `SceneFramesFactory` for an `ISceneFrames` instead, once the render's own thread has the
map's art and the range, rate and speed the user chose. That second path is for a scene that never had a demo
behind it. `MapName` is what the host looks the map's art up by, and it holds that art for the whole run, so
dropping your own hold mid-render cannot take it out from under the render.

The pane's request carries what only you know: the `Ranges` offered in your own units, the
`SceneExportChoices` it opens on, the `OutputFrameCount` that sizes a range, the `Layers` built from its ink
and clock toggles, and a `Capture` the host calls on the UI thread at Start. Return null from `Capture` and
the run is refused with your `NothingToExport` copy, so a scene the user closed while the pane was open can
never be rendered from a stale capture. Leave `LayerIds` or `Layers` unset and you get the default scene with
no opt-in layer: no ink, no HUD and no telestration is ever burned in without being named.

A render that does not produce a file answers with a `SceneExportOutcome` rather than throwing, and
`Message` is the only part of it to put in front of a user. `SupportedFps(formatId)` lists the rates a format
takes, which are shorter for GIF than for video, and `SizePresets` the sizes the host's own export offers.
Every `formatId` here is one of `SceneExportFormats.WebM`, `.Mp4` or `.Gif`. They are persisted keys, so name
them through that class rather than writing the string.

## The host

`services.GetExtensionContext(Id)` resolves only for your own extension's code, so call it from inside your
extension and pass your own id; any other caller is refused. It returns the extension's `IExtensionContext`:

- `Shell`: the open demo, opening demos, seeking, selecting tabs, revealing files, and opening a player in the
  Player Overview tab (`OpenPlayerOverview`, false when the tab is off or the host has none). `CurrentDemo` is the open
  demo's parse, shared with the shell, so read it and drop it on `CurrentDemoChanged`. `CurrentDemoHash` is the
  demo's content key (lowercase hex SHA-256 of the file), the one to key per-demo data on. `CurrentTick`,
  `IsPlaying` and `PlayheadChanged` follow playback; the event fires at most once per rendered frame.
- `Features`: the feature switches, live.
- `Jobs`: the processing queue. Run every off-UI-thread job through it, so the user sees it and can pause or
  remove it. `Enqueue` returns an `IJobHandle`: its `Status`, a `Completion` that never faults, a `Completed`
  event raised on the UI thread, and a `Cancel` that reaches that job only. Jobs carry the extension's id;
  `CancelAll` and switching the extension off cancel the extension's own jobs and nobody else's. Declare your
  own job kinds in `IExtension.JobKinds`, or use `BuiltInJobKinds`. `JobPriority.Backlog` runs ahead of
  background work and behind anything the user asked for.
- `Passes`: ask for one demo again (`Request`), re-check the library (`RecheckAll`), and see whether one of your
  passes has a demo in flight (`IsBusy`, `Changed`).
- `Settings`: your settings, a flat set of keys in a file only you read and write. `Get` takes the default and
  answers it when nothing usable is stored; `Set` writes only a change; `Changed` names the key on the UI
  thread. The browser build keeps them for the session.
- `Data`: per-demo data the host keeps for you. See below.
- `Library`: the demo library, read only. See "Reading the library".
- `Stores`: every folder of yours on disk. `Open(storeId)` hands a store your entry type declared in
  `IExtension.Stores` back as an `IExtensionFolder`: list, read, write whole, append, move and delete under
  it by relative path, or take its `Path` for a process or the file manager. `OpenFile(storeId)` hands a
  store that declares one file back as an `IExtensionFile`. A declared store is where data lives that a user
  is meant to find, or that an older build left where the user has it; the host resolves it from the
  descriptor, so the same rule that bounds "Delete extension data" bounds what you can open. `Own(root)`
  hands back a folder of your own for everything else, one per root (`StoreRoot.Config` for the user's work,
  `StoreRoot.Cache` for what you can rebuild), with the same `IExtensionFolder` on it. Every write replaces
  the file whole or leaves the previous one, so a crash mid-write never leaves a torn file; `Append` is the
  one in-place write, and it reaches the disk before it returns. All three are null on the browser build,
  which has no folders, and a path that would leave the folder it is relative to is refused.
- `Notifications`: short messages to the user, drawn as a small stack above the status strip. See below.
- `Keymap`: the app's one keymap, read only. `Bindings` is every default binding with its scope, and
  `ShellReserved` and `BrowserReserved` the gestures the shell and the browser take first; check a key your
  extension assigns on its own, such as a hotkey in a file the user edits, against them. `ActionFor` and
  `GestureText` read the user's keymap with their rebinds applied, for a surface of your own that resolves
  keys itself, and `Changed` fires on the UI thread after a rebind. `KeyGestureText.Format` writes a gesture
  the way the rest of the app does.
- `SceneExport`: the host's 2D scene export, or null where it exports nothing. See "Exporting a scene".
- `Identity`: the user's own accounts from Settings, Identity, read only. `Accounts` lists every SteamID64 the
  user marked as theirs and `ActiveAccount` the one their own stats are about; `IsMine` checks one id, and
  `Changed` fires on the UI thread after the user edits the list. Read `Accounts` to find the user's side of a
  demo: any of them counts, not only the active one.
- `CreateLogger` and `Post`.

`JobScope` covers code that has no job context at hand: `JobScope.UserAction()` puts the jobs queued inside it
at user priority, and `JobScope.ThrowIfStopped()` stops a long loop when the user removes its job.

Register an `IExtensionLifecycle` as a keyed singleton under your id to start loads when the extension is
switched on and to release memory when it is switched off.

### Notifications

`Post` a `Notification` with an id, a severity, a title and an optional body, action and time to live. It is the
one way an extension tells the user something happened; a status chip is for state that lasts.

```csharp
context.Notifications.Post(new Notification("myextension.scan", NotificationSeverity.Success,
    "Scan finished", $"{count} demos read.")
{
    Action = new NotificationAction("Open", () => context.Shell.SelectTab("myextension.tab")),
    TimeToLive = TimeSpan.FromSeconds(30)
});
```

Posting again under the same id replaces the card in place, so a repeated "N new" message never stacks.
`Dismiss` closes one. `Post` is safe from any thread, never blocks and never throws: a notification without an
id or a title is logged against the extension and dropped. The host keeps three cards per extension and drops
the oldest past that, so a flood from one extension never pushes out another's. Cards show only while the
extension is on, close when it is switched off, and last for the session. The action runs on the UI thread; a
throw from it counts as the extension's fault.

## Reading demos

Never parse a demo yourself. One read of a demo serves the library, the host's passes and every extension's,
so register work that runs on every demo as a pass, and queue work for one demo as a job that names it.

A pass answers `Interest` for a demo from what it already knows (an index, a stamp), never from the file, and
never on the UI thread. `DemoInterest.AfterUpstream` says "once a pass I run after has written": the pass
joins the demo's visit when one of those passes is on it and is asked again right before its turn. `Run` gets
the parse for its turn only; finish synchronously and do not keep the parse. A pass that throws is skipped for
that demo for the rest of the session and counted against the extension; one whose runs keep overrunning its
time budget is switched off for the session.

```csharp
contributions.Pass("dev.example.rounds", () => rounds, HostIds.LibraryPass);
```

`after` names the passes whose output yours reads: `HostIds.LibraryPass` for the demo's cache record and
`HostIds.RoundFactsPass` for its per-round, per-side rows.

A pass that needs only the demo's header, frame clock and rounds declares `Read => PassRead.Forward` and reads
`context.Demo`, `FrameCount`, `FirstServerTick`, `LastServerTick`, `LastFrameTick` and `Rounds`; its `Parsed` is null. A visit
whose passes all read forward streams the demo instead of holding the whole parse, so declare it when you can.
A forward pass sees no player inputs itself, so the host does not consult its `ReadsUserCommands`; a collector or a
`ReadBy` ruleset of the pass that reads them is how it gets them.

Each entry in `Rounds` is a `PassRound`, one round opened by its `round_freeze_end`: `Number`, sequential
from 1 in demo order with warmup and restarts included, and `FreezeEndTick`, the round's freeze end in
the frame clock (`GameEvent.GameTick`), not the server tick `FirstServerTick` and `LastServerTick` report.
`LastFrameTick` is the last frame on that frame clock.

A job that names a demo joins that demo's visit: it runs after the passes on the same parse, or on the parse
the shell already holds when the demo is open, and holds the parse until its task ends.

```csharp
IJobHandle handle = context.Jobs.Enqueue(JobRequest.OnDemo("Count frames", path,
    job => { frames = job.Parsed.Frames.Count; return Task.CompletedTask; },
    new JobOptions(Priority: JobPriority.UserRequested)));
handle.Completed += result => Status = result.Status.ToString();
```

## Collectors

A collector is a per-frame reader that rides the rules run the host already makes of a demo for one of
your passes, instead of reading the demo on its own. Register one with `contributions.Collector`, naming
the pass that owns its result:

```csharp
contributions.Collector(new CollectorContribution("dev.example.positions", "dev.example.rounds", 1,
    ["entity.pawn.pos_x", "entity.pawn.pos_y"], [], () => new PositionCollector())
{
    GameEvents = ["round_start"]
});
```

`Id` starts with your extension's id and a dot, as every id shared across extensions does; one that does not is
left out and reported. It is how the owning pass reads the result back. `PassId` names the
`IExtensionPass` that owns it: the collector runs only on a visit that pass is on. `Version` is bumped
when what the collector puts changes; it folds into `Stamp` along with the declared reads and the engine
build, so a pass that keys its own fingerprint on `Stamp` runs again when any of them moves. `Providers`
and `ProjectileClasses` name what the collector reads off each frame, from the engine's catalog
(`entity.pawn.pos_x`, `CSmokeGrenadeProjectile`); a name the engine does not know refuses the collector.
`Create` builds a fresh `IFrameCollector` for one run.

`ReadsUserCommands` opts into `IFrameReadView.Command`: the pass's visit then decodes user commands for
the collector to read, where it would otherwise leave them out, whether or not the pass itself answers
true to its own `ReadsUserCommands`. A forward pass's own answer is not consulted, so a collector that
answers true is how a forward pass reaches commands. `GameEvents` names game events the
collector reads off `Frame`: a streamed read decodes only the events the rules themselves subscribe
to, plus these, where a held parse already carries every event.

`IFrameCollector.OnFrame` is called once per frame, in read order, with an `IFrameReadView` valid only
during that call: copy what you keep. `Finish` runs once, after the last frame, and puts the result
through `ICollectorSink.Put`; the owning pass reads it back with `IPassContext.Collected<T>(collectorId)`,
which is null when nothing was put and throws when the collector failed on this demo or names a
collector that is not one of the pass's own. A collector that throws from `OnFrame` ends that collector
for the run and fails its pass's read, `Finish` is not called for it, and the run and every other
collector on it carry on.

`IFrameReadView` is the state at the end of one frame, as the rules run folded it: a read here always
sees that frame's own update applied, not the frame-before state a rule's `player.*` read sees at a
game event. `Frame` is the frame just read, `FrameIndex` its position in read order from zero, and
`Tick` the frame clock (`Frame.GameTick ?? Frame.ServerTick`), the same clock `PassRound.FreezeEndTick`
is on. `Provider` resolves a declared name once into an `ObservedRead`, so `TryReadInt`, `TryReadFloat`
and `TryReadString` can read a slot by it without a further name lookup; `Read` takes the name directly
and boxes. `Team` is the slot's controller team as last seen (2 T, 3 CT), or -1. `Command` is the
slot's latest user command as of the end of this frame, or null before its first; one frame's commands
collapse to the last one, and the instance is the engine's baseline for the next delta, so read it and
never change it. It throws unless a collector on the run declared `ReadsUserCommands`.

`Projectiles` is every live projectile of a declared class, in ascending entity index; `Created` holds
the ones that appeared on this frame, and `Removed` the ones that were live last frame and are gone on
this one, with their last-seen state. Each is an `ObservedProjectile` carrying its position, velocity,
bounce count, thrower slot and, for an HE, `ExplodeEffectTickBegin` on the server-boot clock (0 while
it flies, the tick its explosion began once it detonates, null when unseen). Slot reads are never
cleared: a player who died, left or has no pawn on this frame still reads the values from before, so a
present value does not mean the slot has a pawn, and a health or position read cannot tell a dead
player from a live one.

## Reading the library

`context.Library` is the Library's index as `LibraryDemo` rows: path, map, hash, server, source kind, scores,
clans, player names, the players on each side, how far the demo has been read (`State`) and the facts written
for it (`Facts`). `Demos`, `Find`, `FindBySha256` and `Query` read memory only and are safe on any thread; an
unchanged demo is the same row instance on every read. `Changed` is raised on the UI thread with the demo's
`FilePath`, or with a null path when many demos changed at once, and says whether a demo was added, removed or
updated, or only had its facts rewritten.

A demo is its content, not its path. `Sha256` is its identity once the library has read the file in full;
copies of one file, or one share mounted at two paths, are one row whose `Locations` lists every path confirmed
to hold those bytes, `FilePath` (the one the Library shows) first. `Find` answers the same row for any of them,
so a path the shell has open can differ from the `FilePath` a change names. Match by content, or through
`Find`, not by comparing paths:

```csharp
library.Changed += change =>
{
    string? open = shell.CurrentDemoPath;
    if (change.Path is null || (open is not null && library.Find(open)?.Locations.Contains(change.Path, StringComparer.OrdinalIgnoreCase) == true))
    {
        Refresh();
    }
};
```

`Players` is every account in the library and the demos it is in, from the same index and safe on any thread.
`Accounts` lists them with every name they played under (unsanitized), the one in the most demos first;
`Account` and `Find` look one up by SteamID64 or by part of a name, and `Matches` lists an account's demos newest
first, each the library's own `LibraryDemo` row with the side the account ended on. A player who left before
the end has no side and `LeftEarly` set. A match's `Time` is the match's UTC start when the demo carries it,
else the file's local write time. `Changed` fires on the UI thread after the library moves.

A path the library matched to a known demo without a full read is not in `Locations` until a read confirms it.
`Find` answers it with a row of that path alone and no `Sha256`, which `Demos` lists only when the demo has no
confirmed path. A demo whose every path left the library is not found, though the library keeps its data for
a while in case the file comes back.

`GetDetailAsync` reads one demo's record: the roster with slots and SteamIDs, and where each round starts. It
reads no demo file. Off the UI thread it reads before it returns; on the UI thread it reads as a job.

Work over many demos is a record pass, not a loop over `GetDetailAsync`. The host asks a record pass about a
demo whenever the demo's row changes and when it re-checks the library, reads the record once for every pass
that wants it, and runs it as a light job:

```csharp
contributions.RecordPass("dev.example.rosters", () => new RosterPass());

sealed class RosterPass : IExtensionRecordPass
{
    public string Id => "dev.example.rosters";
    public bool Wants(LibraryDemo demo) => demo.State >= LibraryDemoState.Parsed && demo.Sha256 is { } sha && !Seen(sha);
    public void Run(LibraryDemoDetail detail, CancellationToken ct) => Remember(detail.Demo.Sha256!, detail.Players);
}
```

Keyed by `Sha256`, the pass reads a demo once however many copies the library holds; a row with no hash yet is
asked again when the hash lands, since that changes the row. A record pass runs again on a demo only when its
row changes. One that throws is skipped for that demo for the
rest of the session and counted against the extension.

A record pass that reads analysis facts names them after the factory:

```csharp
contributions.RecordPass("dev.example.kast", () => new KastPass(), new FactKey("kast", "kast_game_totals"));
```

The host then holds the pass on a demo until each named ruleset's write on it has settled, and runs it again
when one of those writes moves, not on other changes to the row. A failed or stale write counts as settled, so
the pass checks `Facts.Status` before it trusts a table. A ruleset writes all its tables at once, so naming one of
them re-runs the pass on any. While a named ruleset is off, because its extension is off or its doc is
disabled, the pass waits and is taken again once it is on. Any other key missing from `Facts.Declared`, or a
scoreboard key, refuses the pass; the refusal is reported against the extension once, and the keys are checked
again on every run.

A pass can also wait on what other passes write per demo, named by `DemoDataKey`: the host's Round Facts rows
(`DemoDataKey.RoundFacts`), or a facet an extension writes through `context.Data`, under that extension's id:

```csharp
contributions.RecordPass("dev.example.openings", () => new OpeningsPass(),
    DemoDataKey.RoundFacts, new DemoDataKey("dev.example", "round-index"));
```

The same rules apply: the pass runs once every named facet has a stamp on the demo, and again when one of
those stamps moves. While the owning extension is off the pass waits; a facet of an extension the host has not
loaded refuses it.

## Analysis facts

`context.Library.Facts` is every analysis output the library holds: the highlights its scan found
(`Highlights`), the per-round, per-side Round Facts rows (`RoundFacts`), and the tables of every ruleset the
host runs beside the highlights, its own and every extension's, as `FactTable`s keyed by
`FactKey(rulesetId, table)`. Facts are library data: an extension reads another's as freely as its own. A
ruleset whose extension is off is not in `Declared`, and its facts read as absent until it is back on.

`Declared`, `Status` and `IsCurrent` read the library's index and open no file. `TryGet`, `Highlights` and the
Round Facts rows read one file each: call them off the UI thread, in a job or a pass. A demo whose facts were
written raises `Library.Changed` for it. A pass that reads them names `HostIds.FactsPass` (or
`HostIds.RoundFactsPass`) in `after`.

A ruleset's scoreboard is read off snapshots, which only a full analysis of one open demo takes, so the library
never writes it: its key is in `Declared` and its status is always `NeedsFullAnalysis`.

## Rulesets

Ship a ruleset as YAML and the host runs it on every demo the library visits, in the same rules run as the
highlights, while the extension is on:

```csharp
contributions.Ruleset(new RulesetContribution("kills",
    () => typeof(MyExtension).Assembly.GetManifestResourceStream("MyExtension.kills.rules.yaml")!));
```

The ruleset's id is qualified with the extension id: `RulesetContribution.QualifiedId("com.example.myextension",
"kills")` is `com_example_myextension__kills`, and the YAML's `ruleset:` key must be exactly that. Each table its
`show: tables:` declares is written for every demo as a fact under `FactKey(qualifiedId, tableName)`; a table name
must be one no other ruleset declares. Editing the YAML re-runs the ruleset alone on every demo, and never the
highlights.

The host reads the YAML between the shipped rules and the user's own rules folder, so a user file with the same
id overrides yours, or switches it off with `enabled: false`. A ruleset that does not load, carries another id or
reuses a table name is left out with a line in the diagnostics log, and nothing else is affected.

The Authoring tab lists your ruleset beside the shipped files, tagged with your extension id. It is read-only
there, its problems show in the check like any file's, and Save As under its id writes the user's override.

List the names in `extension.json`, so the host keeps them apart from the highlights even when the extension
does not load. A ruleset the manifest does not list is left out and logged against the extension:

```json
"rulesets": ["kills"]
```

"Delete extension data" removes the facts of your rulesets with the rest of your data.

### A ruleset one pass reads

A table can be a pass's input instead of a fact. Name the pass in `ReadBy` and read the rows with
`IPassContext.Table` during its turn:

```csharp
contributions.Ruleset(new RulesetContribution("positions", OpenPositionsYaml) { ReadBy = "dev.example.rounds" });

// in the pass's Run
IReadOnlyList<FactRow> rows = context.Table("positions");
```

The host records the tables only on the rules run of a demo the pass wants, on a streamed read and a held parse
alike, and never writes them as facts: they are not in `Declared` and have no status. The pass keeps what it
derives from them under its own fingerprint, which should cover the YAML and the engine build. `Table` throws for a
table no ruleset of the pass declares, and when the run did not record it because the ruleset was left out.

A ruleset that reads `player.cmd.*` (or `thrower.cmd.*`) sets `ReadsUserCommands` on its contribution. A streamed read
decodes the inputs from the build alone, but a visit that holds the parse leaves them out unless a pass on it asks, and
a forward pass is never asked; the flag asks on the ruleset's behalf, so the table reads the same on both.

## Settings

Describe your settings and the host draws the page and stores the values in `context.Settings`:

```csharp
contributions.SettingsSchema(new SettingsSchema("myextension.settings", "MY EXTENSION",
[
    SettingDescriptor.Toggle("scan.background", "Scan in the background", false, "About a second per demo."),
    SettingDescriptor.Number("scan.stride", "Sample every n-th tick", 4, 1, 64),
    SettingDescriptor.Choice("names", "Place names from", "pawn", [new("pawn", "The game"), new("zones", "My zones")]),
    SettingDescriptor.Folder("export.folder", "Export to")
]));

bool background = context.Settings.Get("scan.background", false);
context.Settings.Changed += key => { if (key == "scan.background") context.Passes.RecheckAll(); };
```

A setting that changes what a pass wants should ask the scheduler again, as above, or the change waits for the
next demo the library visits.

## Per-demo data

`context.Data` keeps what you compute for a demo, so the next session finds it without reading the demo again.
Data is kept per facet (a name for one kind of data) under the demo's content hash. Methods take a path, and any
of the demo's `Locations` reads and writes the same facet: a moved or renamed demo, a copy, or a second mount of
the same share keeps it. A facet written before the library hashed the demo follows the content once the hash
is known. A path matched to a known demo without a full read reads as absent until a read confirms it. When a
demo's last path leaves the library, its file deleted or its folder gone, its data is kept while the library
keeps the demo, and comes back with the file; it goes once no folder has listed the demo's bytes for the grace
period the user sets, two weeks by default. Nothing deletes one demo's data sooner. Each write records a schema and a
fingerprint (what the data was computed from); a read that asks for another schema or fingerprint finds
nothing, so a new version of your pass rebuilds rather than reads stale data.

```csharp
public DemoInterest Interest(string demoPath) =>
    data.Stamp(demoPath, "rounds") is { } stamp && stamp.IsCurrent(Schema, Fingerprint) ? DemoInterest.No : DemoInterest.Yes;

public void Run(IPassContext context) =>
    data.Write(context.DemoPath, new DemoDataWrite("rounds", Schema, Fingerprint, Serialize(Compute(context.Parsed!))) { Count = rows });

byte[]? payload = data.Read(demoPath, "rounds", Schema, Fingerprint);
```

`Stamp` and `Stamps` read the store's index and never open a file, so they are fine on the UI thread and in
`Interest`. `Read` and `Write` touch the disk: call them from a pass or a job. `Parts` on a write keeps more
than one file under one stamp; `MarkFailed`, `ClearFailed`, `Invalidate` and `SetCount` change a stamp alone.
The browser build keeps no per-demo data: `IsAvailable` is false.

"Delete extension data" in Settings switches the extension off, then removes your folders, your settings, your
per-demo data and the stores you declared, and calls what you registered with `DataDeleted`. A declared store's
paths must start with your id (`<id>.json`, `<id>-old/`); one that does not is dropped and logged.

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
extension contract; `^1.1` keeps those builds from loading SDK extensions. 1.2 adds `IExtensionContext.Identity`:
an extension that reads it states `^1.2`, since a 1.1 host has no such member. 1.2 also adds
`IExtensionLibrary.Players` and `IExtensionShell.OpenPlayerOverview` with default bodies, so a host or a test
double written before them answers no accounts and opens nothing. 2.0 changes `IPlaybackSurface.AddLayer`,
which now takes a display name and a starting state for the layer's own visibility row; a pack that adds a
scene layer states `^2.0`.
