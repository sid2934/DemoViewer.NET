#region

using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The 2D tab's <see cref="IPlaybackSurface" />: band-menu contributors and lanes go to the timeline, side
///     panes to the side-pane host the view binds, right-column panels to <see cref="Panels" /> and mode
///     toggles to <see cref="ModeToggles" />, which the view lists in order. One side pane shows at a time;
///     opening one closes the one before it, and the tab closes the export pane when any opens. Right-column
///     panels are open together; each shows while its gate is on and its mode, if bound to one, is on. The
///     view model of a closed pane or panel is disposed, however it closes.
/// </summary>
public sealed partial class Playback2DSurface : ObservableObject, IPlaybackSurface
{
    private readonly List<Func<Playback2DAction, bool>> _actionHandlers = [];
    private readonly List<Action> _demoChangedHandlers = [];
    private readonly Func<Scene2DFrame> _frame;
    private readonly Func<string, bool> _isEnabled;
    private readonly List<Func<Key, KeyModifiers, bool>> _keyHandlers = [];
    private readonly Func<IReadOnlyList<MapLevel>?> _levels;
    private readonly List<PaneHandle> _panes = [];
    private readonly List<Playback2DPanel> _panels = [];
    private readonly List<Action<int>> _playheadHandlers = [];
    private readonly List<Func<ScenePointer, bool>> _pointerPreHandlers = [];
    private readonly Playback2DTimelineViewModel _timeline;
    private PaneHandle? _openSide;

    /// <summary>The open side pane's view model, or null. The view binds its pane host's content and visibility to this.</summary>
    [ObservableProperty]
    private object? _sidePane;

