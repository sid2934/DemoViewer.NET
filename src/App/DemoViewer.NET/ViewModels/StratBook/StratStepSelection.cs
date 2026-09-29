#region

using System.Collections.Specialized;
using System.ComponentModel;
using DemoViewer.NET.Modules.StratBook.Canvas;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     One selected step for the step rows and the canvas: the canvas's active step. Selecting a row makes its step
///     active and moves the playhead there; whatever makes another step active (the transport, the step track, a
///     new step) moves the row highlight with it. A drag and Set On Map write the selected step.
/// </summary>
public sealed class StratStepSelection : IDisposable
{
    private readonly StratCanvasViewModel _canvas;
    private readonly StratEditorViewModel _editor;
    private Guid? _shown;

    /// <param name="editor">The step rows.</param>
    /// <param name="canvas">The editing canvas; never the Detected preview's.</param>
    public StratStepSelection(StratEditorViewModel editor, StratCanvasViewModel canvas)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(canvas);
        _editor = editor;
        _canvas = canvas;
        _canvas.PropertyChanged += OnCanvasChanged;
        _editor.Steps.CollectionChanged += OnRowsChanged;
        Sync();
    }

    /// <summary>
    ///     Raised when the highlight moves from one of the rows to another; the view scrolls the row into sight. Not
    ///     when a strat opens or the selected step goes, so opening a strat leaves the editor at its top.
    /// </summary>
    public event Action<Guid?>? Changed;

    /// <summary>The selected step, or null with no strat or no steps.</summary>
    public Guid? SelectedStepId => _canvas.ActiveStep?.Id;

    /// <inheritdoc />
    public void Dispose()
    {
        _canvas.PropertyChanged -= OnCanvasChanged;
        _editor.Steps.CollectionChanged -= OnRowsChanged;
    }

    /// <summary>Selects a row's step and moves the playhead to it; again on the same row, nothing moves.</summary>
    /// <param name="stepId">The row's step.</param>
    public void Select(Guid stepId)
    {
        _canvas.SelectStep(stepId);
        Sync();
    }

    /// <summary>Selects a step and one of its lines: Set On Map then writes that line's place.</summary>
    /// <param name="stepId">The row's step.</param>
    /// <param name="slot">The line's slot.</param>
    public void SelectLine(Guid stepId, string? slot)
    {
        _canvas.SelectLine(slot);
        Select(stepId);
    }

    private void OnCanvasChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StratCanvasViewModel.ActiveStep) or nameof(StratCanvasViewModel.SelectedLineSlot))
        {
            Sync();
        }
    }

    // A reorder or a delete rebuilds the rows, so the highlight is put back on the new ones.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void Sync()
    {
        Guid? selected = SelectedStepId;
        string? slot = _canvas.SelectedLineSlot;
        foreach (StratStepRow row in _editor.Steps)
        {
            row.IsSelected = row.Id == selected;
            row.SelectLine(slot);
        }

        if (selected == _shown)
        {
            return;
        }

        // Checked on the canvas's projection, not the rows: the canvas re-projects a newly opened strat first.
        bool moved = _shown is { } previous && _canvas.Projection?.IndexOf(previous) >= 0;
        _shown = selected;
        if (moved)
        {
            Changed?.Invoke(selected);
        }
    }
}
