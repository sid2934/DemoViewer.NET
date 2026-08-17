#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Setup;

#endregion

namespace DemoViewer.NET.Views.Setup;

// The first-run wizard body, hosted by BOTH the desktop FirstRunWizardWindow and the WASM in-app overlay
// (the same view renders in both). The only code-behind is the storage-provider handoff for the folder
// picker, which needs the visual tree (TopLevel) and so cannot live in the view-model — mirrors SettingsView.
/// <summary>First-run wizard view.</summary>
public partial class FirstRunWizardView : UserControl
{
    /// <summary>Initializes a new <see cref="FirstRunWizardView" /> instance.</summary>
    public FirstRunWizardView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is not FirstRunWizardViewModel vm)
        {
            return;
        }

        vm.SetStorageProvider(TopLevel.GetTopLevel(this)?.StorageProvider);
    }
}
