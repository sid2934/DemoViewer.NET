#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using Core = DemoViewer.NET.Playback2D.Core.Timeline;
using SdkP = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>Hosts an SDK playback contribution on the app's own surface.</summary>
internal sealed class SdkPlaybackContribution(SdkP.IPlaybackContribution inner) : IPlaybackContribution, IDisposable
{
    private SdkPlaybackSurface? _surface;

    public SdkP.IPlaybackContribution Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        _surface = new SdkPlaybackSurface(surface);
        Inner.Attach(_surface, context);
    }

    // Detach undoes what Attach added even when the extension forgot a handle.
    public void Detach()
    {
        try
        {
            Inner.Detach();
        }
        finally
        {
            _surface?.Dispose();
            _surface = null;
        }
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }
}

/// <summary>The SDK's view of one 2D Playback tab, over the app's surface.</summary>
internal sealed class SdkPlaybackSurface : SdkP.IPlaybackSurface, IDisposable
{
    private static readonly IReadOnlyCollection<string> _actionIds = Enum.GetNames<Playback2DAction>();

    private readonly IPlaybackSurface _surface;
    private readonly List<IDisposable> _owned = [];

    public SdkPlaybackSurface(IPlaybackSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _surface.KeymapChanged += OnKeymapChanged;
        _surface.Deactivated += OnDeactivated;
    }

    public IReadOnlyList<SdkP.PlaybackLevel> Levels =>
        [.. _surface.MapLevels.Select(l => new SdkP.PlaybackLevel(l.Name, l.ZMin, l.ZMax))];

    public IReadOnlyCollection<string> ActionIds => _actionIds;

    public event Action? KeymapChanged;

    public event Action? Deactivated;

    public IDisposable OnDemoChanged(Action handler) => Own(_surface.OnDemoChanged(handler));

    public IDisposable OnPlayheadChanged(Action<int> handler) => Own(_surface.OnPlayheadChanged(handler));

    public string GestureHint(string actionId) =>
        Enum.TryParse(actionId, false, out Playback2DAction action) ? _surface.GestureHint(action) : "";

    public SdkP.ILaneHandle AddLane(SdkP.ITimelineTrack track, SdkP.ILaneBehaviour? behaviour = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        CoreTrack core = new(track);
        ILaneHandle handle = _surface.AddLane(core, TimelineBandRow.Lane,
            behaviour is null ? null : new LaneBehaviour(behaviour));
        LaneHandle lane = new(track, core, handle);
        _owned.Add(lane);
        return lane;
    }

    public IDisposable AddBandMenu(Func<SdkP.PlaybackBand, IEnumerable<SdkP.MenuEntry>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Own(_surface.AddBandMenu(band => Menu(items(Band(band)))));
    }

    public IDisposable AddModeToggle(SdkP.ModeToggle toggle) => Own(_surface.AddModeToggle(toggle));

    public IDisposable AddToolbarItem(SdkP.ToolbarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Playback2DAction? action = item.ActionId is { } id && Enum.TryParse(id, false, out Playback2DAction parsed)
            ? parsed
            : null;
        ToolbarItem mirrored = new(item.Id, item.Label, item.Tooltip,
            frame => item.Run(new SdkP.PlaybackMoment(frame.Time.Tick, frame.Time.FrameIndex)), action, item.Order,
            item.Icon, item.MenuHeader);

        void Sync(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            mirrored.Label = item.Label;
            mirrored.Tooltip = item.Tooltip;
            mirrored.MenuHeader = item.MenuHeader;
        }

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
        return Own(_surface.AddPointerPreHandler(p => handler(Pointer(p))));
    }

    public SdkP.IPaneHandle AddPane(SdkP.PanePlacement where, int order, Func<object> viewModel)
    {
        SdkP.IPaneHandle pane = _surface.AddPane(where, order, viewModel);
        _owned.Add(pane);
        return pane;
    }

    public SdkP.IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null,
        SdkP.ModeToggle? mode = null)
    {
        SdkP.IPanelHandle panel = _surface.AddPanel(order, viewModel, view, featureId, mode);
        _owned.Add(panel);
        return panel;
    }

    public IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler) => Own(_surface.AddKeyHandler(handler));

    public IDisposable AddActionHandler(Func<string, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Own(_surface.AddActionHandler(action => handler(action.ToString())));
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

    private static List<MenuEntry> Menu(IEnumerable<SdkP.MenuEntry> entries) =>
        [.. entries.Select(e => new MenuEntry(e.Header, e.Run))];

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

        double floorKey = MapSpace.QuantizeZ(p.Level.ZMin);
        return new SdkP.PlaybackPointer(p.Level.Name, p.WorldX, p.WorldY, p.Screen.X, p.Screen.Y, keys, p.Frame.Time.Tick,
            () => p.Zones()?.ResolveOnFloor(p.WorldX, p.WorldY, floorKey).Name);
    }

    private IDisposable Own(IDisposable registration)
    {
        _owned.Add(registration);
        return registration;
    }

    private void OnKeymapChanged() => KeymapChanged?.Invoke();

    private void OnDeactivated() => Deactivated?.Invoke();

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

        public CoreTrack(SdkP.ITimelineTrack track)
        {
            _track = track;
            _track.Changed += OnChanged;
        }

        public string Id => _track.Id;

        public string DisplayName => _track.DisplayName;

        public event Action? MarkersChanged;

        public bool IsAvailable(Core.ITimelineData data) => _track.IsAvailable(new TimelineData(data));

        public IReadOnlyList<Core.TimelineMarker> BuildMarkers(Core.ITimelineData data) => [];

        public IReadOnlyList<Core.TimelineBand> BuildBands(Core.ITimelineData data) =>
        [
            .. _track.BuildBands(new TimelineData(data))
                .Select(b => new Core.TimelineBand(_track.Id, b.StartFrameIndex, b.EndFrameIndex, b.Label, b.Tooltip, b.Argb))
        ];

        public void Detach() => _track.Changed -= OnChanged;

        private void OnChanged() => MarkersChanged?.Invoke();
    }

    private sealed class LaneBehaviour(SdkP.ILaneBehaviour inner) : ILaneBehaviour
    {
        public void OnBandPressed(TimelineBandViewModel band, Core.ITimelineData data) => inner.OnBandPressed(Band(band));

        public IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, Core.ITimelineData data) =>
            Menu(inner.MenuFor(Band(band)));

        public void OnLabelRequested(int frame) => inner.OnLabelRequested(frame);

        public void OnEditSpanDragged(int startFrame, int endFrame) => inner.OnEditSpanDragged(startFrame, endFrame);
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
