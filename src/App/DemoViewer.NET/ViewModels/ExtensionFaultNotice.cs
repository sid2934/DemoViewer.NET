#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     What the user sees when an extension was switched off for the session after errors: one line saying
///     so, and the three ways out. Settings shows it on the extension's rows; the shell shows it once as a
///     banner.
/// </summary>
public sealed partial class ExtensionFaultNotice : ObservableObject
{
    private readonly Action<string> _keepOff;
    private readonly Action? _openLog;
    private readonly Action<string> _turnOnAgain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sentence))]
    private string _text = "";

    [ObservableProperty]
    private bool _isShown;

    [ObservableProperty]
    private bool _canTurnOnAgain;

    /// <param name="featureId">The extension's master switch.</param>
    /// <param name="name">Its display name.</param>
    /// <param name="turnOnAgain">Lifts the suspension.</param>
    /// <param name="keepOff">Turns the master switch off in settings.</param>
    /// <param name="openLog">Opens the diagnostics log, or null where there is none.</param>
    public ExtensionFaultNotice(string featureId, string name, Action<string> turnOnAgain, Action<string> keepOff, Action? openLog)
    {
        ArgumentException.ThrowIfNullOrEmpty(featureId);
        ArgumentNullException.ThrowIfNull(turnOnAgain);
        ArgumentNullException.ThrowIfNull(keepOff);
        FeatureId = featureId;
        Name = name;
        _turnOnAgain = turnOnAgain;
        _keepOff = keepOff;
        _openLog = openLog;
    }

    /// <summary>The extension's master switch.</summary>
    public string FeatureId { get; }

    /// <summary>The extension's display name.</summary>
    public string Name { get; }

    /// <summary>The line with the extension's name in front, for the banner.</summary>
    public string Sentence => Name + ": " + Text;

    /// <summary>True when there is a log to open.</summary>
    public bool CanOpenLog => _openLog is not null;

    /// <summary>Shows or hides the notice from the extension's fault record.</summary>
    public void Apply(ExtensionFaultState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IsShown = state.Suspended;
        CanTurnOnAgain = state.Suspended && !state.StartupFailed;
        Text = !state.Suspended
            ? ""
            : state.StartupFailed
                ? $"Did not start this session after an error (in {state.LastSite}). It tries again the next time the app starts."
                : $"Turned off for this session after {state.Count} errors (last: {state.LastSite}).";
    }

    [RelayCommand]
    private void TurnOnAgain() => _turnOnAgain(FeatureId);

    [RelayCommand]
    private void KeepOff() => _keepOff(FeatureId);

    [RelayCommand]
    private void OpenLog() => _openLog?.Invoke();
}
