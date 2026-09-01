#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.ViewModels.Common;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     VS Code-style docked Output panel. Aggregates parser/tracker
///     diagnostics into channels: Unknown messages, Decode errors, Tracker errors, Build/test.
///     Persistent across tabs; clicking a row seeks to the offending frame.
/// </summary>
public sealed partial class OutputPanelViewModel : ObservableObject
{
    // Kept for lazily-added channels (GetOrAddChannel): the fixed four capture it at ctor time.
    private readonly FrameNavigationViewModel _navigation;

    [ObservableProperty]
    private OutputChannelViewModel _active;

    /// <summary>Toolbar toggle: show/hide the bottom Output drawer.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Initializes a new <see cref="OutputPanelViewModel" /> instance.</summary>
    public OutputPanelViewModel(FrameNavigationViewModel navigation)
    {
        _navigation = navigation;
        // Severity KINDS, not brushes (v0.6.0 code-color promotion): the view maps them onto
        // AccentAmber / AccentError / AccentInteractive / AccentInfo theme tokens via sev-* classes.
        Channels =
        [
            new OutputChannelViewModel("Unknown messages", navigation, OutputSeverity.Warn),
            new OutputChannelViewModel("Decode errors", navigation, OutputSeverity.Error),
            new OutputChannelViewModel("Tracker errors", navigation, OutputSeverity.Error),
            new OutputChannelViewModel("Build/test", navigation, OutputSeverity.Info)
        ];

        foreach (OutputChannelViewModel ch in Channels)
        {
            ch.SelectRequested += SelectChannel;
        }

        _active = Channels[0];
        _active.IsActive = true;
    }

    /// <summary>Build test.</summary>
    public OutputChannelViewModel BuildTest => Channels[3];

    /// <summary>Channels.</summary>
    public ObservableCollection<OutputChannelViewModel> Channels { get; }

    /// <summary>Decode errors.</summary>
    public OutputChannelViewModel DecodeErrors => Channels[1];

    /// <summary>Tracker errors.</summary>
    public OutputChannelViewModel TrackerErrors => Channels[2];

    // Stable channel accessors so call sites don't depend on index magic.
    /// <summary>Unknown messages.</summary>
    public OutputChannelViewModel UnknownMessages => Channels[0];

    /// <summary>Clears every channel's rows (called on file load before re-wiring sources).</summary>
    public void ClearAll()
    {
        foreach (OutputChannelViewModel ch in Channels)
        {
            ch.Clear();
        }
    }

    /// <summary>
    ///     Clears the rows of the channel titled <paramref name="title" /> if it exists; a no-op
    ///     otherwise (never creates an empty channel). UI thread only. Used to drop a feature-owned
    ///     channel's accumulated rows on teardown (e.g. the live-sync log on Disable).
    /// </summary>
    public void ClearChannel(string title)
    {
        foreach (OutputChannelViewModel ch in Channels)
        {
            if (string.Equals(ch.Title, title, StringComparison.Ordinal))
            {
                ch.Clear();
                return;
            }
        }
    }

    /// <summary>
    ///     Returns the channel with <paramref name="title" />, creating and wiring it if absent,
    ///     for feature-owned channels added lazily on first use (e.g. the live-sync CSVG log
    ///     bridge) rather than eagerly for every user. UI thread only (mutates
    ///     <see cref="Channels" />).
    /// </summary>
    public OutputChannelViewModel GetOrAddChannel(string title, OutputSeverity severity)
    {
        foreach (OutputChannelViewModel ch in Channels)
        {
            if (string.Equals(ch.Title, title, StringComparison.Ordinal))
            {
                return ch;
            }
        }

        OutputChannelViewModel added = new(title, _navigation, severity);
        added.SelectRequested += SelectChannel;
        Channels.Add(added);
        return added;
    }

    private void SelectChannel(OutputChannelViewModel channel)
    {
        foreach (OutputChannelViewModel ch in Channels)
        {
            ch.IsActive = ReferenceEquals(ch, channel);
        }

        Active = channel;
    }
}

