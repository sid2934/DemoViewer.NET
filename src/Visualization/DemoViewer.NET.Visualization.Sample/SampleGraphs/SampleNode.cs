#region

using System.ComponentModel;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization.Sample.SampleGraphs;

/// <summary>Sample node.</summary>
public class SampleNode : IGraphNode, INotifyPropertyChanged
{
    private string? _displayValue;

    private bool _isActive;

    /// <summary>Initializes a sample node with the given visual properties.</summary>
    public SampleNode(string name, bool isRoot = false, bool isActive = false,
        string? displayValue = null, string? subtitle = null, NodeStyle? style = null)
    {
        Name = name;
        IsRoot = isRoot;
        IsActive = isActive;
        DisplayValue = displayValue;
        Subtitle = subtitle;
        Style = style;
    }

    /// <summary>The node's current display value; setter raises <see cref="PropertyChanged" />.</summary>
    public string? DisplayValue
    {
        get => _displayValue;
        set
        {
            _displayValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
        }
    }

    /// <summary>Whether the node is currently active; setter raises <see cref="PropertyChanged" />.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>Is root.</summary>
    public bool IsRoot { get; }

    /// <summary>Name.</summary>
    public string Name { get; }

    /// <summary>Style.</summary>
    public NodeStyle? Style { get; init; }

    /// <summary>Subtitle.</summary>
    public string? Subtitle { get; }

    /// <summary>Property changed.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Conjunction-style edge with dashed line and distinct color.</summary>
public record ConjunctionSampleEdge(
    IGraphNode Source,
    IGraphNode Destination,
    string Label,
    string? ConditionLabel = null) : IGraphEdge
{
    /// <summary>Effect.</summary>
    public VisualEdgeEffect Effect => VisualEdgeEffect.Conjunction;

    /// <summary>Style.</summary>
    public EdgeStyle? Style => new()
    {
        Color = Color.Parse("#7986CB"),
        IsDashed = true
    };
}
