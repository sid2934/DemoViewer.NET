#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Update;

#endregion

namespace DemoViewer.NET.Views.Update;

/// <summary>
///     The update-notice pop-up. Kicks the lazy release-notes fetch on open (not in the VM
///     constructor) so building the VM stays network-free for tests and the designer.
/// </summary>
public partial class UpdateNoticeWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public UpdateNoticeWindow() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is UpdateNoticeViewModel vm)
        {
            _ = vm.EnsureNotesLoadedAsync();
        }
    }
}
