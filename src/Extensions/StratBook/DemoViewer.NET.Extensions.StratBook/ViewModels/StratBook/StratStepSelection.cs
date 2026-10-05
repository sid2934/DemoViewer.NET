#region

using System.Collections.Specialized;
using System.ComponentModel;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;

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

    /// <summary>Selects the Start row: the playhead goes to tick 0, where a drag or a map pick writes a token's start.</summary>
    public void SelectStart()
    {
        _canvas.SelectStart();
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

    /// <summary>
    ///     A location control's "pick on map": selects the field's step and line, then arms the canvas so the next map
    ///     click writes that field. Again on the field while it is armed, it cancels. False when nothing was armed.
    /// </summary>
    /// <param name="field">The field the click writes.</param>
    public bool PickOnMap(StratLocationField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_canvas.ArmedField == field)
        {
            _canvas.CancelSetPlace();
            return false;
        }

        bool armed = _canvas.BeginSetPlace(field);
        Sync();
        return armed;
    }

    /// <summary>Whether a map click will write <paramref name="field" />.</summary>
    /// <param name="field">A location field.</param>
    public bool IsPicking(StratLocationField field) => _canvas.ArmedField == field;

    private void OnCanvasChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StratCanvasViewModel.ActiveStep) or nameof(StratCanvasViewModel.SelectedLineSlot)
            or nameof(StratCanvasViewModel.IsStartSelected))
        {
            Sync();
        }
    }

    // A reorder or a delete rebuilds the rows, so the highlight is put back on the new ones.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void Sync()
    {
        bool start = _canvas.IsStartSelected;
        _editor.Start.IsSelected = start;
        Guid? selected = start ? null : SelectedStepId;
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
