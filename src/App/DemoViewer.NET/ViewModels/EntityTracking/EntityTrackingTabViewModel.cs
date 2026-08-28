#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.Models;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Models;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     Owns Entity-Tracking-tab state: entity list, field tree, per-seek
///     <c>EntityTracker</c> lifecycle, the <c>EntitiesRefreshed</c> event, and the
///     prev-tick snapshot used for delta display.
///     <para>
///         Stable-ref collections, lifecycle fields,
///         scalar state, the three async seek pipelines, all rebuild helpers and the
///         <c>WatchField</c> command target now live here. Shell-side concerns (the
///         <see cref="EntityTracker" /> factory, the post-seek card refresh, parse-chain
///         updates on entity selection) reach in via callback hooks
///         (<see cref="CreateTracker" />, <see cref="OnSeekCompleted" />,
///         <see cref="OnEntitySelectionChanged" />) that <c>MainViewModel</c> wires in
///         its constructor -- same pattern as <c>Analysis.OnFrameSeeked</c>.
///     </para>
/// </summary>
public sealed partial class EntityTrackingTabViewModel : ObservableObject
{
    /// <summary>Master node list for the current seek, before the class filter is applied.</summary>
    private List<EntityNode> _allEntityNodes = [];

    /// <summary>Active class filter from the browser (null = all classes).</summary>
    private string? _classFilter;

    // Diagnostics-pillar logger (v0.6.0 — the seek-error surfaces show clean text, this carries
    // the real exception). Lazy: the ambient factory is wired after construction.
    private ILogger? _diagLog;

    [ObservableProperty]
    private int _entityDeltaFieldCount;

    /// <summary>
    ///     Full key/value projection of the selected entity's fields for the KeyValueTable
    ///     field view. Holds every field (delta + unchanged); the KeyValueTable's
    ///     <c>ShowDeltaOnly</c> (bound to <see cref="ShowDeltaFieldsOnly" />) does the visible
    ///     filtering. <see cref="EntityFieldNodes" /> is kept separately because the
    ///     entity parse-chain builder reads it via <c>ParserTab.EntityFieldNodesSource</c>.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _entityFieldRows = [];

    [ObservableProperty]
    private string _entityHeaderText = "";

    [ObservableProperty]
    private string _entityStatusText = "";

    // ── Scalar state ──────────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _hasEntities;

    [ObservableProperty]
    private bool _hasEntitySelection;

    // _hasTickGroups moved to ReplayTabViewModel (3.5b).
    [ObservableProperty]
    private bool _hasWatched;

    [ObservableProperty]
    private bool _isSeekingEntities;

    private EntityTracker? _localTracker;

    [ObservableProperty]
    private object? _selectedEntityItem; // EntityState

    [ObservableProperty]
    private EntityListItem? _selectedEntityListItem;

    [ObservableProperty]
    private bool _showDeltaFieldsOnly;

    [ObservableProperty]
    private bool _showDormantEntities;

    /// <summary>
    ///     When true, the right pane shows the selected entity's relationship inspector tree instead of its flat field
    ///     table.
    /// </summary>
    [ObservableProperty]
    private bool _showRelationshipTree;

    /// <summary>Initializes a new <see cref="EntityTrackingTabViewModel" /> instance.</summary>
    public EntityTrackingTabViewModel(FrameNavigationViewModel navigation)
    {
        Navigation = navigation;

        // 3.4b: WatchedValues -> HasWatched subscription used to live in
        // MainViewModel's constructor. Both ends are now EntityTab-owned, so
        // the wire moved here.
        WatchedValues.CollectionChanged += (_, _) => HasWatched = WatchedValues.Count > 0;

        // The entity list is a TreeDataGrid, fronted by a
        // class-browser filter, a per-entity delta log and a serializer-schema strip.
        // Row selection in the grid funnels the backing EntityState into SelectedEntityItem
        // (kept typed as EntityState so OnSelectedEntityItemChanged is unchanged).
        EntityList.EntitySelected += node => SelectedEntityItem = node?.Entity;
        ClassBrowser.ClassFilterChanged += OnClassFilterChanged;
    }

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.EntityTracking");

    /// <summary>Full unfiltered field list for the currently selected entity (for delta filter toggle).</summary>
    internal List<PayloadNode>? AllEntityFieldNodes { get; set; }

    /// <summary>Left-rail class browser; selection filters the entity list (F8.6).</summary>
    public ClassBrowserViewModel ClassBrowser { get; } = new();

    // ── Callback hooks (wired by MainViewModel) ───────────────────────────────
    /// <summary>
    ///     Factory that yields a fresh <see cref="EntityTracker" /> instance hooked to the
    ///     shell's <c>Debugger</c>. Required for all three seek variants. MainViewModel
    ///     sets this in its constructor.
    /// </summary>
    public Func<EntityTracker>? CreateTracker { get; set; }

