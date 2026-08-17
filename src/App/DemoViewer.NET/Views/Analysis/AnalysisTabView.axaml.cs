#region

using Avalonia.Controls;
using DemoViewer.NET.Debugging;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.Views.Analysis;

/// <summary>Analysis tab view.</summary>
public partial class AnalysisTabView : UserControl
{
    // Set true around a programmatic edit of the condition text (an accepted suggestion splice) so
    // the resulting TextChanged doesn't immediately re-open the suggestion list we just dismissed.
    private bool _suppressSuggestionRefresh;
    private int _tokenEnd;

    // The [start,end) span of the token the current suggestions were computed for, captured in
    // OnConditionTextChanged while the TextBox still owns the caret. OnSuggestionSelected reuses it
    // instead of re-reading CaretIndex (focus has moved to the suggestion list by click time, so the
    // caret can't be trusted there). The text can't change between those events without another
    // TextChanged firing and overwriting this, so the span stays valid for the displayed list.
    private int _tokenStart;

    /// <summary>Initializes a new <see cref="AnalysisTabView" /> instance.</summary>
    public AnalysisTabView()
    {
        InitializeComponent();
        // Node and edge right-clicks share one handler — it builds a ConditionTarget from whichever
        // element was hit and the menu is uniform.
        GraphCanvas.NodeContextRequested += OnGraphElementContextRequested;
        GraphCanvas.EdgeContextRequested += OnGraphElementContextRequested;
        GraphCanvas.NodePicked += OnGraphNodePicked;
        ConditionTextBox.TextChanged += OnConditionTextChanged;
        ConditionSuggestionsList.SelectionChanged += OnSuggestionSelected;
    }

    private AnalysisViewModel? Vm => GraphCanvas.DataContext as AnalysisViewModel;

