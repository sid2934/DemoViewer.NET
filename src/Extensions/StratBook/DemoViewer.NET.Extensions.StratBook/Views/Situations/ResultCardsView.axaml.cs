#region

using Avalonia.Controls;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.Situations;

/// <summary>The Result Cards view. Bindings only, apart from handing the VM the width in cards.</summary>
public partial class ResultCardsView : UserControl
{
    // The slot width the cards had in the WrapPanel.
    private const double CardSlotWidth = 192;

    /// <summary>Builds the view.</summary>
    public ResultCardsView() => InitializeComponent();

    private void OnCardsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is ResultCardsViewModel vm && e.NewSize.Width > 0)
        {
            vm.SetCardColumns((int)(e.NewSize.Width / CardSlotWidth));
        }
    }
}
