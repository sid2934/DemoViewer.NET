#region

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     A status chip: a dot and a neutral label bound to a <see cref="StatusChipViewModel" /> that opens a
///     <c>card-flyout</c> for detail and actions. The dot colour resolves from a palette token through a bound
///     state-to-class selector, so it re-themes live; the label is always the neutral <c>TextMid</c> token and
///     carries the state in words. Click or Enter runs <see cref="StatusChipViewModel.PrimaryAction" /> and opens the
///     flyout when there is <see cref="StatusChipViewModel.FlyoutContent" /> to show.
/// </summary>
public partial class StatusChip : UserControl
{
    private StatusChipViewModel? _viewModel;

    /// <summary>Initializes a new <see cref="StatusChip" /> instance.</summary>
    public StatusChip()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as StatusChipViewModel);
    }

    private void Attach(StatusChipViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        SyncFlyout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusChipViewModel.FlyoutContent))
        {
            SyncFlyout();
        }
    }

    // A chip with no flyout content gets no flyout, so a click runs its action without opening an empty card.
    private void SyncFlyout() =>
        Body.Flyout = _viewModel?.FlyoutContent is null ? null : (FlyoutBase)Resources["CardFlyout"]!;
}
