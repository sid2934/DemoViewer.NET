#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Strats section's Detected inbox (Strat Mining): the patterns the miner found for the tab's map, side and
///     book, each with where it was seen, its record and the utility its rounds share. A pattern becomes a strat only
///     when the user adds it to the book; a dismissed one stays hidden across re-mines.
/// </summary>
public sealed partial class DetectedStratsViewModel : ObservableObject, IDisposable
{
    /// <summary>Seconds a member round opens before the take, so the approach is on screen.</summary>
    public const int ExecuteLeadSeconds = 10;

    private readonly Func<string?, LoadedMapAsset?>? _mapLoader;
    private readonly StratMiningService? _mining;
    private readonly Action<Guid> _openStrat;
    private readonly Func<ISituationPlayback?> _playback;
    private readonly Action<Action> _post;
    private readonly Func<StratOwner?> _targetBook;
    private readonly Func<Guid, string?> _teamName;
    private (string? Map, string? Side, StratOwner? Owner) _filter;
    private CancellationTokenSource? _previewCancel;

    /// <summary>The open preview of the selected pattern, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewing), nameof(ShowPatternDetail))]
    private StratPreviewViewModel? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DetectedRowViewModel? _selectedRow;

    // Dismissed and in-book patterns: hidden unless on (generated-content.md).
    [ObservableProperty]
    private bool _showSettled;

    private int _settledCount;

    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="mining">The miner; null on a host without a demo cache, where the inbox says so.</param>
    /// <param name="playback">Opens a member round in 2D Playback, resolved at click time; null offers no Open.</param>
    /// <param name="teamName">A team's display name, or null.</param>
    /// <param name="targetBook">The book Add to book writes into: the tab's selected book.</param>
    /// <param name="openStrat">Shows a strat in the book once it exists.</param>
    /// <param name="post">UI-thread marshal.</param>
    /// <param name="mapLoader">The preview canvas's map loader; the baked assets when null.</param>
    public DetectedStratsViewModel(StratMiningService? mining, Func<ISituationPlayback?> playback, Func<Guid, string?> teamName,
        Func<StratOwner?> targetBook, Action<Guid> openStrat, Action<Action>? post = null,
        Func<string?, LoadedMapAsset?>? mapLoader = null)
    {
        _mining = mining;
        _mapLoader = mapLoader;
        _playback = playback;
        _teamName = teamName;
        _targetBook = targetBook;
        _openStrat = openStrat;
        _post = post ?? (a => a());
        if (_mining is not null)
        {
            _mining.Changed += Refresh;
        }

        Refresh();
    }

    public bool IsAvailable => _mining is not null;

    public bool IsMining => _mining?.IsMining ?? false;

    public bool HasSelection => SelectedRow is not null;

    public bool IsPreviewing => Preview is not null;

    /// <summary>The pattern's own detail shows when a row is selected and no preview is open.</summary>
    public bool ShowPatternDetail => SelectedRow is not null && Preview is null;

    public ObservableCollection<DetectedRowViewModel> Rows { get; } = [];

    /// <summary>Patterns shown and not dismissed: the count on the toggle.</summary>
    public int NewCount => Rows.Count(r => r.State == GeneratedState.New);

    public void Dispose()
    {
        ClosePreview();
        if (_mining is not null)
        {
            _mining.Changed -= Refresh;
        }
    }

    /// <summary>Called when the tab's filters change; a team book shows only that team's patterns.</summary>
    public void SetFilter(string? map, string? side, StratOwner? owner)
    {
        _filter = (map, side, owner);
        Refresh();
    }

    /// <summary>The first look at the inbox mines when nothing has been mined yet.</summary>
    public void EnsureMined()
    {
        if (_mining is { MinedUtc: null, IsMining: false })
        {
            Find();
        }
    }

    [RelayCommand]
    private void Find()
    {
        if (_mining is null)
        {
            return;
        }

        _ = _mining.MineAsync();
        OnPropertyChanged(nameof(IsMining));
        Refresh();
    }

    /// <summary>Discards the preview and any build still running for it.</summary>
    public void ClosePreview()
    {
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = null;
        StratPreviewViewModel? shown = Preview;
        Preview = null;
        shown?.Dispose();
    }

    /// <summary>
    ///     Builds the strat the selected pattern would become through the same Build that Add to book saves, off the
    ///     UI thread, and shows it read-only. Nothing is written.
    /// </summary>
    [RelayCommand]
    private Task PreviewStrat() => Rebuild(null);