    /// <summary>
    ///     Shared checkpoint-replay seek core. Wired by MainViewModel. The three seek
    ///     pipelines delegate the tracker-build + replay to this service; only the per-pipeline
    ///     snapshot policy stays here.
    /// </summary>
    public EntitySeekService? SeekService { get; set; }

    /// <summary>Public read accessor for <see cref="CurrentTrackerInternal" />.</summary>
    public EntityTracker? CurrentTracker => CurrentTrackerInternal;
    // 3.5b — TickGroups + HasTickGroups moved to ReplayTabViewModel. EntityTab's
    // seek pipelines never consumed the collection directly (they operate on
    // frame indices), so this is a pure relocation. Legacy MainViewModel's
    // pass-through shims for HasTickGroups now route to ReplayTab.

    // ── Seek-lifecycle internals ──────────────────────────────────────────────
    /// <summary>
    ///     The authoritative tracker. EntityTab no longer OWNS the
    ///     tracker instance: this is a pass-through to <c>PlaybackController.AuthoritativeTracker</c>
    ///     via <see cref="TrackerSource" />. EntityTab's async seeks build a fresh tracker and publish
    ///     it to the controller (<see cref="PublishTracker" />); reads here see the controller's
    ///     current instance. Falls back to a locally-held instance only when the source is unwired (the
    ///     XAML designer / tests that don't construct a controller).
    /// </summary>
    internal EntityTracker? CurrentTrackerInternal
    {
        get => TrackerSource is not null ? TrackerSource() : _localTracker;
        set
        {
            _localTracker = value;
            if (value is not null)
            {
                PublishTracker?.Invoke(value);
            }
        }
    }

    /// <summary>
    ///     Pass-through source for the authoritative tracker — wired by MainViewModel to
    ///     <c>() => Playback.AuthoritativeTracker</c>. When set, reads of
    ///     <see cref="CurrentTrackerInternal" /> come from the controller, not EntityTab's local field.
    /// </summary>
    public Func<EntityTracker?>? TrackerSource { get; set; }

    /// <summary>
    ///     Publishes a freshly-built tracker to the controller as the new authoritative instance —
    ///     wired by MainViewModel to <c>Playback.PublishTracker</c> (atomic swap-in).
    /// </summary>
    public Action<EntityTracker>? PublishTracker { get; set; }

    /// <summary>Delta-per-tick log for the selected entity.</summary>
    public DeltaLogViewModel DeltaLog { get; } = new();

    /// <summary>Relationship inspector tree for the selected entity (recursive handle references → other entities).</summary>
    public EntityInspectorViewModel EntityInspector { get; } = new();

    /// <summary>Entity field nodes.</summary>
    public ObservableCollection<PayloadNode> EntityFieldNodes { get; } = [];

    // ── Stable-reference collections ──────────────────────────────────────────
    /// <summary>Entity groups.</summary>
    public ObservableCollection<EntityGroup> EntityGroups { get; } = [];

    // ── Sub-view-models ────────────────────────────────────────────────────────
    /// <summary>Virtualized entity TreeDataGrid.</summary>
    public EntityListViewModel EntityList { get; } = new();

    /// <summary>Entity list items.</summary>
    public ObservableCollection<EntityListItem> EntityListItems { get; } = [];

    /// <summary>First entity with changed fields after the last delta rebuild — used for auto-select.</summary>
    internal EntityState? FirstChangedEntity { get; set; }

    /// <summary>
    ///     Source of the parsed frame list. MainViewModel sets this so the seek
    ///     pipelines can advance the tracker without holding a hard reference back
    ///     to the shell. Returning <c>null</c> aborts the seek.
    /// </summary>
    public Func<List<DemoFrame>?>? FrameSource { get; set; }

    /// <summary>Navigation.</summary>
    public FrameNavigationViewModel Navigation { get; }

    /// <summary>
    ///     Fires whenever <see cref="SelectedEntityItem" /> effectively changes (including
    ///     to null). MainViewModel wires this to refresh the parse chain. The argument
    ///     is the new entity (or null when selection cleared).
    /// </summary>
    public Action<EntityState?>? OnEntitySelectionChanged { get; set; }

    /// <summary>
    ///     Fires after the tracker has been advanced and groups rebuilt. MainViewModel
    ///     wires this to re-decode <c>entity_data</c> nodes in the currently-selected
    ///     PacketEntities card. Invoked on the UI thread (the await chain inside the
    ///     seek methods marshals back via Avalonia's SynchronizationContext).
    /// </summary>
    public Action? OnSeekCompleted { get; set; }

