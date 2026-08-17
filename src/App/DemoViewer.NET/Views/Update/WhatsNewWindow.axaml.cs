#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Update;

#endregion

namespace DemoViewer.NET.Views.Update;

/// <summary>
///     The post-update "What's new" window. Kicks the lazy release-notes fetch on open so
///     building the VM stays network-free for tests and the designer.
/// </summary>
public partial class WhatsNewWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public WhatsNewWindow() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is WhatsNewViewModel vm)
        {
            _ = vm.EnsureLoadedAsync();
        }
    }
}
