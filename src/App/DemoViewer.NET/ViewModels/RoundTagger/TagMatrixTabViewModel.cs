#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.ViewModels.RoundTagger;

/// <summary>
///     The Matrix (plan §3, tag-store.md §3.7): a pivot over every tag instance in scope, codes down the
///     side and a label group across the top by default, either axis any code, round, demo, human label
///     group or fact group, and every cell a count whose click sends exactly those clips to the Review
///     Queue and shows the Review tab.
///     <para>
///         <b>Scope.</b> The team field is the multi-demo mode: the demos Team Identity says the team played
///         (<see cref="TeamIdentityService.DemosOf" />, newest first, the "Falcons, last 6 demos" of the
///         design), narrowed to the tagged ones before the last-N cut so "last 6" means six demos that
///         can contribute. With no team the scope is every tagged demo, newest library row first. The
///         side field is the team's side in the instance's round, joined through
///         <see cref="TeamIdentityService.SideAtRound" />; <see cref="TagQuery" /> knows nothing of teams, so
///         that narrowing happens on the documents before the query.
///     </para>
///     <para>
///         <b>The dynamic mode</b> is the same call with a narrower <see cref="TagSlice" />: the round text
///         ("1-12"), one label or fact group held to one value ("buy.t = full", "phase = postPlant") and
///         the source.
///     </para>
///     <para>
///         <b>Threading.</b> Loading every document is measured at 222 ms warm over a thousand demos, so a
///         rebuild is debounced and runs off the UI thread (the <c>LoadRecords</c> rule), and the result is
///         posted back and dropped when a newer request has been made since. A store or team change while
///         the tab is not showing only marks it stale; the next activation rebuilds.
///     </para>
/// </summary>
public sealed partial class TagMatrixTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>What the browser host says: the documents the Matrix reads are the session's.</summary>
    public const string BrowserNote = "session only: this browser tab forgets tags when it reloads";

    /// <summary>What the side field says while no team is picked.</summary>
    public const string NoTeamSideNote = "pick a team to split by the side it played";

    private static readonly TimeSpan _defaultDebounce = TimeSpan.FromMilliseconds(150);

    // One instance, so a rebuilt team list keeps it as the same object: the field's selection compares
    // records by value and would otherwise hold an equal option the list no longer contains.
    private static readonly SearchFilterOption<Guid?> _allDemos = new("All tagged demos", null);

    // The "last N demos" choices; six is the design's mock.
    private static readonly int[] _lastCounts = [3, 6, 10, 20];

    private readonly TimeSpan _debounce;
    private readonly Func<string, DemoCacheIndexEntry?> _indexBySha;
    private readonly Action<Action> _post;
    private readonly ReviewQueue? _review;
    private readonly Func<string, bool>? _selectTab;
    private readonly TagStore _store;
    private readonly TeamIdentityService? _teams;

    private bool _active;
    private bool _applying;
    private bool _columnsChosen;
    private CancellationTokenSource? _cts;
    private bool _dirty = true;
    private bool _disposed;
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();
    private int _sequence;
    private IReadOnlyDictionary<string, int> _tickRates = new Dictionary<string, int>();

    /// <summary>The column axis.</summary>
    [ObservableProperty]
    private TagMatrixAxis? _columnAxis = TagMatrixAxis.Round;

    /// <summary>A rebuild is in flight.</summary>
    [ObservableProperty]
    private bool _isBuilding;

    /// <summary>Why the round text was not applied, or empty.</summary>
    [ObservableProperty]
    private string _roundsProblem = "";

    /// <summary>The round restriction as typed: "1-12", "13-24, 30", or blank for every round.</summary>
    [ObservableProperty]
    private string _roundsText = "";

    /// <summary>The row axis.</summary>
    [ObservableProperty]
    private TagMatrixAxis? _rowAxis = TagMatrixAxis.Code;

    /// <summary>What the last cell click did.</summary>
    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="store">The tag store the documents come from.</param>
    /// <param name="review">The Review Queue a cell sends its clips to; null says there is none.</param>
    /// <param name="indexBySha">Hash to library row (<see cref="DemoCacheStore.TryGetIndexBySha256" />), for paths, maps and dates.</param>
    /// <param name="teams">Team Identity, for the team and side fields; null offers neither.</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after a send; null stays on the Matrix.</param>
    /// <param name="post">Marshals a finished rebuild onto the UI thread; defaults to synchronous.</param>
    /// <param name="debounce">How long a burst of field changes is folded; 150 ms by default.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    public TagMatrixTabViewModel(
        TagStore store,
        ReviewQueue? review = null,
        Func<string, DemoCacheIndexEntry?>? indexBySha = null,
        TeamIdentityService? teams = null,
        Func<string, bool>? selectTab = null,
        Action<Action>? post = null,
        TimeSpan? debounce = null,
        bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _review = review;
        _indexBySha = indexBySha ?? (_ => null);
        _teams = teams;
        _selectTab = selectTab;
        _post = post ?? (action => action());
        _debounce = debounce ?? _defaultDebounce;
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();

        Team = new SearchFilterField<Guid?>("Team", TeamOptions());
        Last = new SearchFilterField<int?>("Demos",
        [
            new SearchFilterOption<int?>("All demos", null),
            .. _lastCounts.Select(n => new SearchFilterOption<int?>($"Last {n}", n))
        ]);
        Map = new SearchFilterField<string?>("Map", [new SearchFilterOption<string?>("Any map", null)]);
        Side = new SearchFilterField<int?>("Their side",
        [
            new SearchFilterOption<int?>("Either side", null),
            new SearchFilterOption<int?>("T side", 2),
            new SearchFilterOption<int?>("CT side", 3)
        ]);
        Filter = new SearchFilterField<TagMatrixAxis?>("Filter", [new SearchFilterOption<TagMatrixAxis?>("No filter", null)]);
        FilterValue = new SearchFilterField<string?>("Value", [new SearchFilterOption<string?>("Any value", null)]);
        Source = new SearchFilterField<TagSource?>("Source",
        [
            new SearchFilterOption<TagSource?>("Tagged and accepted", null),
            new SearchFilterOption<TagSource?>("Hand tagged", TagSource.Human),
            new SearchFilterOption<TagSource?>("Accepted suggestions", TagSource.Suggested),
            new SearchFilterOption<TagSource?>("Imported", TagSource.Import)
        ]);
        Axes = [TagMatrixAxis.Code, TagMatrixAxis.Round, TagMatrixAxis.Demo];

        Team.Changed += OnTeamChanged;
        Last.Changed += OnFieldChanged;
        Map.Changed += OnFieldChanged;
        Side.Changed += OnFieldChanged;
        Filter.Changed += OnFieldChanged;
        FilterValue.Changed += OnFieldChanged;
        Source.Changed += OnFieldChanged;

        _store.Changed += OnStoreChanged;
        if (_teams is not null)
        {
            _teams.Changed += OnTeamsChanged;
        }
    }

    /// <summary>True on the WASM head: the documents live for the session only.</summary>
    public bool IsBrowser { get; }

    /// <summary>A team whose demos are the scope (the multi-demo mode); any is every tagged demo.</summary>
    public SearchFilterField<Guid?> Team { get; }

    /// <summary>How many of the newest demos in scope are read.</summary>
    public SearchFilterField<int?> Last { get; }

    /// <summary>The map of the demos read, from the library row.</summary>
    public SearchFilterField<string?> Map { get; }

    /// <summary>The team's side in the instance's round; needs a team.</summary>
    public SearchFilterField<int?> Side { get; }

    /// <summary>A label or fact group to hold to one value (the dynamic mode).</summary>
    public SearchFilterField<TagMatrixAxis?> Filter { get; }

    /// <summary>The value <see cref="Filter" /> is held to.</summary>
    public SearchFilterField<string?> FilterValue { get; }

    /// <summary>Which sources count; the default is every source but imported.</summary>
    public SearchFilterField<TagSource?> Source { get; }

    /// <summary>Every axis the documents in scope offer, for both pickers.</summary>
    public ObservableCollection<TagMatrixAxis> Axes { get; }

    /// <summary>Team Identity is wired.</summary>
    public bool HasTeams => _teams is not null;

    /// <summary>A team is picked, so the side field means something.</summary>
    public bool HasTeam => Team.Value is not null;

    /// <summary>The column keys as shown.</summary>
    public ObservableCollection<string> ColumnHeaders { get; } = [];

    /// <summary>One row per row key, with a cell per column.</summary>
    public ObservableCollection<TagMatrixRowViewModel> Rows { get; } = [];

    /// <summary>Instances per column, each counted once.</summary>
    public ObservableCollection<string> ColumnTotals { get; } = [];

    /// <summary>The table the rows show; null before the first build.</summary>
    public TagMatrixTable? Table { get; private set; }

    /// <summary>The table has at least one cell.</summary>
    public bool HasCells => Rows.Count > 0;

    /// <summary>"Falcons, last 6 demos, de_nuke, T side": the scope in the words of the design's mock.</summary>
    public string Title { get; private set; } = "All tagged demos";

    /// <summary>"37 instances in 6 demos", plus how many had no value on an axis when any did not.</summary>
    public string SummaryLine { get; private set; } = "";

    /// <summary>The instances in all cells, each once.</summary>
    public string GrandTotal => Table is null ? "" : Table.Total.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the empty table says.</summary>
    public string EmptyLine { get; private set; } = "Nothing tagged yet. Tag rounds with the palette in 2D Playback; the counts land here.";

    /// <summary>The newest rebuild; tests await it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        _active = true;
        if (_dirty)
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    public void OnDeactivated() => _active = false;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _store.Changed -= OnStoreChanged;
        if (_teams is not null)
        {
            _teams.Changed -= OnTeamsChanged;
        }
    }

    /// <summary>
    ///     Starts a rebuild with the fields as they are now. A round text that does not parse leaves the
    ///     table as it was and says why.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (!TagMatrix.TryParseRounds(RoundsText, out IReadOnlySet<int>? rounds))
        {
            RoundsProblem = "rounds read like 1-12 or 13-24, 30";
            return;
        }

        RoundsProblem = "";
        _dirty = false;
        MatrixInputs inputs = new(
            Team.Value, Team.Value is null ? null : Team.Selected?.Display, Last.Value, Map.Value,
            Team.Value is null ? null : Side.Value,
            RowAxis ?? TagMatrixAxis.Code, ColumnAxis ?? TagMatrixAxis.Round, !_columnsChosen,
            Filter.Value, FilterValue.Value, rounds, Source.Value);

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int sequence = ++_sequence;
        IsBuilding = true;
        Pending = RunAsync(inputs, sequence, _cts.Token);
    }

    /// <summary>
    ///     Sends a cell's instances to the Review Queue under one title card naming the cell and the scope,
    ///     then shows the Review tab. An instance whose demo is not in the library is not queued as a dead
    ///     link; the status line counts it instead.
    /// </summary>
    /// <param name="cell">The cell clicked.</param>
    /// <returns>How many clips were added.</returns>
    public int Open(TagMatrixCellViewModel cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.Count == 0)
        {
            return 0;
        }

        if (_review is null)
        {
            StatusLine = "no Review Queue on this host";
            return 0;
        }

        List<ReviewEntry> clips = [];
        int missing = 0;
        foreach (TagInstanceRef instance in cell.Refs)
        {
            if (ReviewQueue.FromTag(instance, sha => _indexBySha(sha)?.Path, _tickRates.GetValueOrDefault(instance.Sha256)) is { } clip)
            {
                clips.Add(clip);
            }
            else
            {
                missing++;
            }
        }

        string notInLibrary = missing == 0 ? "" : $" · {Plural(missing, "clip")} not queued: demo not in library";
        if (clips.Count == 0)
        {
            StatusLine = $"nothing queued: {(missing == 1 ? "the demo is" : "the demos are")} not in the library";
            return 0;
        }

        int added = _review.Add(clips, $"Matrix · {cell.RowDisplay} × {cell.ColumnDisplay}", Title);
        StatusLine = (added == 0 ? "already in Review" : $"{Plural(added, "clip")} sent to Review") + notInLibrary;
        _selectTab?.Invoke(ReviewQueueModule.TabId);
        return added;
    }

    /// <summary>Exchanges the row and column axes: the same pivot, transposed.</summary>
    [RelayCommand]
    private void SwapAxes()
    {
        _applying = true;
        try
        {
            (RowAxis, ColumnAxis) = (ColumnAxis, RowAxis);
        }
        finally
        {
            _applying = false;
        }

        _columnsChosen = true;
        Refresh();
    }

    /// <summary>Every field back to its default, the axes kept.</summary>
    [RelayCommand]
    private void ClearSlice()
    {
        _applying = true;
        try
        {
            Last.Reset();
            Map.Reset();
            Side.Reset();
            Filter.Reset();
            FilterValue.Reset();
            Source.Reset();
            RoundsText = "";
        }
        finally
        {
            _applying = false;
        }

        Refresh();
    }

    partial void OnRowAxisChanged(TagMatrixAxis? value) => OnAxisChanged(value);

    partial void OnColumnAxisChanged(TagMatrixAxis? value)
    {
        if (!_applying && value is not null)
        {
            _columnsChosen = true;
        }

        OnAxisChanged(value);
    }

    // The round text applies as it is typed; a half-typed "1-" says why it waits rather than emptying the table.
    partial void OnRoundsTextChanged(string value) => OnFieldChanged();

    // The ComboBox writes null while Axes is rebuilt; that is not a choice.
    private void OnAxisChanged(TagMatrixAxis? value)
    {
        if (!_applying && value is not null)
        {
            Refresh();
        }
    }

    private void OnFieldChanged()
    {
        if (!_applying)
        {
            Refresh();
        }
    }

    private void OnTeamChanged()
    {
        OnPropertyChanged(nameof(HasTeam));
        if (Team.Value is null && !Side.IsAny)
        {
            _applying = true;
            try
            {
                Side.Reset();
            }
            finally
            {
                _applying = false;
            }
        }

        OnFieldChanged();
    }

    private void OnStoreChanged(string? sha256)
    {
        if (_active)
        {
            Refresh();
        }
        else
        {
            _dirty = true;
        }
    }

    private void OnTeamsChanged()
    {
        _applying = true;
        try
        {
            Team.Replace(TeamOptions());
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(HasTeam));
        OnStoreChanged(null);
    }

    private IEnumerable<SearchFilterOption<Guid?>> TeamOptions()
    {
        yield return _allDemos;
        if (_teams is null)
        {
            yield break;
        }

        foreach (Team team in _teams.Teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SearchFilterOption<Guid?>(DisplayText.Sanitize(team.Name), team.Id);
        }
    }

    private async Task RunAsync(MatrixInputs inputs, int sequence, CancellationToken token)
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, token).ConfigureAwait(false);
            }

            MatrixResult result = await Task.Run(() => Compute(inputs), token).ConfigureAwait(false);
            _post(() =>
            {
                if (sequence == _sequence && !_disposed)
                {
                    Apply(result);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one; it clears IsBuilding when it lands.
        }
        catch (Exception ex)
        {
            _post(() =>
            {
                if (sequence == _sequence)
                {
                    IsBuilding = false;
                    StatusLine = $"could not build the matrix: {ex.Message}";
                }
            });
        }
    }

    // Off the UI thread: the scope, the documents, the side narrowing, the axes and values offered, and
    // the pivot. Reads the store, the cache index and Team Identity, all of which lock for themselves.
    private MatrixResult Compute(MatrixInputs inputs)
    {
        HashSet<string> tagged = new(_store.Index.Where(e => e.InstanceCount > 0).Select(e => e.Sha256), StringComparer.Ordinal);

        // Newest first either way, so "last N" is a Take.
        List<(string Sha, string? Path)> scope = inputs.Team is Guid team && _teams is not null
            ? [.. _teams.DemosOf(team).Where(d => d.Sha256 is { } sha && tagged.Contains(sha)).Select(d => (d.Sha256!, (string?)d.Path))]
            :
            [
                .. tagged.Select(sha => (Sha: sha, Row: _indexBySha(sha)))
                    .OrderByDescending(d => d.Row?.ModifiedTicks ?? long.MinValue)
                    .ThenBy(d => d.Sha, StringComparer.Ordinal)
                    .Select(d => (d.Sha, d.Row?.Path))
            ];

        List<string> maps = [.. scope.Select(d => _indexBySha(d.Sha)?.Map).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (inputs.Map is { } map)
        {
            scope = [.. scope.Where(d => string.Equals(_indexBySha(d.Sha)?.Map, map, StringComparison.Ordinal))];
        }

        if (inputs.Last is int last)
        {
            scope = [.. scope.Take(last)];
        }

        HashSet<string> demos = new(scope.Select(d => d.Sha), StringComparer.Ordinal);
        List<TagDocument> docs = _store.LoadDocuments(e => demos.Contains(e.Sha256));
        IReadOnlyList<TagMatrixAxis> axes = TagMatrix.AxesOf(docs);

        if (inputs.Team is Guid sided && inputs.Side is int side && _teams is not null)
        {
            Dictionary<string, string?> paths = scope.ToDictionary(d => d.Sha, d => d.Path, StringComparer.Ordinal);
            Dictionary<(string, int), int?> sides = [];
            docs = TagMatrix.Narrow(docs, (d, i) =>
            {
                if (i.Round is not int round || paths.GetValueOrDefault(d.Demo.Sha256) is not { } path)
                {
                    return false;
                }

                if (!sides.TryGetValue((d.Demo.Sha256, round), out int? played))
                {
                    sides[(d.Demo.Sha256, round)] = played = _teams.SideAtRound(path, sided, round);
                }

                return played == side;
            });
        }

        IReadOnlyList<string> values = inputs.Filter?.Axis is PivotAxis.Label group
            ? TagMatrix.ValuesOf(docs, group.Namespace, group.Group)
            : [];

        List<LabelPredicate> where = [];
        if (inputs.Filter?.Axis is PivotAxis.Label held && inputs.FilterValue is { } value)
        {
            where.Add(new LabelPredicate(held.Namespace, held.Group, new HashSet<string>(StringComparer.Ordinal) { value }));
        }

        // The first build picks a human label group for the columns, the mock's shape, unless the user
        // has picked columns already.
        TagMatrixAxis columns = inputs.Columns;
        if (inputs.DefaultColumns
            && axes.FirstOrDefault(a => a.Axis is PivotAxis.Label { Namespace: LabelNamespace.Human } && !a.Equals(inputs.Rows)) is { } label)
        {
            columns = label;
        }

        TagSlice slice = new(demos, null, where, inputs.Rounds, inputs.Source, null);
        TagMatrixTable table = new(TagQuery.Pivot(docs, slice, inputs.Rows.Axis, columns.Axis));
        int found = TagQuery.Find(docs, slice).Count;

        Dictionary<string, string> names = new(StringComparer.Ordinal);
        Dictionary<string, int> rates = new(StringComparer.Ordinal);
        foreach (TagDocument doc in docs)
        {
            string sha = doc.Demo.Sha256;
            string? path = scope.FirstOrDefault(d => d.Sha == sha).Path ?? _indexBySha(sha)?.Path;
            names[sha] = path is not null ? Path.GetFileName(path) : doc.Demo.FileName ?? sha[..Math.Min(12, sha.Length)];
            rates[sha] = doc.Clock.TickRate;
        }

        return new MatrixResult(inputs, columns, table, axes, maps, values, docs.Count, found, names, rates);
    }

    private void Apply(MatrixResult result)
    {
        _applying = true;
        try
        {
            ReplaceAxes(result.Axes, result.Inputs.Rows, result.Columns);
            Map.Replace([Map.Options[0], .. result.Maps.Select(m => new SearchFilterOption<string?>(m, m))]);
            Filter.Replace(
            [
                Filter.Options[0],
                .. result.Axes.Where(a => a.Axis is PivotAxis.Label).Select(a => new SearchFilterOption<TagMatrixAxis?>(a.Display, a))
            ]);
            FilterValue.Replace([FilterValue.Options[0], .. result.FilterValues.Select(v => new SearchFilterOption<string?>(Shown(v), v))]);
        }
        finally
        {
            _applying = false;
        }

        _names = result.Names;
        _tickRates = result.TickRates;
        Table = result.Table;

        ColumnHeaders.Clear();
        foreach (string column in result.Table.ColumnKeys)
        {
            ColumnHeaders.Add(Display(result.Columns.Axis, column));
        }

        ColumnTotals.Clear();
        foreach (int total in result.Table.ColumnTotals)
        {
            ColumnTotals.Add(total.ToString(CultureInfo.InvariantCulture));
        }

        Rows.Clear();
        for (int r = 0; r < result.Table.RowKeys.Count; r++)
        {
            string row = result.Table.RowKeys[r];
            string rowDisplay = Display(result.Inputs.Rows.Axis, row);
            List<TagMatrixCellViewModel> cells =
            [
                .. result.Table.ColumnKeys.Select(column => new TagMatrixCellViewModel(this, rowDisplay,
                    Display(result.Columns.Axis, column), result.Table.Refs(row, column)))
            ];
            Rows.Add(new TagMatrixRowViewModel(rowDisplay, cells, result.Table.RowTotals[r]));
        }

        Title = TitleFor(result.Inputs, result.Demos);
        int unplaced = result.Found - result.Table.Total;
        SummaryLine = $"{Plural(result.Table.Total, "instance")} in {Plural(result.Demos, "demo")}"
                      + (unplaced > 0 ? $" · {unplaced} with no value on an axis" : "");
        EmptyLine = result.Demos == 0
            ? "Nothing tagged in this scope. Tag rounds with the palette in 2D Playback; the counts land here."
            : "No instance in this slice has a value on both axes.";
        IsBuilding = false;

        OnPropertyChanged(nameof(Table));
        OnPropertyChanged(nameof(HasCells));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SummaryLine));
        OnPropertyChanged(nameof(EmptyLine));
        OnPropertyChanged(nameof(GrandTotal));
    }

    // Replaces the offered axes only when they changed, and puts the selections back by value: the
    // ComboBoxes clear theirs while the list is rebuilt.
    private void ReplaceAxes(IReadOnlyList<TagMatrixAxis> axes, TagMatrixAxis rows, TagMatrixAxis columns)
    {
        List<TagMatrixAxis> next = [.. axes];
        foreach (TagMatrixAxis kept in (TagMatrixAxis[])[rows, columns])
        {
            if (!next.Contains(kept))
            {
                next.Add(kept);
            }
        }

        if (!next.SequenceEqual(Axes))
        {
            Axes.Clear();
            foreach (TagMatrixAxis axis in next)
            {
                Axes.Add(axis);
            }
        }

        RowAxis = Axes.First(a => a.Equals(rows));
        ColumnAxis = Axes.First(a => a.Equals(columns));
    }

    private string Display(PivotAxis axis, string key) => axis switch
    {
        PivotAxis.Demo => _names.GetValueOrDefault(key) ?? key,
        PivotAxis.Round => $"round {key}",
        _ => Shown(key)
    };

    private static string Shown(string value) => value.Length == 0 ? "(empty)" : DisplayText.Sanitize(value);

    /// <summary>
    ///     The scope as the design's mock names it: "Falcons, last 6 demos, de_nuke, T side". The count
    ///     is the demos actually read, so "last 6" over a team with four tagged demos says four.
    /// </summary>
    /// <param name="inputs">The fields the build read.</param>
    /// <param name="demos">How many documents were read.</param>
    private static string TitleFor(MatrixInputs inputs, int demos)
    {
        List<string> parts = [inputs.TeamName ?? "All tagged demos"];
        if (inputs.TeamName is not null || inputs.Last is not null)
        {
            parts.Add(inputs.Last is not null ? $"last {Plural(demos, "demo")}" : Plural(demos, "demo"));
        }

        if (inputs.Map is { } map)
        {
            parts.Add(map);
        }

        if (inputs.Side is int side)
        {
            parts.Add(side == 2 ? "T side" : "CT side");
        }

        return string.Join(", ", parts);
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    private sealed record MatrixInputs(
        Guid? Team,
        string? TeamName,
        int? Last,
        string? Map,
        int? Side,
        TagMatrixAxis Rows,
        TagMatrixAxis Columns,
        bool DefaultColumns,
        TagMatrixAxis? Filter,
        string? FilterValue,
        IReadOnlySet<int>? Rounds,
        TagSource? Source);

    private sealed record MatrixResult(
        MatrixInputs Inputs,
        TagMatrixAxis Columns,
        TagMatrixTable Table,
        IReadOnlyList<TagMatrixAxis> Axes,
        IReadOnlyList<string> Maps,
        IReadOnlyList<string> FilterValues,
        int Demos,
        int Found,
        IReadOnlyDictionary<string, string> Names,
        IReadOnlyDictionary<string, int> TickRates);
}

