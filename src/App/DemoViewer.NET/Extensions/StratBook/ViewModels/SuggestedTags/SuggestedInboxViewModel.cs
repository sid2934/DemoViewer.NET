#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.Generated;

#endregion

namespace DemoViewer.NET.ViewModels.SuggestedTags;

/// <summary>One proposal as the Suggested section lists it.</summary>
public sealed class SuggestedInboxRow(SuggestedInboxItem item)
{
    public SuggestedInboxItem Item { get; } = item;

    private TagProposal Proposal => Item.Entry.Proposal;

    /// <summary><c>code@site</c>.</summary>
    public string Title => ProposalTrack.LabelOf(Proposal);

    /// <summary>"de_mirage · spirit-vs-furia-m1.dem · r13 · T".</summary>
    public string WhereText => string.Join(" · ",
        new[] { Item.Map ?? "no map", Item.FileName, string.Create(CultureInfo.InvariantCulture, $"r{Proposal.Round}"), ProposalIds.SideName(Proposal.Side) });

    public string DetectorText => Proposal.Detector;

    public string ConfidenceText => Proposal.Confidence.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"0.60 · default", then " · dismissed" on a settled row.</summary>
    public string MetaText => $"{ConfidenceText} · {DetectorText}" + (StateText.Length > 0 ? $" · {StateText}" : "");

    /// <summary>Why it fired, the first two evidence lines.</summary>
    public string ReasonText => Proposal.Evidence.Count == 0 ? "" : string.Join("; ", Proposal.Evidence.Take(2).Select(e => e.Text));

    public GeneratedState State => Item.Entry.State;

    public bool IsNew => State == GeneratedState.New;

    public bool IsDismissed => State == GeneratedState.Dismissed;

    /// <summary>"accepted" or "dismissed" on a settled row.</summary>
    public string StateText => State switch
    {
        GeneratedState.Accepted => "accepted",
        GeneratedState.Dismissed => "dismissed",
        _ => ""
    };
}

/// <summary>
///     The Strat Book's Suggested section: every demo's tag suggestions in one list, filtered by map, side, detector
///     and confidence. Accept writes the tag into that demo's document, Dismiss and Restore follow the one inbox rule,
///     and Open takes 2D Playback to the proposal. Only new suggestions show until "Show settled" is on.
/// </summary>
public sealed partial class SuggestedInboxViewModel : ObservableObject, IWorkspaceTabViewModel, IDisposable
{
    public const string AllMaps = "all maps";
    public const string BothSides = "both sides";
    public const string AllDetectors = "all detectors";

    private readonly SuggestedInboxService? _inbox;
    private readonly Func<ISituationPlayback?> _playback;
    private int _settledCount;

    [ObservableProperty]
    private string _detectorFilter = AllDetectors;

    [ObservableProperty]
    private string _mapFilter = AllMaps;

    [ObservableProperty]
    private double _minConfidence;

    [ObservableProperty]
    private bool _showSettled;

    [ObservableProperty]
    private string _sideFilter = BothSides;

    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="inbox">The library-wide list; null on a host without a demo cache.</param>
    /// <param name="playback">The seam that opens a demo in 2D Playback at a tick.</param>
    public SuggestedInboxViewModel(SuggestedInboxService? inbox, Func<ISituationPlayback?>? playback = null)
    {
        _inbox = inbox;
        _playback = playback ?? (() => null);
        if (_inbox is not null)
        {
            _inbox.Changed += Refresh;
        }

        Refresh();
    }

    public BulkObservableCollection<SuggestedInboxRow> Rows { get; } = [];

    public BulkObservableCollection<string> Maps { get; } = [AllMaps];

    public IReadOnlyList<string> Sides { get; } = [BothSides, "T", "CT"];

    public BulkObservableCollection<string> Detectors { get; } = [AllDetectors];

    public bool HasRows => Rows.Count > 0;

    /// <summary>"Show settled (n)": accepted and dismissed suggestions under the filters.</summary>
    public string SettledLabel => GeneratedInbox.SettledLabel(_settledCount);