/// <summary>Channel accent kind: mapped to theme tokens by the view's sev-* class styles.</summary>
public enum OutputSeverity
{
    /// <summary>Informational (AccentInteractive underline).</summary>
    Info,

    /// <summary>Warning-flavored channel (AccentAmber).</summary>
    Warn,

    /// <summary>Error-flavored channel (AccentError).</summary>
    Error,

    /// <summary>Live-feed channel: the CSVG log bridge's teal (AccentInfo).</summary>
    Live
}

/// <summary>One output channel (tab + its row buffer).</summary>
public sealed partial class OutputChannelViewModel(string title, FrameNavigationViewModel nav, OutputSeverity severity) : ObservableObject
{
    // Ring-buffer cap: generous enough that ordinary diagnostic channels never reach it, but a
    // runaway producer (e.g. the app-lifetime CSVG "Live Sync" log bridge) stays bounded instead
    // of growing for the whole session. Append is UI-thread-only, so the drop needs no locking.
    private const int MaxRows = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private int _count;

    /// <summary>Active-tab flag: flips the view's .active class (underline + title tint).</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private OutputRow? _selected;

    /// <summary>Has count.</summary>
    public bool HasCount => Count > 0;

    /// <summary>Rows.</summary>
    public ObservableCollection<OutputRow> Rows { get; } = [];

    /// <summary>Title.</summary>
    public string Title { get; } = title;

    // Severity-class selectors (v0.6.0): exactly one true; the view maps them to theme tokens,
    // replacing the code-held Accent/TabBrush/TitleBrush trio that stayed dark-tuned under Light.

    /// <summary>Error accent channel.</summary>
    public bool IsSevError => severity == OutputSeverity.Error;

    /// <summary>Warning accent channel.</summary>
    public bool IsSevWarn => severity == OutputSeverity.Warn;

    /// <summary>Info accent channel.</summary>
    public bool IsSevInfo => severity == OutputSeverity.Info;

    /// <summary>Live-feed accent channel.</summary>
    public bool IsSevLive => severity == OutputSeverity.Live;

    /// <summary>Appends a row and bumps the count badge. Safe to call from the UI thread.</summary>
    public void Append(OutputRow row)
    {
        if (Rows.Count >= MaxRows)
        {
            Rows.RemoveAt(0);
        }

        Rows.Add(row);
        Count = Rows.Count;
    }

    /// <summary>Clear.</summary>
    public void Clear()
    {
        Selected = null;
        Rows.Clear();
        Count = 0;
    }

    /// <summary>Raised when this tab is clicked so the panel can flip the active channel.</summary>
    public event Action<OutputChannelViewModel>? SelectRequested;

    partial void OnSelectedChanged(OutputRow? value)
    {
        if (value is { SeekFrameIndex: >= 0 })
        {
            nav.SeekToFrame(value.SeekFrameIndex);
        }
    }

    [RelayCommand]
    private void Select() => SelectRequested?.Invoke(this);
}

/// <summary>
///     One diagnostic row. <see cref="TickLabel" /> is the displayed left column;
///     <see cref="SeekFrameIndex" /> is the frame the row seeks to on click (-1 = not seekable).
/// </summary>
/// <remarks>Initializes a new <see cref="OutputRow" /> instance.</remarks>
public sealed class OutputRow(int seekFrameIndex, string tickLabel, string level, string message)
{
    /// <summary>Level.</summary>
    public string Level { get; } = level;

    // Level-class selectors (v0.6.0): the view maps lvl-err/lvl-warn (default = info) onto theme
    // tokens; also kills the brush-per-property-get allocation the old LevelBrush paid.

    /// <summary>ERR row.</summary>
    public bool IsErr => Level == "ERR";

    /// <summary>WARN row.</summary>
    public bool IsWarn => Level == "WARN";

    /// <summary>Message.</summary>
    public string Message { get; } = message;

    /// <summary>Seek frame index.</summary>
    public int SeekFrameIndex { get; } = seekFrameIndex;

    // Bound by the panel template (Grid column 0).
    /// <summary>Tick.</summary>
    public string Tick => TickLabel;

    /// <summary>Tick label.</summary>
    public string TickLabel { get; } = tickLabel;
}
