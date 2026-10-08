#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Input;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.StratBook;

/// <summary>
///     The Step Authoring canvas view. The four wirings the 2D Playback view makes around its host, made here
///     for the strat: the tool row to the scene's router, the keys through the canvas's keymap with hold-pan
///     and cancel kept for the scene, the text tool's editor, and focus on a click so keys work without a
///     Tab. The scene draws the canvas, handed to it as its source from this view's DataContext.
/// </summary>
public partial class StratCanvasView : UserControl
{
    private readonly SceneView? _scene;
    private readonly Canvas? _dragLabelLayer;
    private readonly Border? _dragLabel;
    private readonly Canvas? _textEditorLayer;
    private readonly TextBox? _textEditor;

    private StratCanvasViewModel? _bound;
    private bool _editingText;
    private IExtensionKeymap? _watched;

    // The key that started a hold-pan, latched so a rebind while it is down cannot strand the surface panning.
    private Key? _holdPanKey;

    public StratCanvasView()
    {
        AvaloniaXamlLoader.Load(this);

        _scene = this.FindControl<SceneView>("Scene");
        _textEditorLayer = this.FindControl<Canvas>("TextEditorLayer");
        _dragLabelLayer = this.FindControl<Canvas>("DragLabelLayer");
        _dragLabel = this.FindControl<Border>("DragLabel");
        _textEditor = this.FindControl<TextBox>("AnnotationTextEditor");
        if (_scene is not null && _textEditor is not null)
        {
            _scene.TextEditRequested += OnTextEditRequested;
            _textEditor.KeyDown += OnTextEditorKeyDown;
            _textEditor.LostFocus += OnTextEditorLostFocus;
        }

        // The strat canvas's own tool and layer: the guides are read off whichever canvas is bound at the time.
        if (_scene is not null)
        {
            _scene.AddTool(new TokenTool());
            _scene.AddLayer(SceneLayerIds.Guides, () => new GuideLayer(() => _bound?.Guides ?? SceneGuides.None));
        }

        // Tunnel, like the 2D view: the canvas's keys win over a focused button in the tool row.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, true);

        DataContextChanged += (_, _) => Bind();
        Bind();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchKeymap(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WatchKeymap(false);
        base.OnDetachedFromVisualTree(e);
    }

    private void Bind()
    {
        WatchKeymap(false);
        if (_bound is not null)
        {
            _bound.Tools.ToolSelected -= OnToolSelected;
        }

        _bound = DataContext as StratCanvasViewModel;
        if (_scene is not null)
        {
            _scene.Source = _bound;
        }

        if (_bound is not null)
        {
            _bound.Tools.ToolSelected += OnToolSelected;
            OnToolSelected(_bound.Tools.ActiveTool);
        }

        WatchKeymap(this.IsAttachedToVisualTree());
    }

    // Only while on screen: the keymap lives as long as the extension, so a handler left on it would keep this
    // view and its canvas alive after the pack is turned off.
    private void WatchKeymap(bool on)
    {
        if (_watched is not null)
        {
            _watched.Changed -= OnKeymapChanged;
            _watched = null;
        }

        if (on && _bound?.Keymap is { } keymap)
        {
            _watched = keymap;
            keymap.Changed += OnKeymapChanged;
            _bound.RefreshKeymap();
        }
    }

    private void OnKeymapChanged() => _bound?.RefreshKeymap();

    private void OnToolSelected(ToolKind kind) => _scene?.SetActiveTool(kind);

    private void OnFitClick(object? sender, RoutedEventArgs e) => _scene?.Fit();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_editingText && !IsInsideTextEditor(e.Source))
        {
            CommitTextEdit(_textEditor?.Text);
        }

        if (!_editingText && e.Source is Visual source && _scene is not null
            && (ReferenceEquals(source, _scene) || _scene.IsVisualAncestorOf(source)))
        {
            Focus();
        }
    }

    // The drag label follows the pointer, kept inside the scene.
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLabel is null || _dragLabelLayer is null || _bound is not { IsDragging: true })
        {
            return;
        }

        PlaceDragLabel(e.GetPosition(_dragLabelLayer));
    }

    /// <summary>Puts the drag label beside a point of the scene: right of and above it, flipped at the edges.</summary>
    /// <param name="at">The pointer, in the scene's coordinates.</param>
    internal void PlaceDragLabel(Point at)
    {
        if (_dragLabel is null || _dragLabelLayer is null)
        {
            return;
        }

        if (_dragLabel.DesiredSize.Width <= 0)
        {
            _dragLabel.Measure(Size.Infinity);
        }

        Size size = _dragLabel.DesiredSize;
        Rect bounds = _dragLabelLayer.Bounds;
        double x = at.X + 18 + size.Width > bounds.Width ? at.X - 18 - size.Width : at.X + 18;
        double y = at.Y - 18 - size.Height < 0 ? at.Y + 18 : at.Y - 18 - size.Height;
        Canvas.SetLeft(_dragLabel, Math.Max(0, x));
        Canvas.SetTop(_dragLabel, Math.Max(0, y));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _bound is not { } vm || IsTextInputFocused())
        {
            return;
        }

        if (vm.ResolveKey(e.Key, e.KeyModifiers) is not { } action)
        {
            return;
        }

        switch (action)
        {
            case CoreActions.HoldPan:
                if (_scene is not null)
                {
                    _holdPanKey = e.Key;
                    _scene.SetHoldPan(true);
                    e.Handled = true;
                }

                return;

            case CoreActions.CancelGesture:
                if (vm.CancelSetPlace())
                {
                    e.Handled = true;
                    return;
                }

                if (_scene is not null)
                {
                    _scene.CancelGesture();
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
        _scene?.SetHoldPan(false);
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
        if (_textEditor is null || _textEditorLayer is null || _scene is null)
        {
            _scene?.CompleteTextEdit(null);
            return;
        }

        Point at = _scene.TranslatePoint(hostPoint, _textEditorLayer) ?? hostPoint;
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

        _scene?.CompleteTextEdit(text);
        Focus();
    }
}
