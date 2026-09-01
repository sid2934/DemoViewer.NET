#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Debugging;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     UI-side wrapper around <see cref="DebuggerService" />. Surfaces the breakpoint list,
///     the "currently broken at" indicator, and add/remove commands. The actual halt logic
///     lives in <see cref="Shell.MainViewModel" />, which holds the service.
/// </summary>
public partial class DebuggerViewModel : ObservableObject
{
    private readonly DebuggerService _service;

    /// <summary>The breakpoint we're currently stopped at, or null when running.</summary>
    [ObservableProperty]
    private Breakpoint? _hitBreakpoint;

    /// <summary>Frame index captured at the moment the hit fired; -1 when none.</summary>
    [ObservableProperty]
    private int _hitFrameIndex = -1;

    [ObservableProperty]
    private string _newIntValueText = "";

    // ── Add-breakpoint form state (bound from the panel) ──────────────────────

    [ObservableProperty]
    private BreakpointKind _newKind = BreakpointKind.FrameNumber;

    [ObservableProperty]
    private string _newStringValue = "";

    [ObservableProperty]
    private int _trackerDeltaUnknownCount;

    [ObservableProperty]
    private string _trackerErrorText = "";

    /// <summary>
    ///     Live Tier-3 counters from the active EntityTracker. Updated by MainViewModel
    ///     after each seek via <see cref="UpdateTrackerStats" />. Surfaces the same data
    ///     the CLI probe prints, so the user can spot bench-vs-Furia differences (Furia
    ///     deltaUnknown = 0, bench MM demos ≈ 87k) without leaving the app.
    /// </summary>
    [ObservableProperty]
    private int _trackerPacketCount;

    /// <summary>Initializes a new <see cref="DebuggerViewModel" /> instance.</summary>
    public DebuggerViewModel(DebuggerService service)
    {
        _service = service;

        // Keep our notifyable properties in sync with the service's events.
        _service.StateChanged += OnStateChanged;
        OnStateChanged();

        // For the "Add quick…" template box (kind picker). Wire up later from XAML.
        BreakpointKinds = Enum.GetValues<BreakpointKind>();
    }

    /// <summary>Breakpoint kinds.</summary>
    public BreakpointKind[] BreakpointKinds { get; }

    /// <summary>Breakpoints.</summary>
    public ObservableCollection<Breakpoint> Breakpoints => _service.Breakpoints;

    /// <summary>True when the captured hit frame is valid: drives the "Jump to" button visibility.</summary>
    public bool HasHitFrame => HitFrameIndex >= 0;

    /// <summary>Has tracker error.</summary>
    public bool HasTrackerError => !string.IsNullOrEmpty(TrackerErrorText);

    /// <summary>"(at Frame #N)" hint shown next to the status when the hit has a captured frame.</summary>
    public string HitFrameText => HitFrameIndex >= 0 ? $"at Frame #{HitFrameIndex}" : "";

    /// <summary>True while halted (any hit breakpoint). Used by the UI to highlight Continue.</summary>
    public bool IsBroken => HitBreakpoint is not null;

    /// <summary>
    ///     True when the currently-selected <see cref="NewKind" /> uses the int field
    ///     (FrameNumber, TickNumber, PacketIndex). UI shows/hides input boxes via this.
    /// </summary>
    public bool NewKindUsesInt => NewKind is BreakpointKind.FrameNumber
        or BreakpointKind.TickNumber
        or BreakpointKind.PacketIndex;

    /// <summary>True when NewKind uses the string field (GameEventName).</summary>
    public bool NewKindUsesString => NewKind is BreakpointKind.GameEventName;

    /// <summary>Text shown in the panel header. "Running" / "Stopped at: …".</summary>
    public string StatusText => HitBreakpoint is { } bp
        ? $"Stopped at {bp.DisplayText}  (hit #{bp.HitCount})"
        : "Running";

    /// <summary>Called from MainViewModel after a seek completes.</summary>
    public void UpdateTrackerStats(int packetCount, int deltaUnknownCount, string? errorText)
    {
        TrackerPacketCount = packetCount;
        TrackerDeltaUnknownCount = deltaUnknownCount;
        TrackerErrorText = errorText ?? "";
    }

    [RelayCommand]
    private void AddBreakpoint()
    {
        int intVal = 0;
        if (NewKindUsesInt)
        {
            int.TryParse(NewIntValueText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out intVal);
        }

        string? stringVal = NewKindUsesString ? NewStringValue : null;
        if (NewKindUsesString && string.IsNullOrWhiteSpace(stringVal))
        {
            return;
        }

        _service.Add(NewKind, intVal, stringVal);
    }

    [RelayCommand]
    private void ClearAll() => _service.Clear();

    [RelayCommand]
    private void Continue() => _service.Continue();

    partial void OnHitFrameIndexChanged(int value)
        => OnPropertyChanged(nameof(HasHitFrame));

    partial void OnNewKindChanged(BreakpointKind value)
    {
        OnPropertyChanged(nameof(NewKindUsesInt));
        OnPropertyChanged(nameof(NewKindUsesString));
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void OnStateChanged()
    {
        HitBreakpoint = _service.LastHit;
        HitFrameIndex = _service.LastHitFrameIndex;
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HitFrameText));
    }

    partial void OnTrackerErrorTextChanged(string value)
        => OnPropertyChanged(nameof(HasTrackerError));

    [RelayCommand]
    private void RemoveBreakpoint(Breakpoint? bp)
    {
        if (bp is null)
        {
            return;
        }

        _service.Remove(bp.Id);
    }

    [RelayCommand]
    private void ToggleEnabled(Breakpoint? bp)
    {
        if (bp is null)
        {
            return;
        }

        bp.Enabled = !bp.Enabled;
        // Force list refresh so the UI re-reads Enabled.
        int idx = Breakpoints.IndexOf(bp);
        if (idx >= 0)
        {
            Breakpoints.RemoveAt(idx);
            Breakpoints.Insert(idx, bp);
        }
    }
}
