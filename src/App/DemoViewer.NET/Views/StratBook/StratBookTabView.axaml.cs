#region

using Avalonia.Controls;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.Views.StratBook;

/// <summary>The Strat Book tab view. Bindings, plus the step combo boxes' focus loss; behaviour lives on the VM.</summary>
public partial class StratBookTabView : UserControl
{
    /// <summary>Builds the view.</summary>
    public StratBookTabView()
    {
        InitializeComponent();

        // A step's combo box leaving focus ends its burst (StratEditorViewModel.EndEditBurst): the next change
        // there is a new undo entry.
        StepRows.AddHandler(LostFocusEvent, OnStepRowLostFocus, RoutingStrategies.Bubble);
    }

    private void OnStepRowLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ComboBox && DataContext is StratBookTabViewModel vm)
        {
            vm.Editor.EndEditBurst();
        }
    }
}
