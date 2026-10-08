#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels.MatchOverview;

/// <summary>
///     One extension's <see cref="DemoAction" /> on the page. After a press it stays hidden for that demo
///     until the page moves to another one, so a second press does not read as "nothing happened".
/// </summary>
public sealed partial class DemoActionRow : ObservableObject
{
    private readonly string _featureId;
    private readonly Func<string, bool> _isFeatureOn;
    private string? _requestedFor;
    private string? _subject;

    internal DemoActionRow(DemoAction action, string featureId, Func<string, bool> isFeatureOn)
    {
        Action = action;
        _featureId = featureId;
        _isFeatureOn = isFeatureOn;
    }

    /// <summary>The contributed action.</summary>
    public DemoAction Action { get; }

    /// <summary>The button text.</summary>
    public string Label => Action.Label;

    /// <summary>The button tooltip.</summary>
    public string Tooltip => Action.Tooltip;

    /// <summary>True while the button belongs on the page.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Re-asks the action for <paramref name="subject" />, the page's demo path or null.</summary>
    internal void Refresh(string? subject)
    {
        if (!string.Equals(_subject, subject, StringComparison.OrdinalIgnoreCase))
        {
            _subject = subject;
            _requestedFor = null;
        }

        IsVisible = subject is not null
                    && _isFeatureOn(_featureId)
                    && !string.Equals(_requestedFor, subject, StringComparison.OrdinalIgnoreCase)
                    && Action.IsAvailable(subject);
    }

    [RelayCommand]
    private void Run()
    {
        if (_subject is not { } subject)
        {
            return;
        }

        Refresh(subject);
        if (!IsVisible)
        {
            return;
        }

        Action.Run(subject);
        _requestedFor = subject;
        IsVisible = false;
    }
}