/// <summary>One row of the Matrix: its key as shown, a cell per column and the instances in it, each once.</summary>
public sealed class TagMatrixRowViewModel(string display, IReadOnlyList<TagMatrixCellViewModel> cells, int total)
{
    public string Display { get; } = display;

    public IReadOnlyList<TagMatrixCellViewModel> Cells { get; } = cells;

    public string Total { get; } = total.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One count of the Matrix and the refs behind it; a click sends those clips to the Review Queue.</summary>
public sealed partial class TagMatrixCellViewModel : ViewModelBase
{
    private readonly TagMatrixTabViewModel _owner;

    /// <param name="owner">The tab the cell belongs to.</param>
    /// <param name="rowDisplay">The row key as shown.</param>
    /// <param name="columnDisplay">The column key as shown.</param>
    /// <param name="refs">The instances in the cell.</param>
    public TagMatrixCellViewModel(TagMatrixTabViewModel owner, string rowDisplay, string columnDisplay, IReadOnlyList<TagInstanceRef> refs)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(refs);
        _owner = owner;
        RowDisplay = rowDisplay;
        ColumnDisplay = columnDisplay;
        Refs = refs;
    }

    public string RowDisplay { get; }

    public string ColumnDisplay { get; }

    public IReadOnlyList<TagInstanceRef> Refs { get; }

    public int Count => Refs.Count;

    public bool IsEmpty => Refs.Count == 0;

    public string CountText => Refs.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>"7 · execute × ramp: send to Review".</summary>
    public string Tip => $"{Count} · {RowDisplay} × {ColumnDisplay}: send to Review";

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _owner.Open(this);

    private bool CanOpen() => Refs.Count > 0;
}
