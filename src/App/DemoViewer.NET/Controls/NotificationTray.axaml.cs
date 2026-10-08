#region

using System.Collections;
using Avalonia;
using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>The stack of extension notifications drawn above the status strip.</summary>
public partial class NotificationTray : UserControl
{
    /// <summary>The cards to show, newest first.</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<NotificationTray, IEnumerable?>(nameof(Items));

    /// <summary>Initializes a new <see cref="NotificationTray" />.</summary>
    public NotificationTray() => InitializeComponent();

    /// <summary>The cards to show, newest first.</summary>
    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }
}
