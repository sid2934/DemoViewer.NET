#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using CS2DemoKit.Analysis.Catalog;
using DemoViewer.NET.Modules.RuleWorkbench;

#endregion

namespace DemoViewer.NET.Views.RuleWorkbench;

/// <summary>
///     The Authoring Workbench tab's View. DataContext is the descriptor's
///     <see cref="RuleWorkbenchTabViewModel" />. AvaloniaEdit's <see cref="TextEditor.Text" /> does not
///     two-way bind cleanly, so this code-behind bridges editor ↔ VM manually (re-entrancy-guarded) and
///     wires the VM's caret-jump event to the editor.
/// </summary>
public partial class RuleWorkbenchView : UserControl
{
    private readonly ListBox? _diagnostics;
    private readonly TextEditor? _editor;
    private readonly TreeView? _pathsTree;
    private CatalogRoot? _catalog;
    private CompletionWindow? _completionWindow;
    private bool _syncing; // guards editor→VM and VM→editor from looping
    private RuleWorkbenchTabViewModel? _vm;

    public RuleWorkbenchView()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("Editor");
        _diagnostics = this.FindControl<ListBox>("DiagnosticsList");
        _pathsTree = this.FindControl<TreeView>("PathsTree");

        if (_editor is not null)
        {
            ApplySyntaxTheme(); // YAML colours (#5), theme-aware (L2b); re-applied on attach + theme change
            _editor.TextChanged += OnEditorTextChanged;
            _editor.TextArea.KeyDown += OnEditorKeyDown; // Ctrl+Space → completion
            _editor.TextArea.TextEntered += OnTextEntered; // GAP-UI-2: auto-trigger + re-narrow
        }

        if (_diagnostics is not null)
        {
            _diagnostics.SelectionChanged += OnDiagnosticSelected;
        }

        if (_pathsTree is not null)
        {
            _pathsTree.DoubleTapped += OnPathDoubleTapped; // double-click a leaf path → insert at caret (#1)
        }

