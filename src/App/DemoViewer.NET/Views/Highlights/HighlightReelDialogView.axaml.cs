#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Highlights;

#endregion

namespace DemoViewer.NET.Views.Highlights;

/// <summary>
///     The reel configuration pane (the design notes in git history; promoted out of the modal by the
///     Reels-dashboard redesign and now embedded as the Reels tab's right-hand splitter column). The only
///     code-behind is the storage-provider handoff for the Browse folder picker, which needs the visual tree
///     (TopLevel) and so cannot live in the VM — mirrors <c>SettingsView</c>.
/// </summary>
public partial class HighlightReelDialogView : UserControl
{
    /// <summary>Initializes a new <see cref="HighlightReelDialogView" /> instance.</summary>
    public HighlightReelDialogView()
    {
        InitializeComponent();
        // BOTH hooks, because the two hosts deliver the DataContext at different times. The modal set it
        // before showing the window, so attach-time was always enough. Embedded in the tab it arrives through
        // `DataContext="{Binding ReelConfig}"`, which can resolve AFTER attach — and a missed handoff makes
        // Browse a silently dead button on what is now the primary reel flow. Both paths are idempotent;
        // TopLevel.GetTopLevel works from any depth, so a splitter column is fine.
        AttachedToVisualTree += (_, _) => TryAttachStorageProvider();
        DataContextChanged += (_, _) => TryAttachStorageProvider();
    }

    private void TryAttachStorageProvider()
    {
        if (DataContext is HighlightReelDialogViewModel vm && TopLevel.GetTopLevel(this) is { } top)
        {
            vm.SetStorageProvider(top.StorageProvider);
        }
    }
}
