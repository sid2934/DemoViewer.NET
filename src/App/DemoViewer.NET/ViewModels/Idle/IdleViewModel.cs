#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.ViewModels.DemoProcessing;

#endregion

namespace DemoViewer.NET.ViewModels.Idle;

/// <summary>
///     The idle-mode surface shown full-window when the app enters idle (see <see cref="Services.Idle.IdleController" />).
///     It explains why the app parked, offers Resume / Settings, shows what was captured to resume, and embeds
///     the live demo-processing queue (the SAME <see cref="ProcessingQueueStatusViewModel" /> the status strip
///     uses) so the user can watch background work and pause it from here.
/// </summary>
public sealed partial class IdleViewModel : ViewModelBase
{
    private readonly Action _openSettings;
    private readonly Action _resume;

    /// <summary>
    ///     The main explanation line. Set by the shell on entering idle so it reflects the exact configured
    ///     timeout (e.g. "…for 15 minutes.").
    /// </summary>
    [ObservableProperty]
    private string _messageText = "";

    /// <summary>
    ///     A short readout of the captured resume state (which demo was closed and where it resumes), or a
    ///     note that nothing was open. Set by the shell on entering idle.
    /// </summary>
    [ObservableProperty]
    private string _sessionStateText = "";

    /// <summary>Builds the surface. <paramref name="queue" /> is null on hosts without a demo-processing queue.</summary>
    public IdleViewModel(Action resume, Action openSettings, ProcessingQueueStatusViewModel? queue)
    {
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(openSettings);
        _resume = resume;
        _openSettings = openSettings;
        Queue = queue;
    }

    /// <summary>The live queue mapper (rows + pause), shared with the status strip. Null when no queue exists.</summary>
    public ProcessingQueueStatusViewModel? Queue { get; }

    /// <summary>Whether the embedded background-queue panel is shown (a queue was supplied).</summary>
    public bool HasQueue => Queue is not null;

    /// <summary>Fixed heading.</summary>
    public string HeaderText { get; } = "Idle Mode";

    /// <summary>Fixed sub-line pointing at the settings.</summary>
    public string SubText { get; } =
        "See the Idle Settings to configure this timeout and other aspects of the application's idle behavior.";

    /// <summary>Composes the primary message for a given configured timeout.</summary>
    public static string BuildMessage(TimeSpan timeout) =>
        string.Format(
            CultureInfo.CurrentCulture,
            "No user input or playback instructions were received for {0}. "
            + "Application entered idle mode to conserve system resources.",
            FormatDuration(timeout));

    // Human-readable duration ("15 minutes", "1 hour 30 minutes", "45 seconds") — avoids the raw "00:15:00".
    private static string FormatDuration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
        {
            return "the configured time";
        }

        List<string> parts = [];
        if (t.Hours > 0 || t.Days > 0)
        {
            int hours = (int)t.TotalHours;
            parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
        }

        if (t.Minutes > 0)
        {
            parts.Add(t.Minutes == 1 ? "1 minute" : $"{t.Minutes} minutes");
        }

        if (t.Seconds > 0 && t.TotalMinutes < 60)
        {
            parts.Add(t.Seconds == 1 ? "1 second" : $"{t.Seconds} seconds");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "the configured time";
    }

    [RelayCommand]
    private void Resume() => _resume();

    [RelayCommand]
    private void OpenSettings() => _openSettings();
}
