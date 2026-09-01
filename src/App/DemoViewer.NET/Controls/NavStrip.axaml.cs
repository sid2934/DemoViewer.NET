#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Code-behind for the shell <see cref="NavStrip" /> (navigation-review Phase C). The strip binds
///     to the shell <see cref="MainViewModel" /> (its natural DataContext as shell chrome) for the
///     clock (<c>Playback.*</c>), the semantic <c>Nav*Command</c>s, the demo-derived event filter
///     (<c>EventFilterFlyout</c>), and the unchanged breakpoint commands. The only imperative wiring is
///     the editable frame box commit on Enter / LostFocus: frame-index movement, the locked decision.
/// </summary>
public partial class NavStrip : UserControl
{
    /// <summary>Initializes a new <see cref="NavStrip" /> instance.</summary>
    public NavStrip()
    {
        InitializeComponent();
        FrameInput.KeyDown += OnFrameInputKeyDown;
        FrameInput.LostFocus += OnFrameInputLostFocus;
    }

    private void OnFrameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            vm.CommitNavFrameText();
            e.Handled = true;
        }
    }

    private void OnFrameInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CommitNavFrameText();
        }
    }
}
