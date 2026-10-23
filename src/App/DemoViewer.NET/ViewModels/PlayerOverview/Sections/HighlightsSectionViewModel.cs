#region

using System.Collections.Concurrent;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject's highlights across the library, best first: every highlight the scan found in their demos
///     (<see cref="IAnalysisFacts.Highlights" />) whose slot is one the subject held in that demo. Each demo's list
///     is read once off the UI thread and kept until its index row says the scan moved. The section links to
///     the Reels tab's Add-clips picker with the subject ticked, where the clips are staged.
/// </summary>
public sealed partial class HighlightsSectionViewModel : PlayerOverviewSection
{
    // A demo's highlights, every player's, with the row values they were read under.
    private readonly ConcurrentDictionary<string, Read> _read = new(StringComparer.Ordinal);
    private readonly PlayerOverviewServices _services;
    private int _load;
    private ulong? _subject;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _kindText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private string _loadText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(ShowEmpty))]
    private IReadOnlyList<PlayerHighlightItem> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnscanned))]
    private string _unscannedText = "";

    /// <param name="services">The facts that hold the highlights, the directory that opens demos and the picker link.</param>
    public HighlightsSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "highlights";

    /// <inheritdoc />
    public override PlayerOverviewPageKind Page => PlayerOverviewPageKind.Achievements;

    /// <summary>True while the demos' highlights are being read.</summary>
    public bool IsLoading => LoadText.Length > 0;

    /// <summary>True when the subject has a highlight.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True when nothing is being read and the subject has no highlight.</summary>
    public bool ShowEmpty => !HasRows && !IsLoading;

    /// <summary>True when some of the subject's demos have not been scanned for highlights.</summary>
    public bool HasUnscanned => UnscannedText.Length > 0;

    /// <summary>True when there is a Reels tab to open the picker on.</summary>
    public bool CanAddClips => _services.AddClips is not null;

    /// <summary>The latest read of the highlights; completed when nothing is being read.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    partial void OnLoadTextChanged(string value) => OnPropertyChanged(nameof(ShowEmpty));

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A read still running for an earlier refresh finishes into the cache and is not shown.
        int load = ++_load;
        if (context.Player is not { } player || _services.Facts is not { } facts)
        {
            IsVisible = false;
            Rows = [];
            _subject = null;
            LoadText = "";
            return;
        }

        // Only a subject's first read says so; a demo the queue finishes later is read in behind what is shown.
        bool quiet = _subject == player.SteamId64;
        _subject = player.SteamId64;
        int unscanned = context.Matches.Count(m => string.IsNullOrEmpty(m.Row.ConfigFingerprint));
        UnscannedText = unscanned == 0
            ? ""
            : unscanned == 1
                ? "1 match not scanned for highlights yet"
                : string.Create(CultureInfo.InvariantCulture, $"{unscanned} matches not scanned for highlights yet");

        List<PlayerMatch> unread = [.. context.Matches.Where(m => m.Row.HighlightCount > 0 && !IsRead(m.Row))];
        Show(context.Matches);
        IsVisible = true;
        if (unread.Count == 0)
        {
            LoadText = "";
            return;
        }

        Loading = LoadAsync(facts, context.Matches, unread, load, quiet);
    }

    private async Task LoadAsync(IAnalysisFacts facts, IReadOnlyList<PlayerMatch> matches, List<PlayerMatch> unread, int load,
        bool quiet)
    {
        if (!quiet)
        {
            LoadText = "Reading highlights";
        }

        // The count must be written from this flow, not an IProgress callback: a posted callback can run
        // after the clear below and leave IsLoading true.
        for (int i = 0; i < unread.Count; i++)
        {
            DemoCacheIndexEntry row = unread[i].Row;
            await Task.Run(() =>
            {
                try
                {
                    _read[row.Path] = new Read(row.HighlightCount, row.ConfigFingerprint, facts.Highlights(row.Path));
                }
                catch (Exception)
                {
                    // A sidecar that will not read leaves its demo out; the next refresh tries again.
                }
            });

            if (_load == load && !quiet)
            {
                LoadText = string.Create(CultureInfo.InvariantCulture, $"Reading highlights {i + 1} of {unread.Count}");
            }
        }

        if (_load == load)
        {
            Show(matches);
            LoadText = "";
        }
    }

    private bool IsRead(DemoCacheIndexEntry row) =>
        _read.TryGetValue(row.Path, out Read? read)
        && read.Count == row.HighlightCount
        && string.Equals(read.Fingerprint, row.ConfigFingerprint, StringComparison.Ordinal);

    // The subject's highlights from every demo read so far: the slot is the identity, as in the scan's own rows.
    private void Show(IReadOnlyList<PlayerMatch> matches)
    {
        List<PlayerHighlightItem> rows = [];
        HashSet<string> demos = new(StringComparer.Ordinal);
        foreach (PlayerMatch match in matches)
        {
            if (match.Row.HighlightCount == 0 || !IsRead(match.Row))
            {
                continue;
            }

            foreach (LibraryHighlight highlight in _read[match.Row.Path].Highlights)
            {
                if (match.Slots.Contains(highlight.PlayerSlot))
                {
                    rows.Add(new PlayerHighlightItem(match, highlight));
                    demos.Add(match.Row.Path);
                }
            }
        }

        // Best first, as the picker orders them; ties newest match first, then in the order they fired.
        rows.Sort((a, b) => b.Highlight.Score != a.Highlight.Score
            ? b.Highlight.Score.CompareTo(a.Highlight.Score)
            : b.Match.TimeTicks != a.Match.TimeTicks
                ? b.Match.TimeTicks.CompareTo(a.Match.TimeTicks)
                : a.Highlight.Tick.CompareTo(b.Highlight.Tick));
        Rows = rows;
        CountText = rows.Count == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"{rows.Count} in {demos.Count} {(demos.Count == 1 ? "match" : "matches")}");
        KindText = string.Join(", ", rows
            .GroupBy(r => r.KindText)
            .OrderBy(g => KindOrder(g.Key))
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {KindLabel(g.Key, g.Count())}")));
    }

    private static int KindOrder(string kind) => kind switch { "Highlight" => 0, "Funny" => 1, "Lowlight" => 2, _ => 3 };

    private static string KindLabel(string kind, int count) => kind switch
    {
        "Funny" => "funny",
        _ => (count == 1 ? kind : kind + "s").ToLowerInvariant()
    };

    /// <summary>Opens the Reels tab's Add-clips picker with the subject's player filter ticked.</summary>
    [RelayCommand]
    private void AddClips()
    {
        if (_subject is { } subject)
        {
            _services.AddClips?.Invoke(subject);
        }
    }

    /// <summary>Opens the row's demo from the first of its paths on disk, or marks the file missing.</summary>
    [RelayCommand]
    private void OpenDemo(PlayerHighlightItem? row)
    {
        if (row is null)
        {
            return;
        }

        string? path = _services.Directory.OpenPath(row.Match);
        row.IsFileMissing = path is null;
        if (path is not null)
        {
            _services.OpenDemo(path);
        }
    }

    private sealed record Read(int Count, string? Fingerprint, IReadOnlyList<LibraryHighlight> Highlights);
}