    /// <param name="timeline">The timeline the band menus and lanes go to.</param>
    /// <param name="levels">The mounted viewport's levels, read on demand; null for none.</param>
    /// <param name="isEnabled">The tab's feature gate; a tab without one answers true.</param>
    /// <param name="frame">The frame on screen, read on demand (a toolbar item's run gets it at invocation).</param>
    internal Playback2DSurface(Playback2DTimelineViewModel timeline, Func<IReadOnlyList<MapLevel>?> levels,
        Func<string, bool> isEnabled, Func<Scene2DFrame> frame)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(frame);
        _timeline = timeline;
        _levels = levels;
        _isEnabled = isEnabled;
        _frame = frame;
    }

    /// <summary>A side pane opened. The tab closes the export pane on it.</summary>
    public event Action? SidePaneOpened;

    /// <summary>A right-column panel opened, closed, or changed its gate or its mode. The tab re-reads <see cref="HasPanels" /> and <see cref="HasShownPanels" />.</summary>
    public event Action? PanelsChanged;

    /// <summary>The side panes added, in order. For tests.</summary>
    internal IReadOnlyList<IPaneHandle> Panes => _panes;

    /// <summary>The open right-column panels in order, each with its own <see cref="Playback2DPanel.IsShown" />. The view's ItemsSource.</summary>
    public ObservableCollection<Playback2DPanel> Panels { get; } = [];

    /// <summary>The mode toggles added, in order. The toolbar's ItemsSource.</summary>
    public ObservableCollection<ModeToggle> ModeToggles { get; } = [];

    /// <summary>The toolbar items added, ordered by <see cref="ToolbarItem.Order" />. The toolbar's and the overflow menu's ItemsSource.</summary>
    public ObservableCollection<ToolbarItem> ToolbarItems { get; } = [];

    /// <summary>A toolbar item exists: the divider and the row showing them have something to show.</summary>
    public bool HasToolbarItems => ToolbarItems.Count > 0;

    /// <summary>An open panel whose gate is on exists: the column has something to show.</summary>
    public bool HasPanels => Panels.Any(p => p.IsGateOn);

    /// <summary>A panel shows: the column is the panels' and the player cards collapse to a strip.</summary>
    public bool HasShownPanels => Panels.Any(p => p.IsShown);

    /// <summary>A shown panel holds the keyboard: the contributions' action handlers see every action first.</summary>
    public bool HasKeyboard => Panels.Any(p => p.IsShown && p.HasKeyboard);

    /// <inheritdoc />
    public IReadOnlyList<MapLevel> MapLevels => _levels() ?? [];

    /// <inheritdoc />
    public Playback2DKeymapProfile Keymap { get; private set; } = Playback2DKeymapProfile.Default;

    /// <inheritdoc />
    public event Action? KeymapChanged;

    /// <inheritdoc />
    public event Action? Deactivated;

    /// <inheritdoc />
    public IDisposable OnDemoChanged(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _demoChangedHandlers.Add(handler);
        return new Removal(() => _demoChangedHandlers.Remove(handler));
    }

    /// <inheritdoc />
    public IDisposable OnPlayheadChanged(Action<int> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _playheadHandlers.Add(handler);
        return new Removal(() => _playheadHandlers.Remove(handler));
    }

    /// <inheritdoc />
    public ILaneHandle AddLane(ITimelineTrack track, TimelineBandRow row, ILaneBehaviour? behaviour = null) =>
        _timeline.RegisterLane(track, row, behaviour);

    /// <inheritdoc />
    public IDisposable AddModeToggle(ModeToggle toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        ModeToggles.Add(toggle);
        return new Removal(() => ModeToggles.Remove(toggle));
    }

    /// <inheritdoc />
    public IDisposable AddToolbarItem(ToolbarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Command = new RelayCommand(() => item.Run(_frame()));

        int at = 0;
        foreach (ToolbarItem existing in ToolbarItems)
        {
            if (existing.Order > item.Order)
            {
                break;
            }

            at++;
        }

        ToolbarItems.Insert(at, item);
        OnPropertyChanged(nameof(HasToolbarItems));
        return new Removal(() =>
        {
            ToolbarItems.Remove(item);
            item.Command = null;
            OnPropertyChanged(nameof(HasToolbarItems));
        });
    }

    /// <inheritdoc />
    public string GestureHint(Playback2DAction action) =>
        Keymap.GestureText(action) is { Length: > 0 } text ? $" ({text})" : "";

    /// <inheritdoc />
    public IDisposable AddBandMenu(Func<TimelineBandViewModel, IEnumerable<MenuEntry>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _timeline.BandMenus.Add(items);
        return new Removal(() => _timeline.BandMenus.Remove(items));
    }

    /// <inheritdoc />
    public IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (where == PanePlacement.RightColumn)
        {
            return AddPanel(order, viewModel);
        }

        PaneHandle handle = new(this, order, viewModel);
        int at = _panes.FindIndex(p => p.Order > order);
        _panes.Insert(at < 0 ? _panes.Count : at, handle);
        return handle;
    }

    /// <inheritdoc />
    public IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null,
        ModeToggle? mode = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Playback2DPanel panel = new(this, order, viewModel, view, featureId, mode);
        int at = _panels.FindIndex(p => p.Order > order);
        _panels.Insert(at < 0 ? _panels.Count : at, panel);
        return panel;
    }

    /// <inheritdoc />
    public IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _keyHandlers.Add(handler);
        return new Removal(() => _keyHandlers.Remove(handler));
    }

    /// <inheritdoc />
    public IDisposable AddActionHandler(Func<Playback2DAction, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _actionHandlers.Add(handler);
        return new Removal(() => _actionHandlers.Remove(handler));
    }

    /// <inheritdoc />
    public IDisposable AddPointerPreHandler(Func<ScenePointer, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _pointerPreHandlers.Add(handler);
        return new Removal(() => _pointerPreHandlers.Remove(handler));
    }

    /// <summary>The contributions' turn at a key, before the tab's keymap. True when one consumed it.</summary>
    public bool TryHandleKey(Key key, KeyModifiers modifiers)
    {
        // Snapshot: a handler may remove itself (a panel leaving focus closes nothing, but a pack toggle can).
        foreach (Func<Key, KeyModifiers, bool> handler in _keyHandlers.ToArray())
        {
            if (handler(key, modifiers))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The contributions' turn at a keymap action: a mode toggle's action flips it, else a toolbar
    ///     item's action runs it, else the handlers. True when one consumed it.
    /// </summary>
    public bool TryExecute(Playback2DAction action)
    {
        foreach (ModeToggle toggle in ModeToggles.ToArray())
        {
            if (toggle.Action == action)
            {
                return toggle.TryToggle();
            }
        }

        foreach (ToolbarItem item in ToolbarItems.ToArray())
        {
            if (item.Action == action)
            {
                return item.Run(_frame());
            }
        }

        foreach (Func<Playback2DAction, bool> handler in _actionHandlers.ToArray())
        {
            if (handler(action))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The contributions' turn at a primary press, before the pointer tools. True when one took it.</summary>
    internal bool TryHandlePointerPress(ScenePointer pointer)
    {
        foreach (Func<ScenePointer, bool> handler in _pointerPreHandlers.ToArray())
        {
            if (handler(pointer))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsFeatureEnabled(string featureId) => _isEnabled(featureId);

    /// <summary>The tab deactivated. Raised before the tab flushes its documents.</summary>
    internal void NotifyDeactivated() => Deactivated?.Invoke();

    /// <summary>The tab resynced to the demo now current: every <see cref="OnDemoChanged" /> handler, in order.</summary>
    internal void NotifyDemoChanged()
    {
        foreach (Action handler in _demoChangedHandlers.ToArray())
        {
            handler();
        }
    }

    /// <summary>The timeline's playhead moved to <paramref name="tick" />: every <see cref="OnPlayheadChanged" /> handler, in order.</summary>
    internal void NotifyPlayheadChanged(int tick)
    {
        foreach (Action<int> handler in _playheadHandlers.ToArray())
        {
            handler(tick);
        }
    }

    /// <summary>The tab's resolved keymap changed.</summary>
    internal void SetKeymap(Playback2DKeymapProfile keymap)
    {
        Keymap = keymap ?? Playback2DKeymapProfile.Default;
        KeymapChanged?.Invoke();
    }

    /// <summary>The tab's features changed: every panel re-reads its gate.</summary>
    internal void RefreshGates()
    {
        foreach (Playback2DPanel panel in _panels.ToArray())
        {
            panel.Refresh();
        }

        RaisePanelsChanged();
    }

    // A panel's mode flipped. The panel has refreshed itself; the fold-ups follow.
    internal void OnPanelModeChanged() => RaisePanelsChanged();

    private void RaisePanelsChanged()
    {
        PanelsChanged?.Invoke();
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(HasShownPanels));
        OnPropertyChanged(nameof(HasKeyboard));
    }

    /// <summary>Closes the open side pane, if any. The host's Close button and the tab's own lifecycle call this.</summary>
    [RelayCommand]
    public void CloseSidePane() => _openSide?.Close();

    private void OpenSide(PaneHandle handle)
    {
        _openSide?.Close();
        object content = handle.Factory();
        handle.Content = content;
        _openSide = handle;
        SidePane = content;
        SidePaneOpened?.Invoke();
    }

    private void CloseSide(PaneHandle handle)
    {
        if (!ReferenceEquals(_openSide, handle))
        {
            return;
        }

        _openSide = null;
        object? content = handle.Content;
        handle.Content = null;
        SidePane = null;
        (content as IDisposable)?.Dispose();
        handle.RaiseClosed();
    }

    private void Remove(PaneHandle handle)
    {
        CloseSide(handle);
        _panes.Remove(handle);
    }

    internal void OpenPanel(Playback2DPanel panel)
    {
        if (panel.IsOpen)
        {
            return;
        }

        panel.Show(panel.Factory());

        // Panels keeps registration order among the open ones, which AddPanel sorted by Order.
        int at = 0;
        foreach (Playback2DPanel other in _panels)
        {
            if (ReferenceEquals(other, panel))
            {
                break;
            }

            if (other.IsOpen)
            {
                at++;
            }
        }

        Panels.Insert(at, panel);
        panel.Refresh();
        RaisePanelsChanged();
    }

    internal void ClosePanel(Playback2DPanel panel)
    {
        if (!panel.IsOpen)
        {
            return;
        }

        Panels.Remove(panel);
        object? content = panel.Hide();
        (content as IDisposable)?.Dispose();
        panel.Refresh();
        panel.RaiseClosed();
        RaisePanelsChanged();
    }

    internal void RemovePanel(Playback2DPanel panel)
    {
        ClosePanel(panel);
        if (_panels.Remove(panel))
        {
            panel.Unbind();
        }
    }

    internal void OnPanelKeyboardChanged() => OnPropertyChanged(nameof(HasKeyboard));

    private sealed class PaneHandle(Playback2DSurface owner, int order, Func<object> factory) : IPaneHandle
    {
        public int Order => order;

        public Func<object> Factory => factory;

        public object? Content { get; set; }

        public bool IsOpen => Content is not null;

        public event Action? Closed;

        public void Open() => owner.OpenSide(this);

        public void Close() => owner.CloseSide(this);

        public void Dispose() => owner.Remove(this);

        public void RaiseClosed() => Closed?.Invoke();
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            _remove?.Invoke();
            _remove = null;
        }
    }
}

/// <summary>
///     A right-column panel: the contribution's <see cref="IPanelHandle" /> and the view's item. The view
///     presents <see cref="View" /> and follows <see cref="IsShown" />.
/// </summary>
public sealed class Playback2DPanel : ObservableObject, IPanelHandle
{
    private readonly Func<object> _factory;
    private readonly Playback2DSurface _owner;
    private readonly Func<Control>? _view;
    private object? _content;
    private bool _hasKeyboard;
    private bool _isGateOn;
    private bool _isShown;
    private object? _presented;

    internal Playback2DPanel(Playback2DSurface owner, int order, Func<object> factory, Func<Control>? view, string? featureId,
        ModeToggle? mode)
    {
        _owner = owner;
        Order = order;
        _factory = factory;
        _view = view;
        FeatureId = featureId;
        Mode = mode;
        _isGateOn = featureId is null || owner.IsFeatureEnabled(featureId);
        if (mode is not null)
        {
            mode.Changed += OnModeChanged;
        }
    }

    /// <summary>Among right-column panels, lower first.</summary>
    public int Order { get; }

    /// <summary>The gate the panel shows under, or null for none beyond the pack's.</summary>
    public string? FeatureId { get; }

    /// <summary>The mode the panel shows under, or null for none.</summary>
    public ModeToggle? Mode { get; }

    internal Func<object> Factory => _factory;

    /// <summary>The open panel's view model, or null.</summary>
    public object? Content => _content;

    /// <summary>
    ///     What the view presents: the contributed control with the view model as its DataContext, or the
    ///     view model itself for the ViewLocator. Null while closed.
    /// </summary>
    public object? View => _presented;

    /// <summary>The gate reads on. Open panels with the gate on make Review mode available.</summary>
    public bool IsGateOn => _isGateOn;

    /// <inheritdoc />
    public bool IsOpen => _content is not null;

    /// <inheritdoc />
    public bool IsShown => _isShown;

    /// <inheritdoc />
    public event Action? ShownChanged;

    /// <inheritdoc />
    public event Action? Closed;

    /// <inheritdoc />
    public bool HasKeyboard
    {
        get => _hasKeyboard;
        set
        {
            if (_hasKeyboard == value)
            {
                return;
            }

            _hasKeyboard = value;
            OnPropertyChanged();
            _owner.OnPanelKeyboardChanged();
        }
    }

    /// <inheritdoc />
    public void Open() => _owner.OpenPanel(this);

    /// <inheritdoc />
    public void Close() => _owner.ClosePanel(this);

    /// <inheritdoc />
    public void Dispose() => _owner.RemovePanel(this);

    internal void Show(object content)
    {
        _content = content;
        if (_view is { } build)
        {
            Control control = build();
            control.DataContext = content;
            _presented = control;
        }
        else
        {
            _presented = content;
        }

        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(IsOpen));
    }

    internal object? Hide()
    {
        object? content = _content;
        _content = null;
        _presented = null;
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(IsOpen));
        return content;
    }

    internal void RaiseClosed() => Closed?.Invoke();

    // Removed from the surface: the mode subscription goes with it.
    internal void Unbind()
    {
        if (Mode is not null)
        {
            Mode.Changed -= OnModeChanged;
        }
    }

    private void OnModeChanged()
    {
        Refresh();
        _owner.OnPanelModeChanged();
    }

    internal void Refresh()
    {
        bool gate = FeatureId is null || _owner.IsFeatureEnabled(FeatureId);
        bool shown = IsOpen && gate && (Mode?.IsOn ?? true);
        if (gate != _isGateOn)
        {
            _isGateOn = gate;
            OnPropertyChanged(nameof(IsGateOn));
        }

        if (shown == _isShown)
        {
            return;
        }

        _isShown = shown;
        OnPropertyChanged(nameof(IsShown));
        ShownChanged?.Invoke();
    }
}
