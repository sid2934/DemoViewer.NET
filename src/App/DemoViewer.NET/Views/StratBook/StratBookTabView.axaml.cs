#region

using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DemoViewer.NET.Services.Strats;
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

    // Filled here as well as on opening: a menu flyout with no items does not open.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is StratBookTabViewModel vm)
        {
            FillTemplateMenu(NewStratButton.Flyout, StratTemplates.Templates, vm.NewStratCommand, true);
            FillTemplateMenu(ApplyTemplateButton.Flyout, StratTemplates.Templates, vm.ApplyTemplateCommand, false);
        }
    }

    private void OnApplyTemplateMenuOpening(object? sender, EventArgs e)
    {
        if (DataContext is StratBookTabViewModel vm)
        {
            FillTemplateMenu(ApplyTemplateButton.Flyout, vm.ApplicableTemplates, vm.ApplyTemplateCommand, false);
        }
    }

    /// <summary>One entry per template group; a group with site variants opens a submenu of them.</summary>
    internal static void FillTemplateMenu(FlyoutBase? flyout, IEnumerable<StratTemplate> templates, ICommand command, bool blank)
    {
        if (flyout is not MenuFlyout menu)
        {
            return;
        }

        menu.Items.Clear();
        if (blank)
        {
            menu.Items.Add(new MenuItem { Header = "Blank", Command = command });
            menu.Items.Add(new Separator());
        }

        foreach (IGrouping<string, StratTemplate> group in templates.GroupBy(t => t.Group))
        {
            StratTemplate first = group.First();
            string header = first.Side is null ? group.Key : group.Key + " (" + first.Side + ")";
            if (group.Count() == 1 && string.Equals(first.Label, first.Group, StringComparison.Ordinal))
            {
                menu.Items.Add(new MenuItem { Header = header, Command = command, CommandParameter = first.Id });
                continue;
            }

            MenuItem parent = new() { Header = header };
            foreach (StratTemplate template in group)
            {
                parent.Items.Add(new MenuItem { Header = template.Label, Command = command, CommandParameter = template.Id });
            }

            menu.Items.Add(parent);
        }
    }
}
