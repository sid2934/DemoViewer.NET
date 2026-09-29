#region

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.Views.StratBook;

/// <summary>
///     The Strat Book tab view. Bindings, plus the step rows' focus and keys; behaviour lives on the VM.
///     <para>
///         <b>Step row keys.</b> Enter in a row's single-line field, or on the row, adds a step after it; Ctrl+D
///         duplicates it; Delete removes it only while the row itself has focus, never from inside a field; Escape
///         leaves a field for its row; Alt+Up and Alt+Down move it. A field's pending text is committed first:
///         the fields write on focus loss, and the rows are rebuilt when the list changes.
///     </para>
/// </summary>
public partial class StratBookTabView : UserControl
{
    private StratEditorViewModel? _editor;

    // The row focus was last in, for the Add step button; cleared when focus goes anywhere but that button.
    private Guid? _lastRow;

    /// <summary>Builds the view.</summary>
    public StratBookTabView()
    {
        InitializeComponent();

        // A step's combo box leaving focus ends its burst (StratEditorViewModel.EndEditBurst): the next change
        // there is a new undo entry.
        StepRows.AddHandler(LostFocusEvent, OnStepRowLostFocus, RoutingStrategies.Bubble);
        StepRows.AddHandler(KeyDownEvent, OnStepRowKeyDown, RoutingStrategies.Tunnel);
        StepRows.ContainerPrepared += OnStepRowPrepared;
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, true);
    }

    private void OnStepRowLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ComboBox && DataContext is StratBookTabViewModel vm)
        {
            vm.Editor.EndEditBurst();
        }
    }

    // A row takes focus (a click on its background, Escape from a field) but is not a Tab stop.
    private static void OnStepRowPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        e.Container.Focusable = true;
        e.Container.IsTabStop = false;
        e.Container.Classes.Add("stepRow");
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (RowOf(e.Source) is { } hit)
        {
            _lastRow = hit.Row.Id;
        }
        else if (!ReferenceEquals(e.Source, AddStepButton))
        {
            _lastRow = null;
        }
    }

    private void OnAddStepClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StratBookTabViewModel vm)
        {
            vm.Editor.AddStepCommand.Execute(_lastRow is { } id ? vm.Editor.Steps.FirstOrDefault(r => r.Id == id) : null);
        }
    }

    private void OnStepRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not StratBookTabViewModel vm || RowOf(e.Source) is not { } hit)
        {
            return;
        }

        (ContentPresenter container, StratStepRow row) = hit;
        StratEditorViewModel editor = vm.Editor;
        bool onRow = ReferenceEquals(e.Source, container);
        bool inLine = e.Source is TextBox { AcceptsReturn: false };
        bool inOpenCombo = e.Source is ComboBox { IsDropDownOpen: true };
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None && (onRow || inLine):
                container.Focus();
                editor.AddStepCommand.Execute(row);
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control && !inOpenCombo:
                container.Focus();
                editor.DuplicateStepCommand.Execute(row);
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None && onRow:
                int index = StepRows.IndexFromContainer(container);
                editor.RemoveStepCommand.Execute(row);
                if (editor.Steps.Count > 0)
                {
                    FocusRow(editor.Steps[Math.Min(index, editor.Steps.Count - 1)].Id, false);
                }

                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None && e.Source is TextBox:
                container.Focus();
                break;
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.Alt && (onRow || e.Source is TextBox):
                container.Focus();
                (e.Key == Key.Up ? editor.MoveStepUpCommand : editor.MoveStepDownCommand).Execute(row);
                FocusRow(row.Id, false);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // Posted: the rows are rebuilt when the list changes, and the new containers need a layout pass.
    private void OnStepFocusRequested(Guid id) => FocusRow(id, true);

    private void FocusRow(Guid id, bool firstField) => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is not StratBookTabViewModel vm
            || vm.Editor.Steps.Select((r, i) => (r, i)).FirstOrDefault(p => p.r.Id == id) is not { r: not null } found
            || StepRows.ContainerFromIndex(found.i) is not { } container)
        {
            return;
        }

        Control target = firstField ? container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? container : container;
        target.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    // The row a focus or key event came from: the item container, not a presenter inside the row template.
    private (ContentPresenter Container, StratStepRow Row)? RowOf(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null && !ReferenceEquals(visual, StepRows); visual = visual.GetVisualParent())
        {
            if (visual is ContentPresenter { DataContext: StratStepRow row } presenter && StepRows.IndexFromContainer(presenter) >= 0)
            {
                return (presenter, row);
            }
        }

        return null;
    }

    // Filled here as well as on opening: a menu flyout with no items does not open.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_editor is not null)
        {
            _editor.StepFocusRequested -= OnStepFocusRequested;
            _editor = null;
        }

        if (DataContext is StratBookTabViewModel vm)
        {
            _editor = vm.Editor;
            _editor.StepFocusRequested += OnStepFocusRequested;
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