/// <summary>One of the subject's highlights, every cell formatted.</summary>
public sealed partial class PlayerHighlightItem : ObservableObject
{
    /// <summary>True when <see cref="HighlightsSectionViewModel.OpenDemoCommand" /> found no path on disk.</summary>
    [ObservableProperty]
    private bool _isFileMissing;

    /// <param name="match">The demo it fired in.</param>
    /// <param name="highlight">The highlight.</param>
    public PlayerHighlightItem(PlayerMatch match, LibraryHighlight highlight)
    {
        Match = match ?? throw new ArgumentNullException(nameof(match));
        Highlight = highlight ?? throw new ArgumentNullException(nameof(highlight));
        // The title embeds the player's name as it was at the scan: raw, so sanitized here.
        Title = DisplayText.Sanitize(string.IsNullOrEmpty(highlight.Title) ? highlight.HighlightId.Replace('_', ' ') : highlight.Title);
        MapText = DemoEntry.PrettifyMap(match.Row.Map);
        DateText = HistoryRowItem.DateOf(match);
        RoundText = highlight.RoundNumber > 0 ? string.Create(CultureInfo.InvariantCulture, $"R{highlight.RoundNumber}") : "";
        KindText = highlight.Kind;
        ScoreText = highlight.Score.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The demo.</summary>
    public PlayerMatch Match { get; }

    /// <summary>The highlight as the library holds it.</summary>
    public LibraryHighlight Highlight { get; }

    /// <summary>The rendered title, sanitized.</summary>
    public string Title { get; }

    /// <summary>The map, prettified.</summary>
    public string MapText { get; }

    /// <summary>The match time, local, "yyyy-MM-dd HH:mm".</summary>
    public string DateText { get; }

    /// <summary>"R7", or empty when the round is unknown.</summary>
    public string RoundText { get; }

    /// <summary>"Highlight", "Funny" or "Lowlight".</summary>
    public string KindText { get; }

    /// <summary>True for a funny highlight.</summary>
    public bool IsFunny => KindText == "Funny";

    /// <summary>True for a lowlight.</summary>
    public bool IsLowlight => KindText == "Lowlight";

    /// <summary>The authored score, 0-100.</summary>
    public string ScoreText { get; }
}
