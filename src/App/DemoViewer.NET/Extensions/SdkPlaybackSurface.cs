#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Tools;
using DemoViewer.NET.Playback2D.Core.Zones;
using Core = DemoViewer.NET.Playback2D.Core.Timeline;
using SdkP = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>Hosts an SDK playback contribution on the app's own surface, every call into it guarded.</summary>
internal sealed class SdkPlaybackContribution(SdkP.IPlaybackContribution inner, ExtensionGuard guard)
    : IPlaybackContribution, IDisposable
{
    private SdkPlaybackSurface? _surface;

    public SdkP.IPlaybackContribution Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        _surface = new SdkPlaybackSurface(surface, guard);
        SdkPlaybackSurface attached = _surface;
        guard.Run("playback attach", () => Inner.Attach(attached, context));
    }

    // Detach undoes what Attach added even when the extension forgot a handle or threw.
    public void Detach()
    {
        guard.Run("playback detach", Inner.Detach);
        _surface?.Dispose();
        _surface = null;
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }
}

/// <summary>A first-party playback contribution with its attach and detach run as its extension's.</summary>
internal sealed class GuardedPlaybackContribution(IPlaybackContribution inner, ExtensionGuard guard) : IPlaybackContribution
{
    public IPlaybackContribution Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Attach(IPlaybackSurface surface, IModuleContext context) =>
        guard.Run("playback attach", () => Inner.Attach(surface, context));

    public void Detach() => guard.Run("playback detach", Inner.Detach);
}

/// <summary>
///     The SDK's view of one 2D Playback tab, over the app's surface. Every handler and factory the extension
///     hands in is wrapped once, at registration, so a throw is reported against the extension and the tab
///     carries on: the frame loop, the keymap and the timeline never see an extension's exception.
/// </summary>
internal sealed class SdkPlaybackSurface : SdkP.IPlaybackSurface, IDisposable
{
    private readonly IPlaybackSurface _surface;
    private readonly ExtensionGuard _guard;
    private readonly List<IDisposable> _owned = [];

