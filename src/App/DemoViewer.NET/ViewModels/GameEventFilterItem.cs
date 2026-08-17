#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Game event filter item.</summary>
public sealed partial class GameEventFilterItem(string eventName) : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Event name.</summary>
    public string EventName { get; } = eventName;
}
