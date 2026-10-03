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
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The 2D tab's <see cref="IPlaybackSurface" />: band-menu contributors go to the timeline, side panes to
///     the side-pane host the view binds, right-column panels to <see cref="Panels" />, which the view lists
///     in order. One side pane shows at a time; opening one closes the one before it, and the tab closes the
///     export pane when any opens. Right-column panels are open together; each shows while its gate is on
///     and the tab is in Review mode. The view model of a closed pane or panel is disposed, however it closes.
/// </summary>
public sealed partial class Playback2DSurface : ObservableObject, IPlaybackSurface
{
    private readonly List<Func<Playback2DAction, bool>> _actionHandlers = [];
    private readonly Func<Scene2DFrame> _frame;
    private readonly Func<string, bool> _isEnabled;
    private readonly Func<bool> _isReviewMode;
    private readonly List<Func<Key, KeyModifiers, bool>> _keyHandlers = [];
    private readonly Func<IReadOnlyList<MapLevel>?> _levels;
    private readonly List<Func<MapLevel, double, double, bool>> _mapClickHandlers = [];
    private readonly List<PaneHandle> _panes = [];
    private readonly List<Playback2DPanel> _panels = [];
    private readonly Playback2DTimelineViewModel _timeline;
    private readonly Func<PlaceResolver?> _zones;
    private PaneHandle? _openSide;

    /// <summary>The open side pane's view model, or null. The view binds its pane host's content and visibility to this.</summary>
    [ObservableProperty]
    private object? _sidePane;

    /// <param name="timeline">The timeline the band menus go to.</param>
    /// <param name="levels">The mounted viewport's levels, read on demand; null for none.</param>
    /// <param name="isEnabled">The tab's feature gate; a tab without one answers true.</param>
    /// <param name="isReviewMode">The tab's Review mode, read on demand.</param>
    /// <param name="frame">The frame on screen, read on demand.</param>
    /// <param name="zones">The open map's zones, read on demand.</param>
    internal Playback2DSurface(Playback2DTimelineViewModel timeline, Func<IReadOnlyList<MapLevel>?> levels,
        Func<string, bool> isEnabled, Func<bool> isReviewMode, Func<Scene2DFrame> frame, Func<PlaceResolver?> zones)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(isReviewMode);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(zones);
        _timeline = timeline;
        _levels = levels;
        _isEnabled = isEnabled;
        _isReviewMode = isReviewMode;
        _frame = frame;
        _zones = zones;
    }

    /// <summary>A side pane opened. The tab closes the export pane on it.</summary>
    public event Action? SidePaneOpened;

    /// <summary>A right-column panel opened, closed or changed its gate. The tab re-reads <see cref="HasPanels" />.</summary>
    public event Action? PanelsChanged;

    /// <summary>The side panes added, in order. For tests.</summary>
    internal IReadOnlyList<IPaneHandle> Panes => _panes;

    /// <summary>The open right-column panels in order, each with its own <see cref="Playback2DPanel.IsShown" />. The view's ItemsSource.</summary>
    public ObservableCollection<Playback2DPanel> Panels { get; } = [];

    /// <summary>An open panel whose gate is on exists: Review mode has something to show.</summary>
    public bool HasPanels => Panels.Any(p => p.IsGateOn);

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
    public bool IsReviewMode => _isReviewMode();

    /// <inheritdoc />
    public event Action? ReviewModeChanged;

    /// <inheritdoc />
    public Playback2DTimelineViewModel Timeline => _timeline;

    /// <inheritdoc />
    public Scene2DFrame CurrentFrame => _frame();

    /// <inheritdoc />
    public PlaceResolver? Zones => _zones();

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
    public IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Playback2DPanel panel = new(this, order, viewModel, view, featureId);
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
    public IDisposable AddMapClickHandler(Func<MapLevel, double, double, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _mapClickHandlers.Add(handler);
        return new Removal(() => _mapClickHandlers.Remove(handler));
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

    /// <summary>The contributions' turn at a keymap action. True when one consumed it.</summary>
    public bool TryExecute(Playback2DAction action)
    {
        foreach (Func<Playback2DAction, bool> handler in _actionHandlers.ToArray())
        {
            if (handler(action))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The contributions' turn at a map click, before the pointer tools. True when one took it.</summary>
    public bool TryHandleMapClick(MapLevel level, double worldX, double worldY)
    {
        ArgumentNullException.ThrowIfNull(level);
        foreach (Func<MapLevel, double, double, bool> handler in _mapClickHandlers.ToArray())
        {
            if (handler(level, worldX, worldY))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsFeatureEnabled(string featureId) => _isEnabled(featureId);

    /// <summary>The tab deactivated. Raised before the tab flushes its documents.</summary>
    internal void NotifyDeactivated() => Deactivated?.Invoke();

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

        PanelsChanged?.Invoke();
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(HasKeyboard));
    }

    /// <summary>The tab's Review mode flipped: every panel re-reads whether it shows, then the contributions hear it.</summary>
    internal void NotifyReviewModeChanged()
    {
        foreach (Playback2DPanel panel in _panels.ToArray())
        {
            panel.Refresh();
        }

        OnPropertyChanged(nameof(HasKeyboard));
        ReviewModeChanged?.Invoke();
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
        PanelsChanged?.Invoke();
        OnPropertyChanged(nameof(HasPanels));
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
        PanelsChanged?.Invoke();
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(HasKeyboard));
    }

    internal void RemovePanel(Playback2DPanel panel)
    {
        ClosePanel(panel);
        _panels.Remove(panel);
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

    internal Playback2DPanel(Playback2DSurface owner, int order, Func<object> factory, Func<Control>? view, string? featureId)
    {
        _owner = owner;
        Order = order;
        _factory = factory;
        _view = view;
        FeatureId = featureId;
        _isGateOn = featureId is null || owner.IsFeatureEnabled(featureId);
    }

    /// <summary>Among right-column panels, lower first.</summary>
    public int Order { get; }

    /// <summary>The gate the panel shows under, or null for none beyond the pack's.</summary>
    public string? FeatureId { get; }

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

    internal void Refresh()
    {
        bool gate = FeatureId is null || _owner.IsFeatureEnabled(FeatureId);
        bool shown = IsOpen && gate && _owner.IsReviewMode;
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