    // Right-clicking a node OR an edge raises this; wrap the hit element in a ConditionTarget and build
    // one uniform add/condition/remove menu. View-only wiring — the state lives on AnalysisViewModel
    // (the GraphView's inherited DataContext). The two capability gates (breakpointable, condition-
    // supported) are the only node-vs-edge difference; nodes pass both unconditionally.
    private void OnGraphElementContextRequested(object? sender, GraphElementContextEventArgs e)
    {
        if (GraphCanvas.DataContext is not AnalysisViewModel vm)
        {
            return;
        }

        ConditionTarget? target = e.Node is { } node ? ConditionTarget.ForNode(node)
            : e.Edge is { } edge ? ConditionTarget.ForEdge(edge)
            : null;
        if (target is null)
        {
            return;
        }

        ContextMenu menu = new()
        {
            Placement = PlacementMode.Pointer
        };

        // Un-backed edges (logic / conjunction) can't carry a breakpoint — show a disabled note rather
        // than arming one that could never fire. Nodes are always breakpointable.
        if (!vm.IsBreakpointable(target))
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Can't break here — edge has no fire event",
                IsEnabled = false
            });
            menu.Open(GraphCanvas);
            return;
        }

        bool has = vm.HasBreakpoint(target);

        if (!has)
        {
            MenuItem add = new()
            {
                Header = target.Kind == GraphBreakpointTarget.Node
                    ? "Add breakpoint"
                    : "Add breakpoint (break when this edge fires)"
            };
            add.Click += (_, _) => vm.AddBreakpoint(target);
            menu.Items.Add(add);
        }

        // Conditional item only when the target supports a condition (nodes always; edges only when
        // their event exposes typed fields — entity-change edges are default-only).
        if (vm.SupportsCondition(target))
        {
            MenuItem condition = new()
            {
                Header = has ? "Edit condition…" : "Add conditional breakpoint…"
            };
            condition.Click += (_, _) => vm.BeginEdit(target);
            menu.Items.Add(condition);
        }

        if (has)
        {
            menu.Items.Add(new Separator());
            MenuItem remove = new()
            {
                Header = "Remove breakpoint"
            };
            remove.Click += (_, _) => vm.RemoveBreakpoint(target);
            menu.Items.Add(remove);
        }

        // "Verify in CS2" — the same pointer-release context-menu
        // idiom as the breakpoint items above, on the rule-trigger surface (nodes + trigger-backed edges;
        // un-backed logic edges returned early above, so this only reaches real triggers). Two-level gate:
        //   • PRESENT only when the Live Sync chip is (chrome.livesync + desktop) — else no item at all.
        //   • ENABLED only while a Synced session exists; otherwise a disabled item whose header/tooltip
        //     point the user at the Live Sync chip. We never auto-launch CS2 from here.
        AddVerifyInCs2Item(menu, vm, target);

        if (menu.Items.Count > 0)
        {
            menu.Open(GraphCanvas);
        }
    }

    // Appends the "Verify in CS2" item per its two-level gate. Presence is level-1 (chrome.livesync +
    // desktop); enabled-vs-disabled+prompt is level-2 (a live Synced session). The right-clicked element
    // is passed as the command parameter so the VM resolves THAT trigger's firing tick. All the tick/name
    // resolution and busy/failure handling lives on the VM command — this only shapes the menu item.
    private static void AddVerifyInCs2Item(ContextMenu menu, AnalysisViewModel vm, ConditionTarget target)
    {
        if (!(vm.IsVerifyInCs2Present?.Invoke() ?? false))
        {
            return; // absent for users who never opted into Live Sync
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        if (vm.VerifyInCs2Command.CanExecute(target))
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Verify in CS2",
                Command = vm.VerifyInCs2Command,
                CommandParameter = target
            });
            return;
        }

        // Disabled. A disabled MenuItem gets no pointer-over in Avalonia, so a ToolTip on it never
        // surfaces — the enable-first prompt must live in the always-visible header. Only the "no live session"
        // case gets the prompt; a transient in-flight/no-position disable leaves the plain label so the
        // header is never actively wrong.
        bool notSynced = !(vm.CanVerifyMoment?.Invoke() ?? false);
        MenuItem disabled = new()
        {
            Header = notSynced ? "Verify in CS2  —  enable Live Sync first" : "Verify in CS2",
            IsEnabled = false
        };
        if (notSynced)
        {
            ToolTip.SetTip(disabled, "Enable Live Sync to verify this moment in CS2.");
        }

        menu.Items.Add(disabled);
    }

    // Pick gesture: a node clicked while pick-mode is armed is appended to the condition being edited.
    private void OnGraphNodePicked(object? sender, IGraphNode node) => Vm?.InsertPickedNode(node);

    // Recompute autocomplete suggestions for the identifier token under the caret (a view concern —
    // caret position lives on the control, not the VM). The VM does the actual filtering.
    private void OnConditionTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSuggestionRefresh)
        {
            _suppressSuggestionRefresh = false;
            return;
        }

        if (Vm is not { } vm)
        {
            return;
        }

        (_tokenStart, _tokenEnd, string prefix) = TokenAtCaret(ConditionTextBox.Text ?? "", ConditionTextBox.CaretIndex);
        vm.UpdateConditionSuggestions(prefix);
    }

    // Clicking a suggestion splices it into the editor at the token span captured while the TextBox
    // held the caret, then returns focus to the editor so the user can keep typing.
    private void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string chosen || Vm is not { } vm)
        {
            return;
        }

        string text = ConditionTextBox.Text ?? "";
        // Clamp the captured span to the current text as a belt-and-braces guard (the text shouldn't
        // have changed since it was captured, but never index out of range if it somehow did).
        int start = Math.Clamp(_tokenStart, 0, text.Length);
        int end = Math.Clamp(_tokenEnd, start, text.Length);
        string spliced = text[..start] + chosen + text[end..];

        _suppressSuggestionRefresh = true; // the programmatic Text set must not re-open the list
        ConditionTextBox.Text = spliced;
        ConditionTextBox.CaretIndex = start + chosen.Length;
        ConditionSuggestionsList.SelectedItem = null;
        vm.ClearConditionSuggestions();
        ConditionTextBox.Focus();
    }

    // The identifier token (letters / digits / underscore / dot — a dotted entity key like
    // `entity.game.freeze_period` counts as ONE token, matching the expression compiler's
    // reassembly) that the caret sits inside or just after. Returns its [start,end) span and the
    // prefix up to the caret (what we filter suggestions by).
    private static (int Start, int End, string Prefix) TokenAtCaret(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);

        int start = caret;
        while (start > 0 && IsTokenChar(text[start - 1]))
        {
            start--;
        }

        int end = caret;
        while (end < text.Length && IsTokenChar(text[end]))
        {
            end++;
        }

        return (start, end, text[start..caret]);
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}
