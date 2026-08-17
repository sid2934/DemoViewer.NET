#region

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.ViewModels.Commands;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Command palette overlay (Ctrl+P). DataContext is a
///     <see cref="CommandPaletteViewModel" />. Focuses the query box when the popup opens so the
///     user can type immediately.
/// </summary>
public partial class CommandPalette : UserControl
{
    private CommandPaletteViewModel? _vm;

    /// <summary>Initializes a new <see cref="CommandPalette" /> instance.</summary>
    public CommandPalette()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as CommandPaletteViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _vm is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Input);
        }
    }
}
