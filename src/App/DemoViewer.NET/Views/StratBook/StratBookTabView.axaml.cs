#region

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.Views.StratBook;

/// <summary>The Strat Book tab view. Bindings, plus the step combo boxes' focus loss; behaviour lives on the VM.</summary>
public partial class StratBookTabView : UserControl
{
    private StratBookTabViewModel? _bound;

    /// <summary>Builds the view.</summary>
    public StratBookTabView()
    {
        InitializeComponent();

        // A step's combo box leaving focus ends its burst (StratEditorViewModel.EndEditBurst): the next change
        // there is a new undo entry.
        StepRows.AddHandler(LostFocusEvent, OnStepRowLostFocus, RoutingStrategies.Bubble);

        // A press or focus anywhere in a row selects its step. Never handled, so the field still gets it.
        StepRows.AddHandler(GotFocusEvent, OnStepRowActivated, RoutingStrategies.Bubble);
        StepRows.AddHandler(PointerPressedEvent, OnStepRowActivated, RoutingStrategies.Tunnel, true);
    }

    private void OnStepRowActivated(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: StratStepRow row } && DataContext is StratBookTabViewModel vm)
        {
            vm.StepSelection.Select(row.Id);
        }
    }

    private void OnStepRowLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ComboBox && DataContext is StratBookTabViewModel vm)
        {
            vm.Editor.EndEditBurst();
        }
    }

    // Posted: the rows may be rebuilt by the same change and are laid out after it.
    private void OnSelectionChanged(Guid? stepId)
    {
        if (stepId is not { } id || DataContext is not StratBookTabViewModel vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            int index = vm.Editor.Steps.ToList().FindIndex(r => r.Id == id);
            if (index >= 0 && StepRows.ContainerFromIndex(index) is { } row)
            {
                row.BringIntoView();
            }
        }, DispatcherPriority.Background);
    }

    // Filled here as well as on opening: a menu flyout with no items does not open.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_bound is not null)
        {
            _bound.StepSelection.Changed -= OnSelectionChanged;
        }

        _bound = DataContext as StratBookTabViewModel;
        if (_bound is not null)
        {
            _bound.StepSelection.Changed += OnSelectionChanged;
            OnSelectionChanged(_bound.StepSelection.SelectedStepId);
        }

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