    /// <summary>
    ///     Optional callback that runs at the end of every seek. MainViewModel uses it
    ///     to clear <c>Debugger.Suppress</c> (the JumpToHitFrame helper sets this so
    ///     the back-navigation seek doesn't re-fire the same breakpoint).
    /// </summary>
    public Action? OnSeekFinally { get; set; }

    /// <summary>Snapshot of entity state just before the current frame/tick. Used for delta display.</summary>
    internal Dictionary<int, Dictionary<string, object?>>? PrevTickSnapshot { get; set; }

    /// <summary>
    ///     Optional shell-side hook to push tracker counters onto the DebuggerPanel
    ///     each time a seek completes. Invoked with the freshly-advanced tracker.
    /// </summary>
    public Action<EntityTracker?>? PublishTrackerStats { get; set; }

    /// <summary>Cancellation source for in-flight entity seeks. Mutated from three async pipelines.</summary>
    internal CancellationTokenSource? SeekCts { get; set; }

    /// <summary>Bottom serializer/ServerClass schema strip for the active class (F8.6).</summary>
    public SerializerSchemaViewModel SerializerSchema { get; } = new();

    /// <summary>Watched values.</summary>
    public ObservableCollection<WatchedValue> WatchedValues { get; } = [];

    /// <summary>
    ///     Demo-unload reset: cancels any in-flight seek and drops every entity-scale reference this tab
    ///     holds. The node/delta/inspector trees all reference <see cref="EntityState" />s owned by an
    ///     <see cref="EntityTracker" />, which in turn holds the decoded baselines and class shapes — so
    ///     a standalone close has to clear them here, not just null the tracker.
    /// </summary>
    internal void ResetForDemoUnload()
    {
        SeekCts?.Cancel();
        SeekCts = null;
        CurrentTrackerInternal = null; // setter nulls the local field; a null never publishes to the controller
        PrevTickSnapshot = null;
        FirstChangedEntity = null;
        _allEntityNodes = [];
        EntityFieldNodes.Clear();
        EntityGroups.Clear();
        EntityListItems.Clear();
        WatchedValues.Clear();
        EntityList.Clear();
        DeltaLog.Clear();
        EntityInspector.Clear();
        SerializerSchema.Clear();
    }

    /// <summary>
    ///     Synchronous entity-tree rebuild from the incrementally-stepped authoritative tracker, used
    ///     by the controller's <c>StepForward</c> / play loop. Mirrors the post-seek UI build the async
    ///     pipelines do, minus the debounce / Task.Run (the step already happened on the UI thread).
    /// </summary>
    public void RebuildFromSteppedTracker(
        EntityTracker tracker, Dictionary<int, Dictionary<string, object?>>? prevSnapshot)
    {
        PrevTickSnapshot = prevSnapshot;
        PublishTrackerStats?.Invoke(tracker);

        if (prevSnapshot is not null)
        {
            RebuildEntityGroupsWithDelta(tracker, prevSnapshot);
        }
        else
        {
            RebuildEntityGroups(tracker);
        }

        AutoSelectFirstChangedEntity();
        UpdateWatchedValues(tracker);

        OnSeekCompleted?.Invoke();

        EntityStatusText = tracker.LastEntityError is { } err
            ? $"⚠ {err}"
            : $"Tick {tracker.CurrentTick}  •  {EntityGroups.Sum(g => g.Entities.Count)} entities";
    }

    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>Fired on the UI thread whenever entity state is rebuilt (after seeking).</summary>
    public event Action? EntitiesRefreshed;

    /// <summary>Restores the class-browser filter from a persisted session. Best-effort.</summary>
    public void RestoreState(TabSessionState s)
    {
        if (s.SelectedNodePath is { Length: > 0 } filter)
        {
            ClassBrowser.Filter = filter;
        }
    }

