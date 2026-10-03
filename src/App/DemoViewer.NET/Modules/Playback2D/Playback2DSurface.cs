#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The 2D tab's <see cref="IPlaybackSurface" />: band-menu contributors go to the timeline, panes to the
///     side-pane host the view binds. One side pane shows at a time; opening one closes the one before it,
///     and the tab closes the export pane when any opens. The view model of an open pane is disposed when
///     the pane closes, however it closes.
/// </summary>
public sealed partial class Playback2DSurface : ObservableObject, IPlaybackSurface
{
    private readonly Func<IReadOnlyList<MapLevel>?> _levels;
    private readonly List<PaneHandle> _panes = [];
    private readonly Playback2DTimelineViewModel _timeline;
    private PaneHandle? _openSide;

    /// <summary>The open side pane's view model, or null. The view binds its pane host's content and visibility to this.</summary>
    [ObservableProperty]
    private object? _sidePane;

    /// <param name="timeline">The timeline the band menus go to.</param>
    /// <param name="levels">The mounted viewport's levels, read on demand; null for none.</param>
    internal Playback2DSurface(Playback2DTimelineViewModel timeline, Func<IReadOnlyList<MapLevel>?> levels)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(levels);
        _timeline = timeline;
        _levels = levels;
    }

    /// <summary>A side pane opened. The tab closes the export pane on it.</summary>
    public event Action? SidePaneOpened;

    /// <summary>The panes added, in order. For tests.</summary>
    internal IReadOnlyList<IPaneHandle> Panes => _panes;

    /// <inheritdoc />
    public IReadOnlyList<MapLevel> MapLevels => _levels() ?? [];

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
        if (where != PanePlacement.Side)
        {
            throw new NotSupportedException($"{where} panes are not hosted yet; only {PanePlacement.Side} is.");
        }

        PaneHandle handle = new(this, order, viewModel);
        int at = _panes.FindIndex(p => p.Order > order);
        _panes.Insert(at < 0 ? _panes.Count : at, handle);
        return handle;
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
