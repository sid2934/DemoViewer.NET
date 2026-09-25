#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     One detector's line in the Settings tuning table (suggested-tags.md §3.7): the verdict history
///     plus recall/precision at whatever profile was last previewed or saved. Display-only, rebuilt
///     wholesale from a fresh <see cref="TuningReport" /> rather than patched field by field.
/// </summary>
public sealed class TuningDetectorRow
{
    internal TuningDetectorRow(DetectorTuningRow row)
    {
        Detector = row.Detector;
        Made = row.Made;
        Accepted = row.Accepted;
        Edited = row.Edited;
        Rejected = row.Rejected;
        Pending = row.Pending;
        RecallText = row.Recall is { } r ? Percent(r) : "no data";
        PrecisionText = row.Precision is { } p ? Percent(p) : "no data";
    }

    public string Detector { get; }
    public int Made { get; }
    public int Accepted { get; }
    public int Edited { get; }
    public int Rejected { get; }
    public int Pending { get; }

    /// <summary>"no data" rather than 0% when nothing exists to score against (the Sight-columns rule).</summary>
    public string RecallText { get; }

    /// <inheritdoc cref="RecallText" />
    public string PrecisionText { get; }

    // "P0" inserts a locale space before the sign in the invariant culture ("100 %"); the tuning table
    // wants a plain "100%" regardless of host locale.
    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
///     One tunable number in the Settings tuning view: a detector's parameter, its shipped default for
///     the reset affordance, and the value the next preview or save uses.
/// </summary>
public sealed partial class TuningParameterRow : ObservableObject
{
    private readonly Action _onChanged;

    internal TuningParameterRow(string detector, DetectorParameter parameter, double value, Action onChanged)
    {
        Detector = detector;
        Name = parameter.Name;
        Meaning = parameter.Meaning;
        ShippedDefault = parameter.Default;
        _value = value;
        _onChanged = onChanged;
    }

    /// <summary>The detector id: the profile section this parameter lives in.</summary>
    public string Detector { get; }

    /// <summary>The parameter's key in the profile's detector section.</summary>
    public string Name { get; }

    /// <summary>What moving it does, straight from the detector (§3.7 shows this on the tuning view).</summary>
    public string Meaning { get; }

    /// <summary>The shipped value, for the "reset to shipped" affordance.</summary>
    public double ShippedDefault { get; }

    /// <summary>"execute.N": the row's label.</summary>
    public string Label => $"{Detector}.{Name}";

    /// <summary>The value the next preview or save uses; edited in place, never auto-persisted.</summary>
    [ObservableProperty]
    private double _value;

    partial void OnValueChanged(double value) => _onChanged();
}

/// <summary>
///     Backs the Settings tuning section (suggested-tags.md §3.7, step 6): the stored verdict table,
///     every detector's tunable numbers, and the "preview then save" flow — a parameter edit only takes
///     effect in the table after <see cref="PreviewCommand" /> re-runs detection in memory, and only
///     reaches <c>profile.json</c> after <see cref="SaveCommand" />.
///     <para>
///         Null <see cref="SuggestedTagsTuningService" />/<see cref="ProfileStore" /> (the browser host,
///         or a test that does not wire them) makes <see cref="CanManageTuning" /> false and the section
///         hides, per §3.8: "no tuning view" there.
///     </para>
/// </summary>
public sealed partial class SuggestedTagsTuningViewModel : ObservableObject
{
    private readonly ProfileStore? _profiles;
    private readonly SuggestedTagsTuningService? _tuning;
    private TuningReport _baseline = new([], 0, 0);
    private CancellationTokenSource? _previewCts;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _statusText = "";

    /// <param name="tuning">The harness; null hides the section (browser, or an unwired test).</param>
    /// <param name="profiles">Where a save lands; null hides the section.</param>
    /// <param name="isBrowser">Whether the host is the WASM head (no tuning view there either way).</param>
    public SuggestedTagsTuningViewModel(SuggestedTagsTuningService? tuning, ProfileStore? profiles, bool isBrowser = false)
    {
        _tuning = tuning;
        _profiles = profiles;
        CanManageTuning = !isBrowser && tuning is not null && profiles is not null;
        if (CanManageTuning)
        {
            BuildParameterRows();
            RefreshReport();
        }
    }

    /// <summary>Whether the section is usable: desktop, with both the harness and the profile store wired.</summary>
    public bool CanManageTuning { get; }