    // ── Async seek pipelines ──────────────────────────────────────────────────
    /// <summary>
    ///     Seeks the entity tracker to the given frame, computes the prev-tick
    ///     snapshot, and rebuilds the entity groups + list. Mirrors the legacy
    ///     <c>MainViewModel.SeekEntitiesAsync</c> (which delegates here).
    /// </summary>
    public async Task SeekEntitiesAsync(int frameIndex)
    {
        CancellationTokenSource? oldCts = SeekCts;
        CancellationTokenSource cts = new();
        SeekCts = cts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsSeekingEntities = true;
        EntityStatusText = "Seeking…";

        try
        {
            List<DemoFrame>? frames = FrameSource?.Invoke();
            if (frames is null)
            {
                return;
            }

            if (SeekService is null)
            {
                throw new InvalidOperationException(
                    "EntityTrackingTabViewModel.SeekService must be wired before SeekEntitiesAsync.");
            }

            SeekResult result = await Task.Run(() => SeekService.SeekToFrame(frameIndex, frames), cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            EntityTracker tracker = result.Tracker;
            Dictionary<int, Dictionary<string, object?>>? snapshot = result.PrevSnapshot;

            PrevTickSnapshot = snapshot;
            CurrentTrackerInternal = tracker;
            PublishTrackerStats?.Invoke(tracker);

            if (snapshot is not null)
            {
                RebuildEntityGroupsWithDelta(tracker, snapshot);
            }
            else
            {
                RebuildEntityGroups(tracker);
            }

            AutoSelectFirstChangedEntity();
            UpdateWatchedValues(tracker);

            OnSeekCompleted?.Invoke();

            EntityStatusText = tracker.LastEntityError is { } err
                ? $"⚠ {err}"
                : $"Tick {tracker.CurrentTick}  •  {EntityGroups.Sum(g => g.Entities.Count)} entities";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer seek (or the demo closed mid-seek) — the newer seek owns the
            // status line, so a stale "cancelled" note here would only fight it.
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "seek the entity state", ex);
            EntityStatusText = UserFacingError.Describe("seek the entity state", ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsSeekingEntities = false;
            }

            OnSeekFinally?.Invoke();
        }
    }

    /// <summary>
    ///     Variant used when the user clicks a frame inside the tick-view list: the
    ///     previous snapshot is preserved (so the delta highlights persist across
    ///     the in-tick navigation) and only the tracker is re-advanced to the new
    ///     per-frame position. See legacy <c>SeekEntitiesForTickFrameAsync</c>.
    /// </summary>
    public async Task SeekEntitiesForTickFrameAsync(int frameIndex)
    {
        CancellationTokenSource? oldCts = SeekCts;
        CancellationTokenSource cts = new();
        SeekCts = cts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsSeekingEntities = true;
        EntityStatusText = "Seeking…";

        try
        {
            List<DemoFrame>? frames = FrameSource?.Invoke();
            if (frames is null)
            {
                return;
            }

            if (SeekService is null)
            {
                throw new InvalidOperationException(
                    "EntityTrackingTabViewModel.SeekService must be wired before SeekEntitiesForTickFrameAsync.");
            }

            SeekResult result = await Task.Run(
                () => SeekService.SeekToFrameNoSnapshot(frameIndex, frames), cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            EntityTracker tracker = result.Tracker;

            CurrentTrackerInternal = tracker;
            PublishTrackerStats?.Invoke(tracker);

            if (PrevTickSnapshot is not null)
            {
                RebuildEntityGroupsWithDelta(tracker, PrevTickSnapshot);
            }
            else
            {
                RebuildEntityGroups(tracker);
            }

            AutoSelectFirstChangedEntity();
            UpdateWatchedValues(tracker);

            OnSeekCompleted?.Invoke();

            EntityStatusText = tracker.LastEntityError is { } err
                ? $"⚠ {err}"
                : $"Tick {tracker.CurrentTick}  •  {EntityGroups.Sum(g => g.Entities.Count)} entities";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer seek (or the demo closed mid-seek) — the newer seek owns the
            // status line, so a stale "cancelled" note here would only fight it.
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "seek the entity state", ex);
            EntityStatusText = UserFacingError.Describe("seek the entity state", ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsSeekingEntities = false;
            }

            OnSeekFinally?.Invoke();
        }
    }

