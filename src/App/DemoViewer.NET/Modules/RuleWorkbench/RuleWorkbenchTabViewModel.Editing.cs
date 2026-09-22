#region

using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     The node editor's write path, and its undo (design.md §6.4 items 4 and 5).
///     <para>
///         <b>Every gesture here produces a new BUFFER, never a file.</b> It goes out through
///         <see cref="RulesetNodeEditing" />, which goes through <c>RulesetDocumentEditor</c>, which
///         splices the original text. Save already writes the buffer, dirty-tracking already follows
///         it, the checker already re-runs on it, and the graph already re-renders from it, so
///         editing a node reuses all four rather than growing a second copy of any of them. A second
///         write path is exactly what §9 decision 4 rules out: it would make whether an author's
///         comments survive depend on which surface they happened to use.
///     </para>
///     <para>
///         <b>Undo holds values, not inverse commands.</b> <c>RulesetDocumentEditor</c> is immutable
///         and every operation returns a whole new document, so the cheapest correct undo is to keep
///         the one it replaced. Inverting a splice is possible but pointless: the inverse of
///         "remove a stat" has to carry the removed text anyway, at which point it is the old buffer
///         with extra steps. Holding buffers also means a node edit and a text edit undo the same
///         way, which matters because this pane and the text pane edit the same document.
///     </para>
/// </summary>
public sealed partial class RuleWorkbenchTabViewModel
{
    // Buffer snapshots. See the class remarks for why these are values.
    private readonly Stack<string> _redo = new();
    private readonly Stack<string> _undo = new();

    /// <summary>Whether an undone edit is waiting to be re-applied.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Whether there is a previous buffer to go back to.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>The open buffer parsed as a ruleset, or <c>null</c> when it does not load.</summary>
    private RulesetDoc? OpenDocument() =>
        SelectedFile is null ? null : RulesetDocumentLoader.Load(DocumentText, SelectedFile.FullPath).Doc;

    /// <summary>
    ///     Replaces the buffer and records the previous one. A no-op edit is dropped rather than
    ///     pushed, so an author who retypes a value into the field it already has does not have to
    ///     press undo to get back to where they were.
    /// </summary>
    private void PushBuffer(string text)
    {
        if (string.Equals(text, DocumentText, StringComparison.Ordinal))
        {
            return;
        }

        _undo.Push(DocumentText);
        _redo.Clear();
        DocumentText = text;
        AfterBufferChange();
    }

    private void AfterBufferChange()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        RenderGraphForOpenFile();
        RefreshSelectedNodeFields();
    }

    /// <summary>Steps the buffer back one edit.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(DocumentText);
        DocumentText = _undo.Pop();
        AfterBufferChange();
    }

    /// <summary>Re-applies the last undone edit.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(DocumentText);
        DocumentText = _redo.Pop();
        AfterBufferChange();
    }

    /// <summary>Adds a stat and selects it, so the new node is the one being edited.</summary>
    [RelayCommand]
    private void AddStatNode()
    {
        if (SelectedFile is null || IsReadOnlyFile)
        {
            return;
        }

        try
        {
            (string text, string id) = RulesetNodeEditing.AddStat(DocumentText);
            PushBuffer(text);
            SelectedRulesetNode = RulesetNodes.FirstOrDefault(n => n.Binding.Id == id);
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "add a stat", ex);
            SelectedNodeSummary = UserFacingError.Describe("add a stat", ex);
        }
    }

    /// <summary>Removes the selected node's stat or highlight.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedNode))]
    private void DeleteSelectedNode()
    {
        if (SelectedRulesetNode is not { Binding.IsEditable: true } node || IsReadOnlyFile)
        {
            return;
        }

        try
        {
            PushBuffer(RulesetNodeEditing.Delete(DocumentText, node.Binding));
            SelectedRulesetNode = null;
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "delete a node", ex);
            SelectedNodeSummary = UserFacingError.Describe("delete a node", ex);
        }
    }

    /// <summary>
    ///     Writes one field of the selected node. A blank value removes the key, which is the only
    ///     gesture a field editor has for taking an optional one back off.
    /// </summary>
    [RelayCommand]
    private void SetNodeField(RulesetNodeField? field)
    {
        if (field is null || SelectedRulesetNode is not { Binding.IsEditable: true } node
            || IsReadOnlyFile)
        {
            return;
        }

        try
        {
            PushBuffer(RulesetNodeEditing.SetField(DocumentText, node.Binding, field.Key, field.Value));
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "edit a node field", ex);
            SelectedNodeSummary = UserFacingError.Describe("edit a node field", ex);
        }
    }

    partial void OnSelectedRulesetNodeChanged(RulesetGraphNode? value)
    {
        OnPropertyChanged(nameof(CanEditSelectedNode));
        RefreshSelectedNodeFields();
        RevealSelectedNode(value); // picking a node on the canvas reveals the YAML that declares it
    }

    private void RefreshSelectedNodeFields()
    {
        SelectedNodeFields.Clear();

        if (SelectedRulesetNode is not { } node)
        {
            SelectedNodeSummary = "Select a node to edit the stat behind it.";
            return;
        }

        if (!node.Binding.IsEditable)
        {
            // Said plainly rather than left as an empty panel. Every node the canvas draws was
            // written by a human, but not all of them in THIS file: a `use:` dependency's stats
            // compose in and are read-only here, and an author who clicks one deserves to know why
            // nothing happened.
            SelectedNodeSummary =
                $"{node.Title} is declared by a ruleset this one use:s, not by this file, "
                + "so there is nothing here to edit.";
            return;
        }

        if (IsReadOnlyFile)
        {
            SelectedNodeSummary = $"{node.Title} is in a shipped ruleset. Save-As to edit it.";
            return;
        }

        try
        {
            foreach (RulesetNodeField field in
                     RulesetNodeEditing.ReadFields(DocumentText, node.Binding))
            {
                SelectedNodeFields.Add(field);
            }

            SelectedNodeSummary = $"{node.Binding.Kind.ToString().ToLowerInvariant()} {node.Binding.Id}";
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "read node fields", ex);
            SelectedNodeSummary = UserFacingError.Describe("read node fields", ex);
        }
    }
}
