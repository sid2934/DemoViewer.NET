#region

using Avalonia.Media;
using Cs2DemoKit.Parser.Models;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Sub tick event view model.</summary>
public sealed class SubTickEventViewModel(SubTickEvent e)
{
    /// <summary>Accent brush.</summary>
    public IBrush AccentBrush => e.EventType switch
    {
        "Attack" => new SolidColorBrush(Color.Parse("#C0F44336")), // red
        "Attack2" => new SolidColorBrush(Color.Parse("#C0FF9800")), // orange
        "Jump" => new SolidColorBrush(Color.Parse("#C000BCD4")), // cyan
        "Duck" => new SolidColorBrush(Color.Parse("#C0009688")), // teal
        "Reload" => new SolidColorBrush(Color.Parse("#C09C27B0")), // purple
        "Use" => new SolidColorBrush(Color.Parse("#C0FFC107")), // amber
        "Move" => new SolidColorBrush(Color.Parse("#C02196F3")), // blue
        _ => new SolidColorBrush(Color.Parse("#C0888888")) // grey
    };

    /// <summary>Description.</summary>
    public string Description => e.Description;

    /// <summary>Event type.</summary>
    public string EventType => e.EventType;

    /// <summary>Player text.</summary>
    public string PlayerText => $"P{e.PlayerSlot}";

    /// <summary>When text.</summary>
    public string WhenText => $"{e.When:F3}";
}
