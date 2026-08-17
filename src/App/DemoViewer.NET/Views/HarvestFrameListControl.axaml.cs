#region

using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

#endregion

namespace DemoViewer.NET.Views;

/// <summary>Harvest frame list control.</summary>
public partial class HarvestFrameListControl : UserControl
{
    /// <summary>Frames property.</summary>
    public static readonly StyledProperty<IEnumerable?> FramesProperty =
        AvaloniaProperty.Register<HarvestFrameListControl, IEnumerable?>(nameof(Frames));

    /// <summary>Selected item property.</summary>
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<HarvestFrameListControl, object?>(
            nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Initializes a new <see cref="HarvestFrameListControl" /> instance.</summary>
    public HarvestFrameListControl() => InitializeComponent();

    /// <summary>Frames.</summary>

    public IEnumerable? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    /// <summary>Selected item.</summary>

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }
}