    /// <summary>
    ///     Variant used when the user picks a TickGroup in the Replay tab: snapshots
    ///     at the end of the previous tick (or start when this is tick 0) and then
    ///     advances to the end of the chosen tick.
    /// </summary>
    public async Task SeekEntitiesWithDeltaAsync(TickGroup tickGroup)
    {
        CancellationTokenSource? oldCts = SeekCts;
        CancellationTokenSource cts = new();
        SeekCts = cts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsSeekingEntities = true;
        EntityStatusText = "Seeking…";

        try
        {
            List<DemoFrame>? frames = FrameSource?.Invoke();
            if (frames is null)
            {
                return;
            }

            if (SeekService is null)
            {
                throw new InvalidOperationException(
                    "EntityTrackingTabViewModel.SeekService must be wired before SeekEntitiesWithDeltaAsync.");
            }

            int snapshotAt = Math.Max(0, tickGroup.StartFrameIndex - 1);
            int endFrameIdx = tickGroup.EndFrameIndex;
            bool takeSnapshot = tickGroup.StartFrameIndex > 0;

            SeekResult result = await Task.Run(
                () => SeekService.SeekToFrameWithSnapshotAt(snapshotAt, endFrameIdx, takeSnapshot, frames),
                cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            EntityTracker tracker = result.Tracker;
            Dictionary<int, Dictionary<string, object?>>? snapshot = result.PrevSnapshot;

            PrevTickSnapshot = snapshot;
            CurrentTrackerInternal = tracker;
            PublishTrackerStats?.Invoke(tracker);

            if (snapshot is not null)
            {
                RebuildEntityGroupsWithDelta(tracker, snapshot);
            }
            else
            {
                RebuildEntityGroups(tracker);
            }

            AutoSelectFirstChangedEntity();
            UpdateWatchedValues(tracker);

            EntityStatusText = tracker.LastEntityError is { } err
                ? $"⚠ {err}"
                : $"Tick {tracker.CurrentTick}  •  {EntityGroups.Sum(g => g.Entities.Count)} entities";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer seek (or the demo closed mid-seek) — the newer seek owns the
            // status line, so a stale "cancelled" note here would only fight it.
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "seek the entity state", ex);
            EntityStatusText = UserFacingError.Describe("seek the entity state", ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsSeekingEntities = false;
            }

            OnSeekFinally?.Invoke();
        }
    }

    // ── Session state ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Snapshots the Entity tab's durable selection. The only state that survives a reparse
    ///     cleanly is the class-browser filter (entity instances are re-created each load), so it is
    ///     carried in <c>SelectedNodePath</c>. Frame index is shell-owned; the parser-specific
    ///     <c>ShowRawHex</c> field is unused here.
    /// </summary>
    public TabSessionState SnapshotState() => new(
        null,
        ClassBrowser.Filter is { Length: > 0 } f ? f : null,
        false);

    internal void AutoSelectFirstChangedEntity()
    {
        if (FirstChangedEntity is null)
        {
            return;
        }

        EntityListItem? item = EntityListItems.FirstOrDefault(i => i.Entity == FirstChangedEntity);
        if (item is not null)
        {
            SelectedEntityListItem = item;
        }
    }

    /// <summary>
    ///     Repopulates <see cref="EntityFieldNodes" /> from <see cref="AllEntityFieldNodes" />,
    ///     applying the delta-only filter when <see cref="ShowDeltaFieldsOnly" /> is set.
    /// </summary>
    internal void FilterAndSetEntityFieldNodes()
    {
        EntityFieldNodes.Clear();
        if (AllEntityFieldNodes is null)
        {
            return;
        }

        foreach (PayloadNode node in AllEntityFieldNodes)
        {
            if (!ShowDeltaFieldsOnly || node.IsDelta)
            {
                EntityFieldNodes.Add(node);
            }
        }
    }

    /// <summary>Internal helper used by MainViewModel to raise <see cref="EntitiesRefreshed" />.</summary>
    internal void RaiseEntitiesRefreshed() => EntitiesRefreshed?.Invoke();

    internal void RebuildEntityGroups(EntityTracker tracker)
    {
        List<EntityGroup> grouped = LiveEntities(tracker)
            .GroupBy(e => Categorise(e.ClassName))
            .OrderBy(g => g.Key)
            .Select(g => new EntityGroup
            {
                Name = g.Key,
                Entities = g.OrderBy(e => e.ClassName).ToList()
            })
            .ToList();

        EntityGroups.Clear();
        foreach (EntityGroup group in grouped)
        {
            EntityGroups.Add(group);
        }

        HasEntities = EntityGroups.Count > 0;
        RebuildEntityListItems(tracker, null);
    }

