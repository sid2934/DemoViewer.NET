#region

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>Watched value.</summary>
public partial class WatchedValue : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _currentValue = "";

    /// <summary>Display text.</summary>
    public string DisplayText => $"{Label}: {CurrentValue}";

    /// <summary>Entity class name.</summary>
    public string EntityClassName { get; init; } = "";

    /// <summary>Entity serial.</summary>
    public int EntitySerial { get; init; }

    /// <summary>Field key.</summary>
    public string FieldKey { get; init; } = "";

    /// <summary>Label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Remove command.</summary>
    public ICommand? RemoveCommand { get; init; }
}
