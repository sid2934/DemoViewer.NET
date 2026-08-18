#region

using System.ComponentModel;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>A named category of entities shown in the entity tree panel.</summary>
public class EntityGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    /// <summary>Delta count.</summary>
    public int DeltaCount { get; init; }

    /// <summary>Entities.</summary>
    public List<EntityState> Entities { get; init; } = [];

    /// <summary>Has delta.</summary>
    public bool HasDelta => DeltaCount > 0;

    /// <summary>Header.</summary>
    public string Header => DeltaCount > 0
        ? $"{Name}  ({Entities.Count}  Δ{DeltaCount})"
        : $"{Name}  ({Entities.Count})";

    /// <summary>Is expanded.</summary>

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    /// <summary>Name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Property changed.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