        DataContextChanged += OnDataContextChanged;
    }

    // L2b — the DSL syntax colours follow the app theme. AvaloniaEdit caches its parsed highlighting
    // definition, so live-updating means re-setting a fresh per-variant definition + redrawing the view.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplySyntaxTheme(); // ActualThemeVariant is resolved now → correct the ctor's initial default
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplySyntaxTheme();

    private void ApplySyntaxTheme()
    {
        if (_editor is null)
        {
            return;
        }

        _editor.SyntaxHighlighting = WorkbenchYamlHighlighting.DefinitionFor(ActualThemeVariant);
        _editor.TextArea.TextView.Redraw(); // re-colourise the visible document with the new definition
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.JumpRequested -= OnJumpRequested;
        }

        _vm = DataContext as RuleWorkbenchTabViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.JumpRequested += OnJumpRequested;
            PushVmTextToEditor();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuleWorkbenchTabViewModel.DocumentText))
        {
            PushVmTextToEditor();
        }
    }

    private void PushVmTextToEditor()
    {
        if (_editor is null || _vm is null || _syncing)
        {
            return;
        }

        if (!string.Equals(_editor.Text, _vm.DocumentText, StringComparison.Ordinal))
        {
            _syncing = true;
            _editor.Text = _vm.DocumentText;
            _syncing = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_editor is null || _vm is null || _syncing)
        {
            return;
        }

        _syncing = true;
        _vm.DocumentText = _editor.Text;
        _syncing = false;
    }

    private void OnDiagnosticSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && _diagnostics?.SelectedItem is WorkbenchDiagnostic diagnostic)
        {
            _vm.RequestJump(diagnostic);
        }
    }

    private void OnJumpRequested(int line, int column)
    {
        if (_editor is null)
        {
            return;
        }

        int safeLine = Math.Clamp(line, 1, Math.Max(1, _editor.Document.LineCount));
        _editor.TextArea.Caret.Line = safeLine;
        _editor.TextArea.Caret.Column = Math.Max(1, column);
        _editor.ScrollToLine(safeLine);
        _editor.Focus();
    }

    // ── M4: data browser — insert a path by double-click or drag ─────────────────────────────────

    private void OnPathDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only insert for a leaf (a node that carries a full path); intermediates just expand/collapse.
        if (_pathsTree?.SelectedItem is WorkbenchPathNode { FullPath: { } path })
        {
            InsertAtCaret(path);
        }
    }

    private void InsertAtCaret(string text)
    {
        if (_editor is null)
        {
            return;
        }

        int offset = Math.Clamp(_editor.CaretOffset, 0, _editor.Document.TextLength);
        _editor.Document.Insert(offset, text);
        _editor.CaretOffset = offset + text.Length;
        _editor.Focus();
    }

    // ── M3: catalog-driven completion (Ctrl+Space) ──────────────────────────────────────────────

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ShowCompletion();
            e.Handled = true;
        }
    }

    // GAP-UI-2: the completion used to fire only on Ctrl+Space and computed its context exactly
    // once per window — typing past a `:` kept the stale (usually Any → whole-vocabulary) list.
    // Now a context-CHANGING character re-narrows the open window by reopen, and the first
    // identifier character auto-opens it when the caret sits in a NARROWABLE position (never on
    // Any, which would pop the full vocabulary on every keystroke in prose-ish places).
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_editor is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        char ch = e.Text[0];
        if (_completionWindow is not null)
        {
            if (ch is ':' or ' ' or '{')
            {
                _completionWindow.Close(); // Closed handler nulls the field
                ShowCompletion();
            }

            return; // other characters: the window's own prefix filter handles them
        }

        if (char.IsLetter(ch) || ch == '_')
        {
            WorkbenchCompletionContext context =
                WorkbenchCompletionSource.ContextFor(LineBeforeCaret(), TextBeforeCaret());
            if (context.AtKeyPosition || context.ActiveKey is not null)
            {
                ShowCompletion();
            }
        }
    }

    private void ShowCompletion()
    {
        if (_editor is null || _completionWindow is not null)
        {
            return;
        }

        // The catalog is immutable — load once and cache.
        _catalog ??= SafeCatalog();
        if (_catalog is null)
        {
            return;
        }

        // Narrow the suggestions to what fits where the caret sits (type-aware;
        // block-scoped since v0.6.0 — the enclosing top-level section picks the key vocabulary).
        WorkbenchCompletionContext context =
            WorkbenchCompletionSource.ContextFor(LineBeforeCaret(), TextBeforeCaret());
        IReadOnlyList<WorkbenchCompletion> candidates = WorkbenchCompletionSource.Build(_catalog, _editor.Text, context);
        if (candidates.Count == 0)
        {
            return;
        }

        CompletionWindow window = new(_editor.TextArea)
        {
            StartOffset = WordStartOffset()
        };
        foreach (WorkbenchCompletion c in candidates)
        {
            window.CompletionList.CompletionData.Add(new WorkbenchCompletionData(c));
        }

        window.Closed += (_, _) => _completionWindow = null;
        _completionWindow = window;
        window.Show();
    }

    /// <summary>The current line's text from its start up to the caret — the completion-context source.</summary>
    private string LineBeforeCaret()
    {
        TextDocument doc = _editor!.Document;
        DocumentLine line = doc.GetLineByOffset(_editor.CaretOffset);
        return doc.GetText(line.Offset, _editor.CaretOffset - line.Offset);
    }

    /// <summary>Everything from the document start to the caret — the block-scope source (v0.6.0).</summary>
    private string TextBeforeCaret() => _editor!.Document.GetText(0, _editor.CaretOffset);

    /// <summary>The offset where the identifier under the caret starts (walking back over word chars + '.').</summary>
    private int WordStartOffset()
    {
        int caret = _editor!.CaretOffset;
        int start = caret;
        TextDocument doc = _editor.Document;
        while (start > 0)
        {
            char ch = doc.GetCharAt(start - 1);
            if (char.IsLetterOrDigit(ch) || ch is '_' or '.')
            {
                start--;
            }
            else
            {
                break;
            }
        }

        return start;
    }

    private static CatalogRoot? SafeCatalog()
    {
        try
        {
            return CatalogResource.Load();
        }
        catch
        {
            return null;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