    public SdkPlaybackSurface(IPlaybackSurface surface, ExtensionGuard guard)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _surface.KeymapChanged += OnKeymapChanged;
        _surface.Deactivated += OnDeactivated;
    }

    public IReadOnlyList<SdkP.PlaybackLevel> Levels =>
        [.. _surface.MapLevels.Select(l => new SdkP.PlaybackLevel(l.Name, l.ZMin, l.ZMax))];

    public IReadOnlyList<string> Places =>
        _surface.Zones is { } zones
            ? [.. zones.Zones.Places.Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)]
            : [];

    public IReadOnlyCollection<string> ActionIds => CommandRegistry.Default.ActionIds;

    public event Action? KeymapChanged;

    public event Action? Deactivated;

    public IDisposable OnDemoChanged(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Own(_surface.OnDemoChanged(_guard.Wrap("demo change handler", handler)));
    }

    public IDisposable OnPlayheadChanged(Action<int> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Own(_surface.OnPlayheadChanged(_guard.Wrap("playhead handler", handler, FaultKind.Recurring)));
    }

    public string? PlaceAt(string? level, double worldX, double worldY)
    {
        IReadOnlyList<MapLevel> levels = _surface.MapLevels;
        MapLevel? floor = level is null
            ? levels.Count == 1 ? levels[0] : null
            : levels.FirstOrDefault(l => string.Equals(l.Name, level, StringComparison.Ordinal));
        return floor is null ? null : PlaceOnFloor(_surface.Zones, floor, worldX, worldY);
    }

    public string GestureHint(string actionId) => actionId is null ? "" : _surface.GestureHint(actionId);

    public string? ActionFor(string scope, Key key, KeyModifiers modifiers) =>
        !string.IsNullOrEmpty(scope)
        && _surface.Keymap.TryResolveInScope(new Playback2DBindingScope(scope), key, modifiers, out string? actionId)
            ? actionId
            : null;

    public SdkP.ILaneHandle AddLane(SdkP.ITimelineTrack track, SdkP.ILaneBehaviour? behaviour = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        CoreTrack core = new(track, _guard);
        ILaneHandle handle = _surface.AddLane(core, TimelineBandRow.Lane,
            behaviour is null ? null : new LaneBehaviour(behaviour, _guard));
        LaneHandle lane = new(track, core, handle);
        _owned.Add(lane);
        return lane;
    }

    public IDisposable AddBandMenu(Func<SdkP.PlaybackBand, IEnumerable<SdkP.MenuEntry>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Func<SdkP.PlaybackBand, IEnumerable<SdkP.MenuEntry>> guarded =
            _guard.Wrap<SdkP.PlaybackBand, IEnumerable<SdkP.MenuEntry>>("band menu", b => [.. items(b)], []);
        return Own(_surface.AddBandMenu(band => Menu(guarded(Band(band)), _guard)));
    }

    public IDisposable AddModeToggle(SdkP.ModeToggle toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        ExtensionGuards.Register(toggle, _guard);
        return Own(_surface.AddModeToggle(toggle));
    }

    public IDisposable AddToolbarItem(SdkP.ToolbarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Func<SdkP.PlaybackMoment, bool> run = _guard.Wrap("toolbar item", item.Run, false);
        ToolbarItem mirrored = new(item.Id, item.Label, item.Tooltip,
            frame => run(new SdkP.PlaybackMoment(frame.Time.Tick, frame.Time.FrameIndex, frame)), item.ActionId, item.Order,
            item.Icon, item.MenuHeader);

        void Sync(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            _guard.Run("toolbar item", () =>
            {
                mirrored.Label = item.Label;
                mirrored.Tooltip = item.Tooltip;
                mirrored.MenuHeader = item.MenuHeader;
            });

        item.PropertyChanged += Sync;
        IDisposable added = _surface.AddToolbarItem(mirrored);
        return Own(new Registration(() =>
        {
            item.PropertyChanged -= Sync;
            added.Dispose();
        }));
    }

    public IDisposable AddPointerPreHandler(Func<SdkP.PlaybackPointer, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<SdkP.PlaybackPointer, bool> guarded = _guard.Wrap("pointer handler", handler, false, FaultKind.Recurring);
        return Own(_surface.AddPointerPreHandler(p => guarded(Pointer(p))));
    }

    // A pane or panel whose view model cannot be built shows the placeholder control in its place.
    public SdkP.IPaneHandle AddPane(SdkP.PanePlacement where, int order, Func<object> viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        SdkP.IPaneHandle pane = _surface.AddPane(where, order,
            () => _guard.Run("pane view model", viewModel, ExtensionPlaceholder.View(_guard.Scope, "this pane")));
        _owned.Add(pane);
        return pane;
    }

    public SdkP.IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null,
        SdkP.ModeToggle? mode = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Func<Control>? guardedView = view is null
            ? null
            : () => _guard.Run("panel view", view, ExtensionPlaceholder.View(_guard.Scope, "this panel"));
        if (mode is not null)
        {
            ExtensionGuards.Register(mode, _guard);
        }

        SdkP.IPanelHandle panel = _surface.AddPanel(order,
            () => _guard.Run("panel view model", viewModel, ExtensionPlaceholder.View(_guard.Scope, "this panel")),
            guardedView, featureId, mode);
        _owned.Add(panel);
        return panel;
    }

    public IDisposable AddLayer(string id, Func<ISceneLayer> layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        string filed = ExtensionLayerIds.Compose(_guard.Scope.Id, id);
        ISceneLayer Build() =>
            new GuardedSceneLayer(filed, _guard.Run("scene layer factory", layer, EmptyLayer.Instance), _guard);
        return Own(_surface.AddLayer(filed, Build));
    }

    public IDisposable AddTool(IMapTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Own(_surface.AddTool(new GuardedMapTool(tool, _guard)));
    }

    public IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<(Key Key, KeyModifiers Modifiers), bool> guarded =
            _guard.Wrap<(Key Key, KeyModifiers Modifiers), bool>("key handler", k => handler(k.Key, k.Modifiers), false,
                FaultKind.Recurring);
        return Own(_surface.AddKeyHandler((key, modifiers) => guarded((key, modifiers))));
    }

    public IDisposable AddActionHandler(Func<string, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<string, bool> guarded = _guard.Wrap("action handler", handler, false, FaultKind.Recurring);
        return Own(_surface.AddActionHandler(guarded));
    }

    public void Dispose()
    {
        _surface.KeymapChanged -= OnKeymapChanged;
        _surface.Deactivated -= OnDeactivated;
        foreach (IDisposable owned in _owned.ToArray())
        {
            owned.Dispose();
        }

        _owned.Clear();
    }

    internal static SdkP.PlaybackBand Band(TimelineBandViewModel band) =>
        new(band.TrackId, band.StartFrameIndex, band.EndFrameIndex, band.Label, band.Tooltip);

    private static List<MenuEntry> Menu(IEnumerable<SdkP.MenuEntry> entries, ExtensionGuard guard) =>
        [.. entries.Select(e => new MenuEntry(e.Header, guard.Wrap("menu entry", e.Run)))];

    private static SdkP.PlaybackPointer Pointer(ScenePointer p)
    {
        KeyModifiers keys = KeyModifiers.None;
        if (p.Modifiers.HasFlag(ToolModifiers.Shift))
        {
            keys |= KeyModifiers.Shift;
        }

        if (p.Modifiers.HasFlag(ToolModifiers.Control))
        {
            keys |= KeyModifiers.Control;
        }

        if (p.Modifiers.HasFlag(ToolModifiers.Alt))
        {
            keys |= KeyModifiers.Alt;
        }

        return new SdkP.PlaybackPointer(p.Level.Name, p.WorldX, p.WorldY, p.Screen.X, p.Screen.Y, keys, p.Frame.Time.Tick,
            () => PlaceOnFloor(p.Zones(), p.Level, p.WorldX, p.WorldY), p.Frame);
    }

    // A floor's key is its quantized lower bound, the same key the zone set's floors are stored under.
    private static string? PlaceOnFloor(PlaceResolver? zones, MapLevel floor, double worldX, double worldY) =>
        zones?.ResolveOnFloor(worldX, worldY, MapSpace.QuantizeZ(floor.ZMin)).Name;

    private IDisposable Own(IDisposable registration)
    {
        _owned.Add(registration);
        return registration;
    }

    // Every subscriber here is the extension's; each runs even when one before it throws.
    private void OnKeymapChanged() => RaiseEach(KeymapChanged, "keymap change handler");

    private void OnDeactivated() => RaiseEach(Deactivated, "deactivate handler");

    private void RaiseEach(Action? handlers, string site)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            _guard.Run(site, handler);
        }
    }

    // What a factory that threw builds instead: a layer that draws nothing.
    private sealed class EmptyLayer : ISceneLayer
    {
        public static readonly EmptyLayer Instance = new();

        public string Id => "";

        public LayerSlot Slot => LayerSlot.Overlay;

        public int Order => 0;

        public LayerCacheHint Cache => LayerCacheHint.Dynamic;

        public bool IsEnabled { get; set; }

        public int ContentVersion => 0;

        public bool Advance(in Playback2D.Core.SceneTime time, Playback2D.Core.Scene2DFrame frame) => false;

        public void Render(SkiaSharp.SKCanvas canvas, SceneRenderContext ctx)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class TimelineData(Core.ITimelineData data) : SdkP.ITimelineData
    {
        public int TotalFrames => data.TotalFrames;

        public int TickRate => data.TickRate;

        public int FrameIndexAtTick(int tick) => data.FrameIndexAtTick(tick);

        public IReadOnlyList<SdkP.TimelineEvent> EventsOfType(string eventName) =>
            [.. data.EventsOfType(eventName).Select(e => new SdkP.TimelineEvent(e.Tick, e.FrameIndex, e.Fields))];

        public bool HasEvent(string eventName) => data.HasEvent(eventName);
    }

    private sealed class CoreTrack : Core.ITimelineTrack
    {
        private readonly SdkP.ITimelineTrack _track;
        private readonly ExtensionGuard _guard;

        // The id and name are read once: the timeline reads them on every rebuild.
        public CoreTrack(SdkP.ITimelineTrack track, ExtensionGuard guard)
        {
            _track = track;
            _guard = guard;
            Id = guard.Run("timeline track", () => track.Id, guard.Scope.Id + ".track");
            DisplayName = guard.Run("timeline track", () => track.DisplayName, guard.Scope.Name);
            guard.Run("timeline track", () => _track.Changed += OnChanged);
        }

        public string Id { get; }

        public string DisplayName { get; }

        public event Action? MarkersChanged;

        public bool IsAvailable(Core.ITimelineData data) =>
            _guard.Run("timeline track", () => _track.IsAvailable(new TimelineData(data)), false);

        public IReadOnlyList<Core.TimelineMarker> BuildMarkers(Core.ITimelineData data) => [];

        public IReadOnlyList<Core.TimelineBand> BuildBands(Core.ITimelineData data) =>
            _guard.Run<IReadOnlyList<Core.TimelineBand>>("timeline track", () =>
            [
                .. _track.BuildBands(new TimelineData(data))
                    .Select(b => new Core.TimelineBand(Id, b.StartFrameIndex, b.EndFrameIndex, b.Label, b.Tooltip, b.Argb))
            ], []);

        public void Detach() => _guard.Run("timeline track", () => _track.Changed -= OnChanged);

        private void OnChanged() => MarkersChanged?.Invoke();
    }

    private sealed class LaneBehaviour(SdkP.ILaneBehaviour inner, ExtensionGuard guard) : ILaneBehaviour
    {
        public void OnBandPressed(TimelineBandViewModel band, Core.ITimelineData data) =>
            guard.Run("lane band press", () => inner.OnBandPressed(Band(band)));

        public IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, Core.ITimelineData data) =>
            Menu(guard.Run<IReadOnlyList<SdkP.MenuEntry>>("lane menu", () => [.. inner.MenuFor(Band(band))], []), guard);

        public void OnLabelRequested(int frame) => guard.Run("lane label", () => inner.OnLabelRequested(frame));

        public void OnEditSpanDragged(int startFrame, int endFrame) =>
            guard.Run("lane drag", () => inner.OnEditSpanDragged(startFrame, endFrame), FaultKind.Recurring);
    }

    private sealed class LaneHandle(SdkP.ITimelineTrack track, CoreTrack core, ILaneHandle handle) : SdkP.ILaneHandle
    {
        public SdkP.ITimelineTrack Track => track;

        public bool IsSuppressed
        {
            get => handle.IsSuppressed;
            set => handle.IsSuppressed = value;
        }

        public bool IsEditable
        {
            get => handle.IsEditable;
            set => handle.IsEditable = value;
        }

        public (int Start, int End)? EditSpan
        {
            get => handle.EditSpan;
            set => handle.EditSpan = value;
        }

        public void Dispose()
        {
            core.Detach();
            handle.Dispose();
        }
    }
}