    internal void RebuildEntityGroupsWithDelta(
        EntityTracker tracker,
        Dictionary<int, Dictionary<string, object?>> prevSnapshot)
    {
        HashSet<int> changedSlots = new();
        HashSet<int> displayChangedSlots = new();
        foreach ((int idx, EntityState entity) in LiveEntitiesIndexed(tracker))
        {
            if (!prevSnapshot.TryGetValue(idx, out Dictionary<string, object?>? prevFields))
            {
                changedSlots.Add(idx);
                continue;
            }

            foreach (KeyValuePair<string, object?> kv in entity.Fields)
            {
                string currFormatted = MainViewModel.FormatValue(kv.Value);
                if (!prevFields.TryGetValue(kv.Key, out object? prevVal))
                {
                    changedSlots.Add(idx);
                    displayChangedSlots.Add(idx);
                    break;
                }

                if (MainViewModel.FormatValue(prevVal) != currFormatted)
                {
                    changedSlots.Add(idx);
                    displayChangedSlots.Add(idx);
                    break;
                }
            }
        }

        FirstChangedEntity = null;
        List<EntityGroup> grouped = LiveEntitiesIndexed(tracker)
            .GroupBy(t => Categorise(t.Entity.ClassName))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                List<(int Index, EntityState Entity)> entities = g.OrderBy(t => t.Entity.ClassName).ToList();
                int deltaCount = g.Count(t => changedSlots.Contains(t.Index));

                if (FirstChangedEntity is null && deltaCount > 0)
                {
                    foreach ((int Index, EntityState Entity) t in entities)
                    {
                        if (displayChangedSlots.Contains(t.Index))
                        {
                            FirstChangedEntity = t.Entity;
                            break;
                        }
                    }

                    if (FirstChangedEntity is null)
                    {
                        foreach ((int Index, EntityState Entity) t in entities)
                        {
                            if (changedSlots.Contains(t.Index))
                            {
                                FirstChangedEntity = t.Entity;
                                break;
                            }
                        }
                    }
                }

                return new EntityGroup
                {
                    Name = g.Key,
                    Entities = entities.Select(t => t.Entity).ToList(),
                    DeltaCount = deltaCount,
                    IsExpanded = deltaCount > 0
                };
            })
            .ToList();

        EntityGroups.Clear();
        foreach (EntityGroup group in grouped)
        {
            EntityGroups.Add(group);
        }

        HasEntities = EntityGroups.Count > 0;
        RebuildEntityListItems(tracker, prevSnapshot);
    }

    internal void RebuildEntityListItems(
        EntityTracker tracker,
        Dictionary<int, Dictionary<string, object?>>? prevSnapshot)
    {
        List<EntityListItem> changed = new();
        List<EntityListItem> unchanged = new();

        foreach ((int idx, EntityState entity) in LiveEntitiesIndexed(tracker)
                     .OrderBy(t => t.Entity.ClassName))
        {
            int delta = 0;
            if (prevSnapshot is not null && prevSnapshot.TryGetValue(idx, out Dictionary<string, object?>? prev))
            {
                foreach (KeyValuePair<string, object?> kv in entity.Fields)
                {
                    string curr = MainViewModel.FormatValue(kv.Value);
                    if (!prev.TryGetValue(kv.Key, out object? pv) || MainViewModel.FormatValue(pv) != curr)
                    {
                        delta++;
                    }
                }
            }

            EntityListItem item = new()
            {
                Entity = entity,
                DeltaCount = delta,
                SlotIndex = idx
            };
            if (delta > 0)
            {
                changed.Add(item);
            }
            else
            {
                unchanged.Add(item);
            }
        }

        changed.Sort((a, b) => b.DeltaCount != a.DeltaCount
            ? b.DeltaCount.CompareTo(a.DeltaCount)
            : string.Compare(a.Entity!.ClassName, b.Entity!.ClassName, StringComparison.Ordinal));

        SelectedEntityListItem = null;
        EntityListItems.Clear();
        foreach (EntityListItem item in changed)
        {
            EntityListItems.Add(item);
        }

        if (changed.Count > 0 && unchanged.Count > 0)
        {
            EntityListItems.Add(new EntityListItem
            {
                IsHeader = true
            });
        }

        foreach (EntityListItem item in unchanged)
        {
            EntityListItems.Add(item);
        }

        // Build the entity-grid node list (changed-first, same ordering as the
        // legacy list) and push it through the class filter. Slot index is carried on the
        // EntityListItem so MakeNode is O(1). Class browser rebuilds from the tracker's
        // registry so the rail stays current as classes are discovered.
        List<EntityNode> nodes = new(changed.Count + unchanged.Count);
        foreach (EntityListItem item in changed)
        {
            if (item.Entity is { } e)
            {
                nodes.Add(MakeNode(e, item.SlotIndex, item.DeltaCount));
            }
        }

        foreach (EntityListItem item in unchanged)
        {
            if (item.Entity is { } e)
            {
                nodes.Add(MakeNode(e, item.SlotIndex, item.DeltaCount));
            }
        }

        _allEntityNodes = nodes;
        ClassBrowser.Rebuild(tracker);
        ApplyClassFilterToNodes();

        RaiseEntitiesRefreshed();
    }

    /// <summary>
    ///     Re-runs the most recent group/list rebuild with the current
    ///     <see cref="ShowDormantEntities" /> filter. Used as the partial handler for
    ///     <c>OnShowDormantEntitiesChanged</c>; safe to call any time.
    /// </summary>
    internal void RefreshEntityView()
    {
        if (CurrentTrackerInternal is not { } tracker)
        {
            return;
        }

        if (PrevTickSnapshot is not null)
        {
            RebuildEntityGroupsWithDelta(tracker, PrevTickSnapshot);
        }
        else
        {
            RebuildEntityGroups(tracker);
        }
    }

    internal void UpdateWatchedValues(EntityTracker tracker)
    {
        foreach (WatchedValue w in WatchedValues)
        {
            EntityState? entity = tracker.CurrentEntities.All()
                .FirstOrDefault(e => e.ClassName == w.EntityClassName && e.Serial == w.EntitySerial);
            w.CurrentValue = entity is not null && entity.Fields.TryGetValue(w.FieldKey, out object? v)
                ? MainViewModel.FormatValue(v)
                : "<not found>";
        }
    }

    // ── Watched-field commands ────────────────────────────────────────────────
    internal void WatchField(EntityState entity, string fieldKey)
    {
        if (WatchedValues.Any(w => w.EntityClassName == entity.ClassName
                                   && w.EntitySerial == entity.Serial
                                   && w.FieldKey == fieldKey))
        {
            return;
        }

        WatchedValue wv = null!;
        wv = new WatchedValue
        {
            Label = $"{entity.ClassName}.{fieldKey}",
            EntityClassName = entity.ClassName,
            EntitySerial = entity.Serial,
            FieldKey = fieldKey,
            CurrentValue = entity.Fields.TryGetValue(fieldKey, out object? v)
                ? MainViewModel.FormatValue(v)
                : "",
            RemoveCommand = new RelayCommand(() => WatchedValues.Remove(wv))
        };
        WatchedValues.Add(wv);
    }

    private void ApplyClassFilterToNodes()
    {
        List<EntityNode> visible = _classFilter is null
            ? _allEntityNodes
            : _allEntityNodes.FindAll(n => n.ClassName == _classFilter);
        EntityList.Rebuild(visible);
    }

    private static string Categorise(string name) => name switch
    {
        _ when name.Contains("Player", StringComparison.Ordinal) || name.Contains("Controller", StringComparison.Ordinal)
            => "Players",
        _ when name.Contains("Weapon", StringComparison.Ordinal) || name.Contains("Knife", StringComparison.Ordinal)
                                                                 || name.Contains("Pistol", StringComparison.Ordinal) || name.Contains("Rifle", StringComparison.Ordinal)
                                                                 || name.Contains("Machine", StringComparison.Ordinal) || name.Contains("Shotgun", StringComparison.Ordinal)
                                                                 || name.Contains("Sniper", StringComparison.Ordinal)
            => "Weapons",
        _ when name.Contains("Grenade", StringComparison.Ordinal) || name.Contains("Molotov", StringComparison.Ordinal)
                                                                  || name.Contains("Smoke", StringComparison.Ordinal) || name.Contains("Inferno", StringComparison.Ordinal)
                                                                  || name.Contains("Flash", StringComparison.Ordinal) || name.Contains("Decoy", StringComparison.Ordinal)
                                                                  || name.Contains("Bomb", StringComparison.Ordinal)
            => "Grenades / Projectiles",
        _ when name.StartsWith("CCSGame", StringComparison.Ordinal) || name.StartsWith("CCSTeam", StringComparison.Ordinal)
                                                                    || name.StartsWith("CCSMatch", StringComparison.Ordinal) || name.StartsWith("CCSScore", StringComparison.Ordinal)
            => "Game",
        _ when name.StartsWith("CDynamic", StringComparison.Ordinal) || name.StartsWith("CPhys", StringComparison.Ordinal)
                                                                     || name.StartsWith("CBaseProp", StringComparison.Ordinal) || name.StartsWith("CProp", StringComparison.Ordinal)
            => "Props",
        _ when name.StartsWith("CEnv", StringComparison.Ordinal) || name.StartsWith("CWorld", StringComparison.Ordinal)
                                                                 || name.StartsWith("CLightEntity", StringComparison.Ordinal) || name.StartsWith("CSky", StringComparison.Ordinal)
                                                                 || name.StartsWith("CFog", StringComparison.Ordinal) || name.StartsWith("CColor", StringComparison.Ordinal)
            => "World / Environment",
        _ when name.StartsWith("CParticle", StringComparison.Ordinal) || name.StartsWith("CEffect", StringComparison.Ordinal)
                                                                      || name.StartsWith("CSVC", StringComparison.Ordinal)
            => "Effects",
        _ => "Other"
    };

    // ── Entity grouping helpers ───────────────────────────────────────────────
    private IEnumerable<EntityState> LiveEntities(EntityTracker t)
        => ShowDormantEntities ? t.CurrentEntities.All() : t.CurrentEntities.AllInPvs();

    private IEnumerable<(int Index, EntityState Entity)> LiveEntitiesIndexed(EntityTracker t)
        => ShowDormantEntities ? t.CurrentEntities.AllIndexed() : t.CurrentEntities.AllInPvsIndexed();

    private static EntityNode MakeNode(EntityState entity, int index, int deltaCount) =>
        new()
        {
            Index = index,
            ClassName = entity.ClassName,
            Serial = entity.Serial,
            Dormant = !entity.IsInPvs,
            DeltaCount = deltaCount,
            Entity = entity
        };

    private void OnClassFilterChanged(string? className)
    {
        _classFilter = className;
        ApplyClassFilterToNodes();
        // Surface the schema for the chosen class (cleared when filter is "all").
        SerializerSchema.Show(CurrentTrackerInternal, className);
    }

    partial void OnShowRelationshipTreeChanged(bool value)
    {
        if (value && SelectedEntityItem is EntityState entity)
        {
            EntityInspector.BuildFor(entity, CurrentTrackerInternal);
        }
    }

    partial void OnSelectedEntityItemChanged(object? value)
    {
        AllEntityFieldNodes = null;
        ShowDeltaFieldsOnly = false;
        EntityFieldNodes.Clear();
        EntityFieldRows = [];
        EntityHeaderText = "";
        HasEntitySelection = false;
        EntityDeltaFieldCount = 0;

        if (value is not EntityState entity)
        {
            // Notify shell so it can refresh the parse-chain for a non-entity context.
            DeltaLog.Clear();
            EntityInspector.Clear();
            OnEntitySelectionChanged?.Invoke(null);
            return;
        }

        // Rebuild the relationship inspector only while its panel is showing.
        if (ShowRelationshipTree)
        {
            EntityInspector.BuildFor(entity, CurrentTrackerInternal);
        }

        Dictionary<string, object?>? prevFields = null;
        if (PrevTickSnapshot is not null && CurrentTrackerInternal is not null)
        {
            foreach ((int idx, EntityState e) in CurrentTrackerInternal.CurrentEntities.AllIndexed())
            {
                if (ReferenceEquals(e, entity))
                {
                    PrevTickSnapshot.TryGetValue(idx, out prevFields);
                    break;
                }
            }
        }

        AllEntityFieldNodes =
        [
            .. entity.Fields.Select(kv =>
            {
                string currVal = MainViewModel.FormatValue(kv.Value);
                string? prevVal = null;
                if (prevFields is not null && prevFields.TryGetValue(kv.Key, out object? pv))
                {
                    string prevFormatted = MainViewModel.FormatValue(pv);
                    if (prevFormatted != currVal)
                    {
                        prevVal = prevFormatted;
                    }
                }

                return new PayloadNode
                {
                    Name = kv.Key,
                    Value = currVal,
                    PreviousValue = prevVal,
                    WatchCommand = new RelayCommand(() => WatchField(entity, kv.Key))
                };
            })
        ];

        EntityFieldRows = AllEntityFieldNodes
            .Select(n => new KvpRow(n.Name, n.Value, n.IsDelta, n.PreviousValue))
            .ToList();

        EntityDeltaFieldCount = AllEntityFieldNodes.Count(n => n.IsDelta);
        EntityHeaderText = EntityDeltaFieldCount > 0
            ? $"{entity.ClassName}  (serial {entity.Serial})  •  {EntityDeltaFieldCount} changed"
            : $"{entity.ClassName}  (serial {entity.Serial})";
        HasEntitySelection = true;

        // Feed the delta log (changed fields prev → curr) and surface the
        // selected entity's serializer schema in the bottom strip.
        List<(string Field, string Prev, string Curr)> changes = AllEntityFieldNodes
            .Where(n => n.IsDelta)
            .Select(n => (n.Name, n.PreviousValue ?? "", n.Value ?? ""))
            .ToList();
        DeltaLog.Show(CurrentTrackerInternal?.CurrentTick ?? 0,
            $"{entity.ClassName} (serial {entity.Serial})", changes);
        SerializerSchema.Show(CurrentTrackerInternal, entity.ClassName);

        // Keep the grid row highlighted when the selection originated upstream
        // (auto-select after a delta seek, or the legacy list). No-op if filtered out.
        EntityList.SelectByEntity(entity);

        FilterAndSetEntityFieldNodes();
        OnEntitySelectionChanged?.Invoke(entity);
    }

    partial void OnSelectedEntityListItemChanged(EntityListItem? value)
    {
        if (value is { IsHeader: false, Entity: not null })
        {
            SelectedEntityItem = value.Entity;
        }
    }

    partial void OnShowDeltaFieldsOnlyChanged(bool value) => FilterAndSetEntityFieldNodes();

    partial void OnShowDormantEntitiesChanged(bool value) => RefreshEntityView();
}
