#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>
///     The saved list of the Situations tab: one row per watched situation with its own "N new", the
///     Watch action that saves the canvas's query as it stands, Mark all seen, and the session note
///     on a host with no config root. Every row is a projection of the service; the service is the
///     only state.
/// </summary>
public sealed partial class WatchedSituationsViewModel : ViewModelBase, IDisposable
{
    /// <summary>The line the panel shows on a host that cannot persist: the annotations panel's words.</summary>
    public const string SessionNote = "session only: watched situations are not saved in the browser";

    private readonly QueryCanvasViewModel _canvas;
    private readonly WatchedSituationsService _service;
    private readonly TeamIdentityService? _teams;

    private bool _disposed;

    /// <summary>The name the next watch is saved under; empty takes the query's own line.</summary>
    [ObservableProperty]
    private string _nameText = "";

    /// <param name="service">The watches and their badges.</param>
    /// <param name="canvas">The canvas a watch is saved from and re-run onto.</param>
    /// <param name="teams">Team Identity, so a row can name the opponent; null shows the id-free line.</param>
    public WatchedSituationsViewModel(WatchedSituationsService service, QueryCanvasViewModel canvas, TeamIdentityService? teams = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(canvas);
        _service = service;
        _canvas = canvas;
        _teams = teams;

        _service.Changed += Refresh;
        _canvas.PropertyChanged += OnCanvasPropertyChanged;
        _canvas.Document.Changed += OnQueryChanged;
        _canvas.Filters.Changed += OnQueryChanged;
        Refresh();
    }

    /// <summary>The rows, in creation order.</summary>
    public ObservableCollection<WatchedSituationRowViewModel> Rows { get; } = [];

    /// <summary>New hits over every watch: the same number the tab badge shows.</summary>
    public int NewCount => _service.NewCount;

    public bool HasNew => NewCount > 0;

    public bool HasRows => Rows.Count > 0;

    /// <summary>"Watched situations · 3 new", or the plain header.</summary>
    public string HeaderLine => HasNew ? $"Watched situations · {NewCount} new" : "Watched situations";

    /// <summary>Nothing persists on this host.</summary>
    public bool IsSessionOnly => _service.IsSessionOnly;

    /// <summary>Why the file was refused, or null.</summary>
    public string? FileProblem => _service.FileProblem;

    public bool HasFileProblem => FileProblem is not null;

    /// <summary>The name the Watch action falls back to: the query as text.</summary>
    public string SuggestedName => _canvas.Map is null ? "" : WatchedSituationsService.QueryLine(Current());