    private async Task Rebuild(string? notice)
    {
        if (_mining is null || SelectedRow is not { StratId: null } row
                            || _mining.Patterns.FirstOrDefault(p => p.Pattern.Key == row.Key)?.Pattern is not { } pattern)
        {
            return;
        }

        ClosePreview();
        StratOwner owner = _targetBook() ?? StratOwner.Me();
        StratPreviewViewModel preview = new(pattern, row.Title, _mapLoader, _post) { Notice = notice };
        CancellationTokenSource cancel = new();
        _previewCancel = cancel;
        Preview = preview;
        CancellationToken token = cancel.Token;

        StratDocument? built;
        try
        {
            built = await _mining.PreviewAsync(pattern, owner, DateTime.UtcNow, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            built = null;
        }

        _post(() =>
        {
            if (ReferenceEquals(Preview, preview) && !token.IsCancellationRequested)
            {
                preview.Show(built);
            }
        });
    }

    [RelayCommand]
    private void BackToDetected() => ClosePreview();

    partial void OnSelectedRowChanged(DetectedRowViewModel? value)
    {
        // By key: Refresh rebuilds every row on each store or mining change and reselects the same pattern.
        if (Preview is { } shown && shown.Key != value?.Key)
        {
            ClosePreview();
        }

        OnPropertyChanged(nameof(ShowPatternDetail));
    }

    [RelayCommand]
    private async Task AddToBook()
    {
        if (_mining is null || SelectedRow is not { StratId: null } row || _targetBook() is not { } book)
        {
            return;
        }

        StratDocument? doc;
        if (Preview is { IsReady: true, Document: { } built } preview)
        {
            PromoteResult result = _mining.Promote(preview.Pattern, built, book);
            if (result.PatternChanged)
            {
                const string changed = "This pattern changed; review again.";
                StatusLine = changed;
                await Rebuild(changed);
                return;
            }

            doc = result.Document;
        }
        else
        {
            doc = _mining.Promote(row.Key, book);
        }

        StatusLine = doc is not null ? $"Added \"{doc.Name}\" to the book."
            : _mining.StateProblem is { } problem ? $"Not added: {problem}"
            : "Could not build this strat: the round's cached files are gone. Find strats again.";
        if (doc is not null)
        {
            _openStrat(doc.Id);
        }
    }

    [RelayCommand]
    private void OpenStrat()
    {
        if (SelectedRow?.StratId is { } id)
        {
            _openStrat(id);
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (SelectedRow is { } row)
        {
            _mining?.Dismiss(row.Key);
            NoteUnsaved("Dismissed");
        }
    }

    [RelayCommand]
    private void Restore()
    {
        if (SelectedRow is { } row)
        {
            _mining?.Restore(row.Key);
            NoteUnsaved("Restored");
        }
    }

    [RelayCommand]
    private async Task OpenRound(DetectedMemberRow? member)
    {
        if (member is null || _playback() is not { } playback)
        {
            return;
        }

        if (!await playback.SeekAsync(member.DemoPath, member.OpenTick))
        {
            StatusLine = $"Could not open {member.FileName}.";
        }
    }

    partial void OnShowSettledChanged(bool value) => Refresh();

    /// <summary>"Show settled (n)": the dismissed and in-book patterns under the current filters.</summary>
    public string SettledLabel => GeneratedInbox.SettledLabel(_settledCount);

    private void Refresh()
    {
        if (_mining is null)
        {
            StatusLine = "Strat mining needs the demo cache, which this host does not have.";
            return;
        }

        string? keep = SelectedRow?.Key;
        List<DetectedPattern> matching = [.. _mining.Patterns.Where(p => Matches(p.Pattern))];
        _settledCount = GeneratedCounts.Of(matching.Select(p => p.State)).Settled;
        List<DetectedRowViewModel> rows =
        [
            .. matching
                .Where(p => GeneratedInbox.Shows(p.State, ShowSettled))
                .OrderBy(p => p.Pattern.UtilityCompared ? 0 : 1)
                .ThenByDescending(p => p.Pattern.Support)
                .ThenBy(p => p.Pattern.Spread)
                .Select(p => new DetectedRowViewModel(p, _teamName, ExecuteLeadSeconds))
        ];
        Rows.Clear();
        foreach (DetectedRowViewModel row in rows)
        {
            Rows.Add(row);
        }

        SelectedRow = keep is null ? null : Rows.FirstOrDefault(r => r.Key == keep);
        OnPropertyChanged(nameof(NewCount));
        OnPropertyChanged(nameof(SettledLabel));
        OnPropertyChanged(nameof(IsMining));
        OnPropertyChanged(nameof(StateProblem));
        OnPropertyChanged(nameof(HasStateProblem));
        StatusLine = Status(rows.Count);
    }

    /// <summary>Why the dismissals file is not in use, or null.</summary>
    public string? StateProblem => _mining?.StateProblem;

    public bool HasStateProblem => StateProblem is not null;

    private void NoteUnsaved(string what)
    {
        if (_mining?.StateProblem is { } problem)
        {
            StatusLine = $"{what} for this session only: {problem}";
        }
    }

    private string Status(int shown)
    {
        if (_mining is null)
        {
            return "";
        }

        if (_mining.IsMining)
        {
            return "Finding strats in the library…";
        }

        if (_mining.MinedUtc is null)
        {
            return "Not searched yet.";
        }

        (int demos, _) = _mining.LastRead;
        string read = demos > 0 ? string.Create(CultureInfo.InvariantCulture, $" across {demos} demos") : "";
        return shown == 0
            ? $"No repeated setup or execute here{read}."
            : string.Create(CultureInfo.InvariantCulture, $"{shown} {(shown == 1 ? "pattern" : "patterns")}{read}.");
    }

    private bool Matches(MinedPattern p)
    {
        (string? map, string? side, StratOwner? owner) = _filter;
        if (map is not null && !string.Equals(p.Map, map, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (side is not null && side != (p.Side == 2 ? StratVocabulary.SideT : StratVocabulary.SideCt))
        {
            return false;
        }

        return owner?.TeamId is not { } team || p.Teams.Contains(team);
    }
}

/// <summary>One detected pattern in the list, and its detail when selected.</summary>
public sealed class DetectedRowViewModel
{
    public DetectedRowViewModel(DetectedPattern detected, Func<Guid, string?> teamName, int executeLeadSeconds)
    {
        MinedPattern p = detected.Pattern;
        Key = p.Key;
        IsDismissed = detected.Dismissed;
        State = detected.State;
        StratId = detected.StratId;
        Title = MinedStratBuilder.Name(p);
        string side = p.Side == 2 ? "T" : "CT";
        int known = p.Members.Count(m => m.Won is not null);
        List<string> teams = [.. p.Teams.Select(teamName).OfType<string>().Order(StringComparer.Ordinal)];
        TeamsLine = teams.Count > 0 ? $"Run by {string.Join(", ", teams)}" : "No known team";
        Summary = string.Create(CultureInfo.InvariantCulture,
            $"{p.Map} · {side} · {p.Support} rounds in {p.Demos} {(p.Demos == 1 ? "demo" : "demos")}")
                  + (known > 0 ? string.Create(CultureInfo.InvariantCulture, $" · won {p.Wins}/{known}") : "")
                  + (p.UtilityCompared ? "" : " · positions only")
                  + (detected.StratId is not null ? " · in book" : detected.Dismissed ? " · dismissed" : "");
        UtilityLine = p.UtilityCompared
            ? "Utility and positions compared."
            : "Positions and timing only: some of these demos have no grenade rows. Index their grenades in the Utility Book to compare utility.";
        TimingLine = p.Kind == PatternKind.Execute
            ? string.Create(CultureInfo.InvariantCulture, $"Site taken {p.Medoid.AnchorSeconds:0} s after freeze end in the most typical round.")
            : "The opening 15 to 25 seconds after freeze end.";
        CommonThrows =
        [
            .. p.CommonThrows.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.Throw.Kind} into {c.Throw.LandingPlace ?? "an unnamed spot"} at {(p.Kind == PatternKind.Execute ? $"{c.Throw.Seconds:+0;-0} s from the take" : $"{c.Throw.Seconds:0} s")}, in {c.Rounds} of {p.Members.Count} rounds"))
        ];
        Members =
        [
            .. p.Members.Select(m => new DetectedMemberRow(
                m.DemoPath,
                Path.GetFileName(m.DemoPath),
                m.Round,
                m.Won switch { true => "won", false => "lost", _ => "" },
                m.TeamId is { } id ? teamName(id) : null,
                p.Kind == PatternKind.Execute ? Math.Max(m.FreezeEndTick, m.AnchorTick - executeLeadSeconds * Math.Max(1, m.TickRate)) : m.FreezeEndTick))
        ];
    }

    public string Key { get; }

    public string Title { get; }

    public string Summary { get; }

    public string TeamsLine { get; }

    public string UtilityLine { get; }

    public string TimingLine { get; }

    public bool IsDismissed { get; }

    public GeneratedState State { get; }

    public Guid? StratId { get; }

    public bool CanAdd => StratId is null;

    public bool IsInBook => StratId is not null;

    public IReadOnlyList<string> CommonThrows { get; }

    public bool HasCommonThrows => CommonThrows.Count > 0;

    public IReadOnlyList<DetectedMemberRow> Members { get; }
}

/// <summary>A member round: the demo, the round, how it ended and where Open lands.</summary>
public sealed record DetectedMemberRow(string DemoPath, string FileName, int Round, string Result, string? Team, int OpenTick)
{
    public string Line => string.Create(CultureInfo.InvariantCulture, $"Round {Round} · {FileName}")
                          + (Result.Length > 0 ? $" · {Result}" : "")
                          + (Team is null ? "" : $" · {Team}");
}