    public void Dispose()
    {
        if (_inbox is not null)
        {
            _inbox.Changed -= Refresh;
        }
    }

    public void OnActivated(IModuleContext context)
    {
        if (_inbox is { IsLoaded: false, IsLoading: false })
        {
            _ = _inbox.LoadAsync();
            StatusLine = "Reading suggestions across the library…";
        }
    }

    public void OnDeactivated()
    {
    }

    partial void OnMapFilterChanged(string value) => Refresh();

    partial void OnSideFilterChanged(string value) => Refresh();

    partial void OnDetectorFilterChanged(string value) => Refresh();

    partial void OnMinConfidenceChanged(double value) => Refresh();

    partial void OnShowSettledChanged(bool value) => Refresh();

    [RelayCommand]
    private void Accept(SuggestedInboxRow? row) => Verdict(row, i => _inbox!.Accept(i), "The tag could not be written.");

    [RelayCommand]
    private void Dismiss(SuggestedInboxRow? row) => Verdict(row, i => _inbox!.Dismiss(i), "The dismissal could not be written.");

    [RelayCommand]
    private void Restore(SuggestedInboxRow? row) => Verdict(row, i => _inbox!.Restore(i), "The restore could not be written.");

    [RelayCommand]
    private async Task Open(SuggestedInboxRow? row)
    {
        if (row is null || _playback() is not { } playback)
        {
            return;
        }

        if (!await playback.SeekAsync(row.Item.DemoPath, row.Item.Entry.Proposal.FromTick))
        {
            StatusLine = $"Could not open {row.Item.FileName}.";
        }
    }

    private void Verdict(SuggestedInboxRow? row, Func<SuggestedInboxItem, bool> write, string failed)
    {
        if (_inbox is null || row is null)
        {
            return;
        }

        if (!write(row.Item))
        {
            StatusLine = failed;
        }
    }

    private void Refresh()
    {
        if (_inbox is null)
        {
            StatusLine = "Suggested tags need the demo cache, which this host does not have.";
            return;
        }

        IReadOnlyList<SuggestedInboxItem> items = _inbox.Items;
        Sync(Maps, AllMaps, items.Select(i => i.Map ?? "no map"));
        Sync(Detectors, AllDetectors, items.Select(i => i.Entry.Proposal.Detector));

        List<SuggestedInboxRow> shown = [];
        int settled = 0, pending = 0;
        foreach (SuggestedInboxItem item in items)
        {
            if (!Matches(item))
            {
                continue;
            }

            bool isNew = item.Entry.State == GeneratedState.New;
            settled += isNew ? 0 : 1;
            pending += isNew ? 1 : 0;
            if (GeneratedInbox.Shows(item.Entry.State, ShowSettled))
            {
                shown.Add(new SuggestedInboxRow(item));
            }
        }

        _settledCount = settled;
        Rows.ReplaceAll(shown);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(SettledLabel));
        int demos = items.Select(i => i.DemoPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        StatusLine = _inbox.IsLoaded
            ? string.Create(CultureInfo.InvariantCulture,
                $"{pending} new {(pending == 1 ? "suggestion" : "suggestions")} across {demos} {(demos == 1 ? "demo" : "demos")}.")
            : _inbox.IsLoading ? "Reading suggestions across the library…" : "Not read yet.";
    }

    private bool Matches(SuggestedInboxItem item)
    {
        TagProposal p = item.Entry.Proposal;
        return (MapFilter == AllMaps || string.Equals(item.Map ?? "no map", MapFilter, StringComparison.OrdinalIgnoreCase))
               && (SideFilter == BothSides || ProposalIds.SideName(p.Side) == SideFilter)
               && (DetectorFilter == AllDetectors || p.Detector == DetectorFilter)
               && p.Confidence >= MinConfidence;
    }

    // Keeps the "all" entry first and the rest sorted; leaves the collection alone when nothing changed.
    private static void Sync(BulkObservableCollection<string> target, string all, IEnumerable<string> values)
    {
        List<string> next = [all, .. values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        if (!target.SequenceEqual(next, StringComparer.Ordinal))
        {
            target.ReplaceAll(next);
        }
    }
}