    /// <summary>One row per shipped detector, in <see cref="ProposalDetection.All" /> order.</summary>
    public ObservableCollection<TuningDetectorRow> DetectorRows { get; } = [];

    /// <summary>Every detector's tunable numbers, grouped by <see cref="TuningParameterRow.Detector" /> in the view.</summary>
    public ObservableCollection<TuningParameterRow> ParameterRows { get; } = [];

    /// <summary>The folder <c>profile.json</c> lives in, for the hint text; null on the browser.</summary>
    public string? ProfileFolderPath { get; } = AppPaths.SuggestedTagsDirectory;

    /// <summary>Re-reads the stored table: verdict counts and recall/precision at the SAVED profile.</summary>
    [RelayCommand]
    private void Refresh() => RefreshReport();

    /// <summary>
    ///     Builds a candidate profile from the edited parameter rows and re-runs detection over every
    ///     scored demo, in memory: nothing is written. Only <see cref="TuningDetectorRow.RecallText" />
    ///     and <see cref="TuningDetectorRow.PrecisionText" /> move; the verdict counts stay the ones
    ///     <see cref="Refresh" /> last loaded, because they are history, not a function of the candidate.
    /// </summary>
    [RelayCommand]
    private async Task Preview()
    {
        if (_tuning is null)
        {
            return;
        }

        _previewCts?.Cancel();
        CancellationTokenSource cts = new();
        _previewCts = cts;
        IsBusy = true;
        StatusText = "Re-running detectors over the scored demos…";
        try
        {
            DetectorProfile candidate = BuildCandidate();
            TuningReport report = await _tuning.PreviewAsync(candidate, _baseline, _tuning.ScoredDemoPaths(), cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ApplyReport(report);
            StatusText = report.DemosWithHandTags == 0
                ? "Previewed — no hand-tagged demos to score recall or precision against yet."
                : $"Previewed over {report.DemosWithHandTags} hand-tagged demo(s).";
        }
        catch (OperationCanceledException)
        {
            // A later Preview or the view closing superseded this one.
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    ///     Persists the edited parameters as the profile in force (suggested-tags.md §3.7): this is what
    ///     changes the detector-set fingerprint and marks every demo's proposals stale for the evaluator
    ///     to rebuild in the background. The verdict history and any prior preview's recall/precision are
    ///     unaffected until the next <see cref="Refresh" /> or <see cref="Preview" />.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (_profiles is null)
        {
            return;
        }

        _profiles.Save(BuildCandidate());
        _tuning?.InvalidateCache(); // a saved profile is worth a fresh parse next time a map's regions changed underneath it
        IsDirty = false;
        StatusText = "Saved. Demos built under the old parameters will rebuild in the background.";
    }

    /// <summary>Sets every parameter row back to the shipped value, without saving.</summary>
    [RelayCommand]
    private void ResetToShipped()
    {
        foreach (TuningParameterRow row in ParameterRows)
        {
            row.Value = row.ShippedDefault;
        }
    }

    private void RefreshReport()
    {
        if (_tuning is null)
        {
            return;
        }

        _baseline = _tuning.BuildStoredReport();
        ApplyReport(_baseline);
        IsDirty = false;
        StatusText = _baseline.DemosWithVerdicts == 0
            ? "No demo has a Suggested Tags verdict yet."
            : $"{_baseline.DemosWithVerdicts} demo(s) with verdicts, {_baseline.DemosWithHandTags} with hand tags to score against.";
    }

    private void ApplyReport(TuningReport report)
    {
        DetectorRows.Clear();
        foreach (DetectorTuningRow row in report.Rows)
        {
            DetectorRows.Add(new TuningDetectorRow(row));
        }
    }

    private void BuildParameterRows()
    {
        DetectorProfile profile = _profiles!.Current;
        foreach (IProposalDetector detector in ProposalDetection.All)
        {
            foreach (DetectorParameter parameter in detector.Parameters)
            {
                double value = profile.Get(detector.Id, parameter.Name);
                ParameterRows.Add(new TuningParameterRow(detector.Id, parameter, value, () => IsDirty = true));
            }
        }
    }

    private DetectorProfile BuildCandidate()
    {
        DetectorProfile candidate = _profiles!.Current;
        foreach (TuningParameterRow row in ParameterRows)
        {
            candidate = candidate.With(row.Detector, row.Name, row.Value);
        }

        return candidate;
    }
}
