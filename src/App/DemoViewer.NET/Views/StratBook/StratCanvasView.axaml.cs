#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Input;

#endregion

namespace DemoViewer.NET.Views.StratBook;

/// <summary>
///     The Step Authoring canvas view. The four wirings the 2D Playback view makes around its host, made here
///     for the strat (the scene frame host note, §1): the tool row to the host's router, the keymap through
///     the canvas's resolved profile with hold-pan and cancel kept for the surface, the text tool's editor,
///     and focus on a click so keys work without a Tab. The host itself binds the canvas as its frame host
///     through this view's DataContext.
/// </summary>
public partial class StratCanvasView : UserControl
{
    private readonly Scene2DHost? _host;
    private readonly Canvas? _textEditorLayer;
    private readonly TextBox? _textEditor;

    private StratCanvasViewModel? _bound;
    private bool _editingText;

    // The key that started a hold-pan, latched so a rebind while it is down cannot strand the surface panning.
    private Key? _holdPanKey;

    public StratCanvasView()
    {
        AvaloniaXamlLoader.Load(this);

        _host = this.FindControl<Scene2DHost>("Host");
        _textEditorLayer = this.FindControl<Canvas>("TextEditorLayer");
        _textEditor = this.FindControl<TextBox>("AnnotationTextEditor");
        if (_host is IAnnotationSurface surface && _textEditor is not null)
        {
            surface.TextEditRequested += OnTextEditRequested;
            _textEditor.KeyDown += OnTextEditorKeyDown;
            _textEditor.LostFocus += OnTextEditorLostFocus;
        }

        // Tunnel, like the 2D view: the canvas's keys win over a focused button in the tool row.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) => Bind();
        Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.Annotations.ToolSelected -= OnToolSelected;
        }

        _bound = DataContext as StratCanvasViewModel;
        if (_bound is not null)
        {
            _bound.Annotations.ToolSelected += OnToolSelected;
            OnToolSelected(_bound.Annotations.ActiveTool);
        }
    }

    private void OnToolSelected(ToolKind kind) => (_host as IAnnotationSurface)?.SetActiveTool(kind);

    private void OnFitClick(object? sender, RoutedEventArgs e) => _host?.FitToExtent();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_editingText && !IsInsideTextEditor(e.Source))
        {
            CommitTextEdit(_textEditor?.Text);
        }

        if (!_editingText && e.Source is Visual source && _host is not null
            && (ReferenceEquals(source, _host) || _host.IsVisualAncestorOf(source)))
        {
            Focus();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _bound is not { } vm || IsTextInputFocused())
        {
            return;
        }

        if (!vm.Keymap.TryResolve(e, vm.IsToolActive, out Playback2DAction action))
        {
            return;
        }

        IAnnotationSurface? surface = _host;
        switch (action)
        {
            case Playback2DAction.HoldPan:
                if (surface is not null)
                {
                    _holdPanKey = e.Key;
                    surface.SetSpacePanHeld(true);
                    e.Handled = true;
                }

                return;

            case Playback2DAction.CancelGesture:
                if (surface is not null)
                {
                    surface.CancelActiveGesture();
                    e.Handled = true;
                }

                return;

            default:
                e.Handled = vm.ExecuteAction(action);
                return;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_holdPanKey is not { } latched || e.Key != latched)
        {
            return;
        }

        _holdPanKey = null;
        (_host as IAnnotationSurface)?.SetSpacePanHeld(false);
    }

    private bool IsTextInputFocused() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox;

    private bool IsInsideTextEditor(object? source) =>
        _textEditor is not null && source is Visual visual
                                && (ReferenceEquals(visual, _textEditor) || _textEditor.IsVisualAncestorOf(visual));

    // The text tool placed a label: the editor goes over it at the label's size, focus posted because the
    // press that placed it is still being routed and would take focus straight back.
    private void OnTextEditRequested(Point hostPoint, double emPixels)
    {
        if (_textEditor is null || _textEditorLayer is null || _host is null)
        {
            (_host as IAnnotationSurface)?.CompleteTextEdit(null);
            return;
        }

        Point at = _host.TranslatePoint(hostPoint, _textEditorLayer) ?? hostPoint;
        Canvas.SetLeft(_textEditor, at.X);
        Canvas.SetTop(_textEditor, at.Y);
        _textEditor.FontSize = Math.Clamp(emPixels, 10, 48);
        _textEditor.Text = "";
        _textEditor.IsVisible = true;
        _editingText = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (_editingText)
            {
                _textEditor.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void OnTextEditorKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitTextEdit(_textEditor?.Text);
                e.Handled = true;
                break;
            case Key.Escape:
                CommitTextEdit(null);
                e.Handled = true;
                break;
        }
    }

    private void OnTextEditorLostFocus(object? sender, RoutedEventArgs e) => CommitTextEdit(_textEditor?.Text);

    // Cleared first: hiding the box moves focus, and losing focus is itself a commit.
    private void CommitTextEdit(string? text)
    {
        if (!_editingText)
        {
            return;
        }

        _editingText = false;
        if (_textEditor is not null)
        {
            _textEditor.IsVisible = false;
            _textEditor.Text = "";
        }

        (_host as IAnnotationSurface)?.CompleteTextEdit(text);
        Focus();
    }
}
