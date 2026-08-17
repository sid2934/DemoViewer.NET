#region

using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views;

/// <summary>Inspector card list view.</summary>
public partial class InspectorCardListView : UserControl
{
    /// <summary>Cards property.</summary>
    public static readonly StyledProperty<IEnumerable?> CardsProperty =
        AvaloniaProperty.Register<InspectorCardListView, IEnumerable?>(nameof(Cards));

    /// <summary>Collapse all command property.</summary>
    public static readonly StyledProperty<ICommand?> CollapseAllCommandProperty =
        AvaloniaProperty.Register<InspectorCardListView, ICommand?>(nameof(CollapseAllCommand));

    /// <summary>Expand all command property.</summary>
    public static readonly StyledProperty<ICommand?> ExpandAllCommandProperty =
        AvaloniaProperty.Register<InspectorCardListView, ICommand?>(nameof(ExpandAllCommand));

    /// <summary>Has cards property.</summary>
    public static readonly StyledProperty<bool> HasCardsProperty =
        AvaloniaProperty.Register<InspectorCardListView, bool>(nameof(HasCards));

    /// <summary>Header text property.</summary>
    public static readonly StyledProperty<string> HeaderTextProperty =
        AvaloniaProperty.Register<InspectorCardListView, string>(nameof(HeaderText), "");

    /// <summary>Placeholder text property.</summary>
    public static readonly StyledProperty<string> PlaceholderTextProperty =
        AvaloniaProperty.Register<InspectorCardListView, string>(
            nameof(PlaceholderText), "Select a frame to inspect its messages");

    /// <summary>Initializes a new <see cref="InspectorCardListView" /> instance.</summary>
    public InspectorCardListView() => InitializeComponent();

    /// <summary>Cards.</summary>

    public IEnumerable? Cards
    {
        get => GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
    }

    /// <summary>Collapse all command.</summary>

    public ICommand? CollapseAllCommand
    {
        get => GetValue(CollapseAllCommandProperty);
        set => SetValue(CollapseAllCommandProperty, value);
    }

    /// <summary>Expand all command.</summary>

    public ICommand? ExpandAllCommand
    {
        get => GetValue(ExpandAllCommandProperty);
        set => SetValue(ExpandAllCommandProperty, value);
    }

    /// <summary>Has cards.</summary>

    public bool HasCards
    {
        get => GetValue(HasCardsProperty);
        set => SetValue(HasCardsProperty, value);
    }

    /// <summary>Header text.</summary>

    public string HeaderText
    {
        get => GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    /// <summary>Placeholder text.</summary>

    public string PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }
}
