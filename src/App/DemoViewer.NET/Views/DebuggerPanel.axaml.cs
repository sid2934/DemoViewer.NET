#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.Views;

/// <summary>Debugger panel.</summary>
public partial class DebuggerPanel : UserControl
{
    /// <summary>Initializes a new <see cref="DebuggerPanel" /> instance.</summary>
    public DebuggerPanel() => InitializeComponent();

    /// <summary>
    ///     "Jump to" button — finds the MainViewModel via the visual tree and asks it to
    ///     navigate the frame selection to <c>Debugger.LastHitFrameIndex</c>. We do this via
    ///     code-behind rather than XAML ancestor binding because the panel's DataContext is
    ///     the <see cref="DemoViewer.NET.ViewModels.DebuggerViewModel" />, not the MainViewModel — and the command lives
    ///     on MainViewModel so it can mutate <c>SelectedFrame</c> directly.
    /// </summary>
    private void OnJumpToHitFrameClick(object? sender, RoutedEventArgs e)
    {
        // Walk up the visual tree to the nearest ancestor whose DataContext is a MainViewModel.
        foreach (Visual anc in this.GetVisualAncestors())
        {
            if (anc is Control { DataContext: MainViewModel mvm })
            {
                mvm.JumpToHitFrameCommand.Execute(null);
                return;
            }
        }
    }
}
