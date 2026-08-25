#region

using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;

#endregion

namespace DemoViewer.NET.ViewModels.Playback2D;

/// <summary>
///     The annotation toolbar's view-model: tool selection, ink style, envelope defaults, undo/redo and
///     the persistence status line.
///     <para>
///         A NESTED view-model hanging off <c>Playback2DTabViewModel.Annotations</c> rather than more
///         properties on a tab class that is already 1,400 lines. It owns no document of its own — the
///         <see cref="AnnotationSessionController" /> does, and this projects it.
///     </para>
/// </summary>
public sealed partial class AnnotationsPanelViewModel : ObservableObject, IDisposable
{
    private readonly AnnotationSessionController _controller;
    private readonly Func<int> _currentTick;

    private bool _applyingFromSession;
    private bool _disposed;

    /// <summary>Creates the panel over a controller.</summary>
    /// <param name="controller">The session controller this panel projects.</param>
    /// <param name="currentTick">Reads the playhead in DV frame-clock ticks.</param>
    public AnnotationsPanelViewModel(AnnotationSessionController controller, Func<int> currentTick)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(currentTick);

        _controller = controller;
        _currentTick = currentTick;

        _controller.StateChanged += OnControllerStateChanged;
        PullFromSession();
        RefreshFromController();
    }

    /// <summary>The shared session — handed to the host so its layer and tools see the same document.</summary>
    public AnnotationSession Session => _controller.Session;

    /// <summary>The document, for the timeline track and the tests.</summary>
    public AnnotationDocument Document => _controller.Document;

    /// <summary>Whether the <c>playback2d.annotations</c> feature is on. Fails open.</summary>
    public bool IsEnabled => _controller.IsEnabled;

    /// <summary>Raised when the user picks a tool; the view drives the host's router from it.</summary>
    public event Action<ToolKind>? ToolSelected;

    /// <summary>The active pointer tool.</summary>
    [ObservableProperty]
    private ToolKind _activeTool = ToolKind.PanZoom;

    /// <summary>Ink colour, as the picker sees it.</summary>
    [ObservableProperty]
    private Color _inkColor = Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07);

    /// <summary>Ink width in world units.</summary>
    [ObservableProperty]
    private double _inkWidth = 8;

    /// <summary>Ink opacity 0..1.</summary>
    [ObservableProperty]
    private double _inkOpacity = 1;

    /// <summary>Envelope authoring mode for new elements.</summary>
    [ObservableProperty]
    private EnvelopeMode _visibility = EnvelopeMode.Always;

    /// <summary>Lead-in ticks for <see cref="EnvelopeMode.Fade" /> elements.</summary>
    [ObservableProperty]
    private int _fadeInTicks = 8;

    /// <summary>Lead-out ticks for <see cref="EnvelopeMode.Fade" /> elements.</summary>
    [ObservableProperty]
    private int _fadeOutTicks = 16;

    /// <summary>Fully-opaque hold for <see cref="EnvelopeMode.Fade" /> elements.</summary>
    [ObservableProperty]
    private int _holdTicks = 320;

    /// <summary>Whether a stroke started near a player follows them by SteamId.</summary>
    [ObservableProperty]
    private bool _anchorToEntities;

    /// <summary>One line saying where annotations are saved, or why they are not.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True when the loaded sidecar was authored against a different parse.</summary>
    [ObservableProperty]
    private bool _clockMismatch;

    /// <summary>True when a sidecar belonging to a different demo was found and left alone.</summary>
    [ObservableProperty]
    private bool _demoMismatch;

    /// <summary>How many undo entries are available. Drives the button's enabled state.</summary>
    [ObservableProperty]
    private int _undoDepth;

    /// <summary>How many redo entries are available.</summary>
    [ObservableProperty]
    private int _redoDepth;

    /// <summary>How many elements the document holds.</summary>
    [ObservableProperty]
    private int _elementCount;

    /// <summary>
    ///     <see cref="Visibility" /> as a ComboBox index. A plain enum binding would need a converter
    ///     for one three-item list; the index is the smaller contract.
    /// </summary>
    public int VisibilityIndex
    {
        get => (int)Visibility;
        set
        {
            EnvelopeMode mode = value switch
            {
                1 => EnvelopeMode.Fade,
                2 => EnvelopeMode.Custom,
                _ => EnvelopeMode.Always
            };

            Visibility = mode;
        }
    }

    /// <summary>True while a drawing tool owns the surface — what shadows Space and Esc in the keymap.</summary>
    public bool IsDrawingToolActive => ActiveTool is ToolKind.Draw or ToolKind.Erase;

    /// <summary>True when the pan/zoom tool is selected. Toolbar toggle state.</summary>
    public bool IsPanZoomSelected => ActiveTool == ToolKind.PanZoom;

    /// <summary>True when the draw tool is selected.</summary>
    public bool IsDrawSelected => ActiveTool == ToolKind.Draw;

    /// <summary>True when the erase tool is selected.</summary>
    public bool IsEraseSelected => ActiveTool == ToolKind.Erase;

    /// <summary>Whether undo is available.</summary>
    public bool CanUndo => UndoDepth > 0;

    /// <summary>Whether redo is available.</summary>
    public bool CanRedo => RedoDepth > 0;

    /// <summary>Selects a tool. Idempotent.</summary>
    /// <param name="kind">The tool to select.</param>
    public void SelectTool(ToolKind kind) => ActiveTool = kind;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= OnControllerStateChanged;
    }

    [RelayCommand]
    private void SelectPanZoom() => SelectTool(ToolKind.PanZoom);

    [RelayCommand]
    private void SelectDraw() => SelectTool(ToolKind.Draw);

    [RelayCommand]
    private void SelectErase() => SelectTool(ToolKind.Erase);

    [RelayCommand]
    private void Undo() => _controller.Document.Undo();

    [RelayCommand]
    private void Redo() => _controller.Document.Redo();

    /// <summary>
    ///     Clears every annotation as ONE undo entry — a mis-clicked "clear all" that could not be undone
    ///     would be the single most destructive button on the surface.
    ///     <para>
    ///         Declined while a gesture is in flight. Ctrl+X is bound with the pointer captured mid-stroke,
    ///         and gestures deliberately do not nest — so opening one here threw
    ///         <c>InvalidOperationException</c> straight out of a key handler.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        AnnotationDocument document = _controller.Document;
        if (document.Elements.Count == 0 || document.IsGestureOpen)
        {
            return;
        }

        List<DocDelta> removals = new(document.Elements.Count);
        for (int i = document.Elements.Count - 1; i >= 0; i--)
        {
            removals.Add(new DocDelta.Remove(document.Elements[i].Id));
        }

        using (document.BeginGesture("clear-all"))
        {
            document.Apply(new DocDelta.Batch(removals));
        }
    }

    /// <summary>
    ///     "Pin to now": switches new elements to the Fade envelope opening at the playhead, and re-times
    ///     the most recently drawn element to open there too, so the gesture has an immediate effect
    ///     rather than only changing a default the user cannot see.
    /// </summary>
    [RelayCommand]
    private void PinToNow()
    {
        int tick = _currentTick();
        Visibility = EnvelopeMode.Fade;

        // Re-timing an element mid-gesture would fold the edit into the in-flight stroke's undo entry,
        // so one Ctrl+Z would take both. The default above still changes; the retro-pin waits.
        AnnotationDocument document = _controller.Document;
        if (document.Elements.Count == 0 || document.IsGestureOpen)
        {
            return;
        }

        AnnotationElement newest = document.Elements[^1];
        AnnotationElement pinned = newest with
        {
            Time = TimeEnvelope.Static.PinnedTo(tick, HoldTicks, FadeInTicks, FadeOutTicks)
        };

        document.Apply(new DocDelta.Replace(newest.Id, pinned));
    }

    partial void OnActiveToolChanged(ToolKind value)
    {
        Session.ActiveTool = value;
        OnPropertyChanged(nameof(IsDrawingToolActive));
        OnPropertyChanged(nameof(IsPanZoomSelected));
        OnPropertyChanged(nameof(IsDrawSelected));
        OnPropertyChanged(nameof(IsEraseSelected));

        if (_applyingFromSession)
        {
            return;
        }

        ToolSelected?.Invoke(value);
        _controller.PersistSettings();
    }

    partial void OnInkColorChanged(Color value) => PushStyle();

    partial void OnInkWidthChanged(double value) => PushStyle();

    partial void OnInkOpacityChanged(double value) => PushStyle();

    partial void OnAnchorToEntitiesChanged(bool value)
    {
        Session.AnchorToEntities = value;
        PersistIfUserDriven();
    }

    partial void OnVisibilityChanged(EnvelopeMode value)
    {
        Session.DefaultVisibility = value;
        OnPropertyChanged(nameof(VisibilityIndex));
        PersistIfUserDriven();
    }

    partial void OnFadeInTicksChanged(int value)
    {
        Session.FadeInTicks = Math.Max(0, value);
        PersistIfUserDriven();
    }

    partial void OnFadeOutTicksChanged(int value)
    {
        Session.FadeOutTicks = Math.Max(0, value);
        PersistIfUserDriven();
    }

    partial void OnHoldTicksChanged(int value)
    {
        Session.HoldTicks = Math.Max(0, value);
        PersistIfUserDriven();
    }

    partial void OnUndoDepthChanged(int value) => OnPropertyChanged(nameof(CanUndo));

    partial void OnRedoDepthChanged(int value) => OnPropertyChanged(nameof(CanRedo));

    private void PushStyle()
    {
        Session.Style = new AnnotationStyle(ToArgb(InkColor), (float)InkWidth,
            (float)Math.Clamp(InkOpacity, 0, 1));

        if (_applyingFromSession)
        {
            return;
        }

        _controller.RememberColor(ToArgb(InkColor));
        _controller.PersistSettings();
    }

    private void PersistIfUserDriven()
    {
        if (!_applyingFromSession)
        {
            _controller.PersistSettings();
        }
    }

    // Seeds the panel from the session the controller built out of settings, without echoing every
    // assignment straight back into a settings write.
    private void PullFromSession()
    {
        _applyingFromSession = true;
        try
        {
            AnnotationStyle style = Session.Style;
            InkColor = FromArgb(style.ColorArgb);
            InkWidth = style.WidthWorld;
            InkOpacity = style.Opacity;
            Visibility = Session.DefaultVisibility;
            FadeInTicks = Session.FadeInTicks;
            FadeOutTicks = Session.FadeOutTicks;
            HoldTicks = Session.HoldTicks;
            AnchorToEntities = Session.AnchorToEntities;
            ActiveTool = Session.ActiveTool;
        }
        finally
        {
            _applyingFromSession = false;
        }
    }

    // The controller's StateChanged may arrive from the autosave's thread-pool continuation, so the
    // hop is mandatory: raising PropertyChanged off the UI thread is an Avalonia binding crash.
    private void OnControllerStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshFromController();
            return;
        }

        Dispatcher.UIThread.Post(RefreshFromController);
    }

    private void RefreshFromController()
    {
        if (_disposed)
        {
            return;
        }

        StatusText = _controller.StatusText;
        ClockMismatch = _controller.ClockMismatch;
        DemoMismatch = _controller.DemoMismatch;
        UndoDepth = _controller.Document.UndoDepth;
        RedoDepth = _controller.Document.RedoDepth;
        ElementCount = _controller.Document.Elements.Count;
        OnPropertyChanged(nameof(IsEnabled));
    }

    /// <summary>Re-reads settings into the session and re-seeds the panel. After a demo attach.</summary>
    public void Resync()
    {
        PullFromSession();
        RefreshFromController();
    }

    private static uint ToArgb(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>The ink colour as <c>#AARRGGBB</c>, for the swatch tooltip.</summary>
    public string InkColorHex => "#" + ToArgb(InkColor).ToString("X8", CultureInfo.InvariantCulture);
}
