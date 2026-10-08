#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

/// <summary>
///     The strat canvas's tool row: which pointer tool is active, one command per button, and each button's
///     hint with the user's gesture. The selection is mirrored onto the ink session the scene draws, and
///     <see cref="ToolSelected" /> tells the view to drive the scene's router.
/// </summary>
public sealed partial class StratCanvasTools : ObservableObject
{
    private readonly AnnotationSession _session;
    private IExtensionKeymap? _keymap;

    /// <summary>The active pointer tool.</summary>
    [ObservableProperty]
    private ToolKind _activeTool = ToolKind.PanZoom;

    /// <summary>A row over <paramref name="session" />, starting on pan/zoom.</summary>
    /// <param name="session">The ink session the scene draws; its active tool follows this row.</param>
    public StratCanvasTools(AnnotationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _session.ActiveTool = ActiveTool;
    }

    /// <summary>Raised when a tool is picked, by button or by key.</summary>
    public event Action<ToolKind>? ToolSelected;

    /// <summary>Whether the pan/zoom tool is active, for its button's checked state.</summary>
    public bool IsPanZoomSelected => ActiveTool == ToolKind.PanZoom;

    /// <summary>Whether the token tool is active, for its button's checked state.</summary>
    public bool IsTokenSelected => ActiveTool == ToolKind.Token;

    /// <summary>Whether the pen tool is active, for its button's checked state.</summary>
    public bool IsDrawSelected => ActiveTool == ToolKind.Draw;

    /// <summary>Whether the eraser tool is active, for its button's checked state.</summary>
    public bool IsEraseSelected => ActiveTool == ToolKind.Erase;

    /// <summary>Whether the line tool is active, for its button's checked state.</summary>
    public bool IsLineSelected => ActiveTool == ToolKind.Line;

    /// <summary>Whether the arrow tool is active, for its button's checked state.</summary>
    public bool IsArrowSelected => ActiveTool == ToolKind.Arrow;

    /// <summary>Whether the rectangle tool is active, for its button's checked state.</summary>
    public bool IsRectSelected => ActiveTool == ToolKind.Rect;

    /// <summary>Whether the ellipse tool is active, for its button's checked state.</summary>
    public bool IsEllipseSelected => ActiveTool == ToolKind.Ellipse;

    /// <summary>Whether the text tool is active, for its button's checked state.</summary>
    public bool IsTextSelected => ActiveTool == ToolKind.Text;

    /// <summary>The token button's hint, with the user's gesture.</summary>
    public string TokenToolTip =>
        $"Token tool{Gesture(StratBookActions.ToolToken)}: drag a token to place it at the active step; drag its heading stub to turn it";

    /// <summary>The pen button's hint, with the user's gesture.</summary>
    public string DrawToolTip =>
        $"Draw{Gesture(CoreActions.ToolDraw)}: right-drag for the second pen, middle- or Ctrl-drag to pan{Held()}{Cancel()}";

    /// <summary>The eraser button's hint, with the user's gesture.</summary>
    public string EraseToolTip => $"Erase whole strokes{Gesture(CoreActions.ToolErase)}: middle- or Ctrl-drag to pan";

    /// <summary>The line button's hint, with the user's gesture.</summary>
    public string LineToolTip => $"Line{Gesture(CoreActions.ToolLine)}, Shift snaps to 45°";

    /// <summary>The arrow button's hint, with the user's gesture.</summary>
    public string ArrowToolTip => $"Arrow{Gesture(CoreActions.ToolArrow)}, Shift snaps to 45°";

    /// <summary>The rectangle button's hint, with the user's gesture.</summary>
    public string RectToolTip => $"Rectangle{Gesture(CoreActions.ToolRect)}, Shift draws a square";

    /// <summary>The ellipse button's hint, with the user's gesture.</summary>
    public string EllipseToolTip => $"Ellipse{Gesture(CoreActions.ToolEllipse)}, Shift draws a circle";

    /// <summary>The text button's hint, with the user's gesture.</summary>
    public string TextToolTip =>
        $"Text label{Gesture(CoreActions.ToolText)}: click to place, type, Enter to keep, Esc to drop";

    /// <summary>Re-reads the hints' gestures from <paramref name="keymap" />; null shows no gestures.</summary>
    /// <param name="keymap">The user's keymap.</param>
    public void ApplyKeymap(IExtensionKeymap? keymap)
    {
        _keymap = keymap;
        OnPropertyChanged(nameof(TokenToolTip));
        OnPropertyChanged(nameof(DrawToolTip));
        OnPropertyChanged(nameof(EraseToolTip));
        OnPropertyChanged(nameof(LineToolTip));
        OnPropertyChanged(nameof(ArrowToolTip));
        OnPropertyChanged(nameof(RectToolTip));
        OnPropertyChanged(nameof(EllipseToolTip));
        OnPropertyChanged(nameof(TextToolTip));
    }

    /// <summary>Selects a tool. Idempotent.</summary>
    /// <param name="kind">The tool.</param>
    public void SelectTool(ToolKind kind) => ActiveTool = kind;

    [RelayCommand]
    private void SelectPanZoom() => SelectTool(ToolKind.PanZoom);

    [RelayCommand]
    private void SelectToken() => SelectTool(ToolKind.Token);

    [RelayCommand]
    private void SelectDraw() => SelectTool(ToolKind.Draw);

    [RelayCommand]
    private void SelectErase() => SelectTool(ToolKind.Erase);

    [RelayCommand]
    private void SelectLine() => SelectTool(ToolKind.Line);

    [RelayCommand]
    private void SelectArrow() => SelectTool(ToolKind.Arrow);

    [RelayCommand]
    private void SelectRect() => SelectTool(ToolKind.Rect);

    [RelayCommand]
    private void SelectEllipse() => SelectTool(ToolKind.Ellipse);

    [RelayCommand]
    private void SelectText() => SelectTool(ToolKind.Text);

    partial void OnActiveToolChanged(ToolKind value)
    {
        _session.ActiveTool = value;
        OnPropertyChanged(nameof(IsPanZoomSelected));
        OnPropertyChanged(nameof(IsTokenSelected));
        OnPropertyChanged(nameof(IsDrawSelected));
        OnPropertyChanged(nameof(IsEraseSelected));
        OnPropertyChanged(nameof(IsLineSelected));
        OnPropertyChanged(nameof(IsArrowSelected));
        OnPropertyChanged(nameof(IsRectSelected));
        OnPropertyChanged(nameof(IsEllipseSelected));
        OnPropertyChanged(nameof(IsTextSelected));
        ToolSelected?.Invoke(value);
    }

    // An unbound action yields "", and " ()" would read as a bug, so each hint carries its own punctuation.
    private string Gesture(string actionId) =>
        _keymap?.GestureText(actionId) is { Length: > 0 } text ? $" ({text})" : "";

    private string Held() =>
        _keymap?.GestureText(CoreActions.HoldPan) is { Length: > 0 } text ? $", {text} to pan" : "";

    private string Cancel() =>
        _keymap?.GestureText(CoreActions.CancelGesture) is { Length: > 0 } text ? $", {text} to cancel" : "";
}