    /// <summary>A query is on the canvas: a map with a token or a filter set. An empty query would match every round.</summary>
    public bool CanWatch => _canvas.Map is not null && (!_canvas.Draft.IsEmpty || _canvas.Filters.IsActive);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.Changed -= Refresh;
        _canvas.PropertyChanged -= OnCanvasPropertyChanged;
        _canvas.Document.Changed -= OnQueryChanged;
        _canvas.Filters.Changed -= OnQueryChanged;
    }

    /// <summary>Saves the canvas's query as it stands. The watermark starts now: what it matches today has been seen.</summary>
    [RelayCommand(CanExecute = nameof(CanWatch))]
    private void WatchCurrent()
    {
        if (_canvas.Map is null)
        {
            return;
        }

        _service.Watch(NameText, _canvas.Map, _canvas.Document.Placed, _canvas.Draft.Tolerance, _canvas.Filters.Values);
        NameText = "";
    }

    /// <summary>Clears every badge.</summary>
    [RelayCommand(CanExecute = nameof(HasNew))]
    private void MarkAllSeen() => _service.MarkAllSeen();

    /// <summary>Re-run: the watch back on the canvas, then the search.</summary>
    /// <param name="watch">The saved query.</param>
    internal void Run(WatchedSituation watch)
    {
        _canvas.Load(watch);
        if (_canvas.SearchCommand.CanExecute(null))
        {
            _canvas.SearchCommand.Execute(null);
        }
    }

    internal void MarkSeen(WatchedSituation watch) => _service.MarkSeen(watch.Id);

    internal void Remove(WatchedSituation watch) => _service.Remove(watch.Id);

    // The rail's own line where it can name the team; the service's where it cannot.
    internal string QueryLineFor(WatchedSituation watch)
    {
        string line = WatchedSituationsService.QueryLine(watch);
        if (watch.Filters.Opponent is Guid opponent && _teams?.Teams.FirstOrDefault(t => t.Id == opponent) is { } team)
        {
            line = line.Replace("vs opponent", $"vs {DisplayText.Sanitize(team.Name)}", StringComparison.Ordinal);
        }

        return line;
    }

    internal int NewCountOf(WatchedSituation watch) => _service.NewCountOf(watch.Id);

    internal int NewDemoCountOf(WatchedSituation watch) => _service.NewDemoCountOf(watch.Id);

    private WatchedSituation Current() => new()
    {
        Map = _canvas.Map ?? "",
        Tolerance = _canvas.Draft.Tolerance,
        Tokens = [.. _canvas.Document.Placed.Select(WatchedToken.From)],
        Filters = _canvas.Filters.Values
    };

    private void OnCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryCanvasViewModel.Map))
        {
            OnQueryChanged();
        }
    }

    private void OnQueryChanged()
    {
        OnPropertyChanged(nameof(SuggestedName));
        OnPropertyChanged(nameof(CanWatch));
        WatchCurrentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds the rows from the service.</summary>
    public void Refresh()
    {
        Rows.Clear();
        foreach (WatchedSituation watch in _service.Watches)
        {
            Rows.Add(new WatchedSituationRowViewModel(this, watch));
        }

        OnPropertyChanged(nameof(NewCount));
        OnPropertyChanged(nameof(HasNew));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HeaderLine));
        OnPropertyChanged(nameof(FileProblem));
        OnPropertyChanged(nameof(HasFileProblem));
        MarkAllSeenCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One saved query on the list: its name, its query line, its badge and the three actions.</summary>
public sealed partial class WatchedSituationRowViewModel : ViewModelBase
{
    private readonly WatchedSituationsViewModel _owner;

    /// <param name="owner">The list.</param>
    /// <param name="watch">The saved query.</param>
    public WatchedSituationRowViewModel(WatchedSituationsViewModel owner, WatchedSituation watch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(watch);
        _owner = owner;
        Watch = watch;
        NewCount = owner.NewCountOf(watch);
        NewDemoCount = owner.NewDemoCountOf(watch);
        QueryLine = owner.QueryLineFor(watch);
    }

    /// <summary>The saved query.</summary>
    public WatchedSituation Watch { get; }

    public Guid Id => Watch.Id;

    /// <summary>The display name, sanitized at the render boundary like a team's.</summary>
    public string Name => DisplayText.Sanitize(Watch.Name);

    public string Map => Watch.Map;

    /// <summary>"CT BombsiteA:2|Outside:3 vs T any · CT buy full · vs Falcons".</summary>
    public string QueryLine { get; }

    /// <summary>New hits in demos indexed since the watch was last seen.</summary>
    public int NewCount { get; }

    /// <summary>How many demos those hits fall in.</summary>
    public int NewDemoCount { get; }

    public bool HasNew => NewCount > 0;

    /// <summary>"3 new" on the badge.</summary>
    public string BadgeText => $"{NewCount} new";

    /// <summary>"3 new rounds in 2 demos", or "nothing new" when the badge is clear.</summary>
    public string NewLine => NewCount == 0
        ? "nothing new"
        : $"{NewCount} new {(NewCount == 1 ? "round" : "rounds")} in {NewDemoCount} {(NewDemoCount == 1 ? "demo" : "demos")}";

    /// <summary>Puts the query back on the canvas and searches.</summary>
    [RelayCommand]
    private void Run() => _owner.Run(Watch);

    /// <summary>Clears this row's badge.</summary>
    [RelayCommand(CanExecute = nameof(HasNew))]
    private void MarkSeen() => _owner.MarkSeen(Watch);

    /// <summary>Drops the watch.</summary>
    [RelayCommand]
    private void Remove() => _owner.Remove(Watch);
}
