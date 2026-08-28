#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using Playback2DAction = DemoViewer.NET.Modules.Playback2D.Playback2DAction;
using Playback2DKeymapProfile = DemoViewer.NET.Modules.Playback2D.Playback2DKeymapProfile;

#endregion

namespace DemoViewer.NET.ViewModels.Playback2D;

/// <summary>
///     One recently used ink colour, ready to paint. Carries an <see cref="ImmutableSolidColorBrush" />
///     and never a <c>SolidColorBrush</c>: that constructor asserts UI-thread affinity, which would make
///     the panel untestable off the dispatcher for the sake of a brush the panel never mutates.
/// </summary>
public sealed class AnnotationSwatch
{
    private AnnotationSwatch(uint argb, string hex)
    {
        Argb = argb;
        Hex = hex;
        Brush = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
    }

    /// <summary>Packed ARGB (0xAARRGGBB).</summary>
    public uint Argb { get; }

    /// <summary>The persisted spelling, <c>#AARRGGBB</c>. Also the tooltip.</summary>
    public string Hex { get; }

    /// <summary>The fill for the swatch button.</summary>
    public IImmutableSolidColorBrush Brush { get; }

    /// <summary>Parses a persisted <c>#AARRGGBB</c>. A malformed row is DROPPED, never guessed at.</summary>
    /// <param name="hex">The persisted spelling.</param>
    /// <param name="swatch">The parsed swatch.</param>
    public static bool TryParse(string? hex, out AnnotationSwatch? swatch)
    {
        swatch = null;
        if (hex is not { Length: 9 } || hex[0] != '#'
                                     || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                         out uint argb))
        {
            return false;
        }

        swatch = new AnnotationSwatch(argb, hex);
        return true;
    }
}

/// <summary>
///     The annotation toolbar's view-model: tool selection, ink style, envelope defaults, undo/redo and
///     the persistence status line.
///     <para>
///         A NESTED view-model hanging off <c>Playback2DTabViewModel.Annotations</c> rather than more
///         properties on a tab class that is already 1,400 lines. It owns no document of its own. The
///         <see cref="AnnotationSessionController" /> does, and this projects it.
///     </para>
/// </summary>
public sealed partial class AnnotationsPanelViewModel : ObservableObject, IDisposable
{
    private readonly AnnotationSessionController _controller;
    private readonly Func<int> _currentTick;

    /// <summary>The active pointer tool.</summary>
    [ObservableProperty]
    private ToolKind _activeTool = ToolKind.PanZoom;

    /// <summary>Whether a stroke started near a player follows them by SteamId.</summary>
    [ObservableProperty]
    private bool _anchorToEntities;

    private bool _applyingFromSession;

    /// <summary>
    ///     Whether the document is written to its sidecar automatically. The user-reachable face of
    ///     <c>AppSettings.Playback2D.AnnotationAutoSave</c>, which previously shipped read-only with no
    ///     UI reaching it.
    /// </summary>
    [ObservableProperty]
    private bool _autoSaveSidecar = true;

    /// <summary>True when the loaded sidecar was authored against a different parse.</summary>
    [ObservableProperty]
    private bool _clockMismatch;

    /// <summary>
    ///     First fully-opaque tick for <see cref="EnvelopeMode.Custom" /> elements, in DV FRAME-CLOCK
    ///     ticks, never CS2 server ticks: the LiveSync servo bends the playhead, so a CS2 anchor drifts
    ///     against what the user was looking at.
    /// </summary>
    [ObservableProperty]
    private int _customFromTick;

    /// <summary>Last fully-opaque tick for <see cref="EnvelopeMode.Custom" /> elements, in DV frame-clock ticks.</summary>
    [ObservableProperty]
    private int _customUntilTick = 320;

    /// <summary>True when a sidecar belonging to a different demo was found and left alone.</summary>
    [ObservableProperty]
    private bool _demoMismatch;

    private bool _disposed;

    /// <summary>How many elements the document holds.</summary>
    [ObservableProperty]
    private int _elementCount;

    // The three ramps are SHARED by Fade and RealTime, and deliberately not duplicated per mode: a
    // RealTime element runs the very same trapezoid, once per section, shifted by the offset that
    // section was drawn at. A second set of "real-time in/out/hold" keys would be a second spelling of
    // these three, with nothing to distinguish them but which mode last wrote them.

    /// <summary>Lead-in ticks for <see cref="EnvelopeMode.Fade" /> and <see cref="EnvelopeMode.RealTime" />.</summary>
    [ObservableProperty]
    private int _fadeInTicks = 8;

    /// <summary>Lead-out ticks for <see cref="EnvelopeMode.Fade" /> and <see cref="EnvelopeMode.RealTime" />.</summary>
    [ObservableProperty]
    private int _fadeOutTicks = 16;

    /// <summary>
    ///     Fully-opaque hold. Per ELEMENT for <see cref="EnvelopeMode.Fade" />, and per SECTION for
    ///     <see cref="EnvelopeMode.RealTime" />: the same number, applied to whatever the mode's
    ///     trapezoid is anchored to.
    /// </summary>
    [ObservableProperty]
    private int _holdTicks = 320;

    /// <summary>Ink colour, as the picker sees it. The LEFT button's pen.</summary>
    [ObservableProperty]
    private Color _inkColor = Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07);

    /// <summary>Ink opacity 0..1.</summary>
    [ObservableProperty]
    private double _inkOpacity = 1;

    /// <summary>Ink width in world units.</summary>
    [ObservableProperty]
    private double _inkWidth = 6;

    // ── Gesture hints ────────────────────────────────────────────────────────────────────────────────
    // Every gesture the toolbar names used to be spelled out in the XAML: "(D)", "Ctrl+Z", "Space to
    // pan". Keys became user-configurable, so each of those went stale the first time anyone rebound
    // one, silently and in the one place a user goes to LEARN the gesture.
    //
    // The profile is PUSHED here by the tab (ApplyKeymap) rather than pulled through a $parent binding:
    // the toolbar binds this panel, and it is also mounted directly in tests. Seeding with the shipped
    // Default means a panel that never received a push still shows the real shipped gestures instead of
    // blanks.

    private Playback2DKeymapProfile _keymap = Playback2DKeymapProfile.Default;

    private int _lastTicksPerSecond;

    private int _recentColorsVersion = -1;

    /// <summary>How many redo entries are available.</summary>
    [ObservableProperty]
    private int _redoDepth;

    /// <summary>
    ///     Whether the right button erases instead of drawing. Off by default: shipping right-erase would
    ///     leave the secondary swatch inert on first run with no hint that a second colour exists at all.
    ///     One click here turns it into the eraser people expect from every other telestration tool.
    /// </summary>
    [ObservableProperty]
    private bool _rightButtonErases;

    /// <summary>The RIGHT button's pen. Shares width and opacity with the primary; only the hue differs.</summary>
    [ObservableProperty]
    private Color _secondaryInkColor = Color.FromUInt32(AnnotationSession.DefaultSecondaryColorArgb);

    /// <summary>One line saying where annotations are saved, or why they are not.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>How many undo entries are available. Drives the button's enabled state.</summary>
    [ObservableProperty]
    private int _undoDepth;

    /// <summary>Envelope authoring mode for new elements.</summary>
    [ObservableProperty]
    private EnvelopeMode _visibility = EnvelopeMode.Always;

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

    /// <summary>The shared session: handed to the host so its layer and tools see the same document.</summary>
    public AnnotationSession Session => _controller.Session;

    /// <summary>The document, for the timeline track and the tests.</summary>
    public AnnotationDocument Document => _controller.Document;

    /// <summary>
    ///     The loaded parse's tick rate, read live off the session: the divisor every DURATION on this
    ///     panel is shown through. Not a panel-owned value and not a preference: the controller writes it
    ///     off the demo's clock on attach, and 64 is only what a session with no demo assumes.
    /// </summary>
    public int TicksPerSecond => Session.TicksPerSecond;

    /// <summary>
    ///     Whether the toolbar should exist: the <c>playback2d.annotations</c> feature is on AND the
    ///     mounted surface can actually host ink. Fails open on both halves.
    ///     <para>
    ///         The gate alone answers "is the user allowed to draw"; it cannot answer "is there anything
    ///         to draw on", and under the legacy renderer the answer to the second is no. Binding the
    ///         toolbar to the gate alone rendered a complete, inert tool row, and selecting a tool in it
    ///         took <c>Space</c> and <c>Esc</c> away from transport and follow, because the keymap's
    ///         tool-scoped rows key off <see cref="IsDrawingToolActive" />, which key off this.
    ///     </para>
    /// </summary>
    public bool IsEnabled => _controller.IsEnabled && IsSurfaceCapable;

    /// <summary>
    ///     Whether the surface the View mounted implements <c>IAnnotationSurface</c>. Defaults to
    ///     <c>true</c> so a headless test, and the window between construction and the View's first
    ///     bind, behaves as it always did; the View narrows it the moment it knows.
    /// </summary>
    public bool IsSurfaceCapable { get; private set; } = true;

    // ── The three durations, in seconds ──────────────────────────────────────────────────────────────
    // STORAGE stays in ticks. The persisted key names and their units are forever, TimeEnvelope is
    // tick-based end to end, and a tick is the clock's own unit. What changed is that a tick is not a
    // unit anyone can reason about: "320" answers "how long does this stay up" only if you also know the
    // parse's rate.
    //
    // TICKS ARE THE SOURCE OF TRUTH on both sides of the pair. The seconds are re-derived from them every
    // time rather than held beside them: two stored values for one quantity, each re-rounded against the
    // other on every panel reload, is exactly the creep the tick value cannot have. Round-to-nearest on
    // the way IN makes the composition idempotent (ticks → seconds → ticks is the same tick for every
    // tick), so the only error a user ever meets is the one-time half-tick their typed value is quantized
    // by, which at 64 tick is 7.8 ms and at 128 is 3.9.
    //
    // from/until are deliberately NOT here. They are absolute POSITIONS in the demo, not durations, and
    // with Round arriving, Custom is the "type an exact window" mode: ticks are the right unit for it.

    /// <summary><see cref="FadeInTicks" /> as seconds. What the toolbar shows and the user types.</summary>
    public double FadeInSeconds
    {
        get => TicksToSeconds(FadeInTicks);
        set => FadeInTicks = SecondsToTicks(value);
    }

    /// <summary><see cref="FadeOutTicks" /> as seconds.</summary>
    public double FadeOutSeconds
    {
        get => TicksToSeconds(FadeOutTicks);
        set => FadeOutTicks = SecondsToTicks(value);
    }

    /// <summary><see cref="HoldTicks" /> as seconds.</summary>
    public double HoldSeconds
    {
        get => TicksToSeconds(HoldTicks);
        set => HoldTicks = SecondsToTicks(value);
    }

    /// <summary>
    ///     Whether a sidecar could be written here at all. See
    ///     <see cref="AnnotationSessionController.CanAutoSave" />; drives the toggle's enabled state.
    /// </summary>
    public bool CanAutoSave => _controller.CanAutoSave;

    /// <summary>
    ///     <see cref="Visibility" /> as a ComboBox index. A plain enum binding would need a converter
    ///     for one short list; the index is the smaller contract.
    ///     <para>
    ///         The getter is the raw cast, so the XAML's item ORDER is the enum's declaration order and a
    ///         new member has to be appended in both places at once. Anything the setter does not
    ///         recognise is <see cref="EnvelopeMode.Always" />, which is the mode that cannot surprise
    ///         anyone.
    ///     </para>
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
                3 => EnvelopeMode.RealTime,
                4 => EnvelopeMode.Round,
                _ => EnvelopeMode.Always
            };

            Visibility = mode;
        }
    }

    /// <summary>
    ///     Recently used ink colours, newest first. Rebuilt from the controller only when its version
    ///     moves, so a stroke that reuses the current colour does not churn the strip's bindings.
    /// </summary>
    public ObservableCollection<AnnotationSwatch> RecentColors { get; } = [];

    /// <summary>Whether there is anything in the swatch strip. Hides the divider with it.</summary>
    public bool HasRecentColors => RecentColors.Count > 0;

    /// <summary>
    ///     Whether the envelope editor is offered at all. Hidden for <see cref="EnvelopeMode.Always" />,
    ///     which is the shipped default: a user who never leaves Always must not pay for a row of spin
    ///     boxes that can only ever say "not applicable" in a toolbar that already reflows at 820 px.
    ///     <para>
    ///         Stated as "not Always" rather than as a list of the modes that want it: every mode but
    ///         Always composes a trapezoid, so every one of them has an <c>in</c> and an <c>out</c> to
    ///         edit, and a list would be a second place a new mode has to be remembered in.
    ///     </para>
    /// </summary>
    public bool IsEnvelopeEditorVisible => Visibility != EnvelopeMode.Always;

    /// <summary>True for <see cref="EnvelopeMode.Fade" />: the hold is relative to the playhead.</summary>
    public bool IsFadeEnvelope => Visibility == EnvelopeMode.Fade;

    /// <summary>True for <see cref="EnvelopeMode.Custom" />: the window is typed in absolute ticks.</summary>
    public bool IsCustomEnvelope => Visibility == EnvelopeMode.Custom;

    /// <summary>True for <see cref="EnvelopeMode.RealTime" />: the stroke replays at its draw cadence.</summary>
    public bool IsRealTimeEnvelope => Visibility == EnvelopeMode.RealTime;

    /// <summary>
    ///     Whether the <c>hold</c> spinner is offered: <see cref="EnvelopeMode.Fade" /> and
    ///     <see cref="EnvelopeMode.RealTime" />, the two modes whose window is relative to a moment
    ///     rather than typed in absolute ticks.
    /// </summary>
    /// <remarks>
    ///     A separate bool from <see cref="IsFadeEnvelope" />: that one answers "which mode is this",
    ///     which the per-mode spinner groups key off, and this one answers "does this control belong on
    ///     the row". RealTime wants a hold because each section replays its own trapezoid shifted by its
    ///     draw offset, so the hold decides whether the stroke shows whole and dissolves, or chases its
    ///     own tail. <see cref="EnvelopeMode.Round" /> does not want it: its window is the round itself, so
    ///     a hold would be a second, contradictory answer to "how long".
    /// </remarks>
    public bool IsHoldEnvelope => Visibility is EnvelopeMode.Fade or EnvelopeMode.RealTime;

    /// <summary>True while a drawing tool owns the surface: what shadows Space and Esc in the keymap.</summary>
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

    /// <summary>Draw-tool tooltip, naming the live draw / hold-pan / cancel gestures.</summary>
    public string DrawToolTip =>
        $"Draw{Gesture(Playback2DAction.ToolDraw)} — right-drag for the second pen, middle- or "
        + $"Ctrl-drag to pan{Held(Playback2DAction.HoldPan)}{Cancel(Playback2DAction.CancelGesture)}";

    /// <summary>Erase-tool tooltip, naming the live erase gesture.</summary>
    public string EraseToolTip =>
        $"Erase whole strokes{Gesture(Playback2DAction.ToolErase)} — middle- or Ctrl-drag to pan";

    /// <summary>Undo tooltip, naming the live undo gesture.</summary>
    public string UndoToolTip => $"Undo{Gesture(Playback2DAction.Undo)}";

    /// <summary>Redo tooltip, naming the live redo gesture.</summary>
    public string RedoToolTip => $"Redo{Gesture(Playback2DAction.Redo)}";

    /// <summary>Clear-all tooltip, naming the live clear gesture.</summary>
    public string ClearAllToolTip =>
        $"Clear every annotation{Gesture(Playback2DAction.ClearAnnotations)} — one undo entry";

    /// <summary>The ink colour as <c>#AARRGGBB</c>, for the swatch tooltip.</summary>
    public string InkColorHex => "#" + ToArgb(InkColor).ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The right button's ink as <c>#AARRGGBB</c>, for its tooltip.</summary>
    public string SecondaryInkColorHex =>
        "#" + ToArgb(SecondaryInkColor).ToString("X8", CultureInfo.InvariantCulture);

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

    /// <summary>
    ///     Told by the View which surface got mounted. Idempotent, and re-raises everything the tool
    ///     state feeds: a capability that arrives after a tool was already selected has to put the tool
    ///     back, or the keymap keeps shadowing Space over a surface that cannot pan.
    /// </summary>
    /// <param name="capable">Whether the mounted surface can host annotations.</param>
    internal void SetSurfaceCapability(bool capable)
    {
        if (IsSurfaceCapable == capable)
        {
            return;
        }

        IsSurfaceCapable = capable;

        if (!capable)
        {
            // A tool left selected over an incapable surface is exactly the state that makes
            // IsDrawingToolActive true with nothing able to service the gesture.
            SelectTool(ToolKind.PanZoom);
        }

        OnPropertyChanged(nameof(IsSurfaceCapable));
        OnPropertyChanged(nameof(IsEnabled));
    }

    /// <summary>Raised when the user picks a tool; the view drives the host's router from it.</summary>
    public event Action<ToolKind>? ToolSelected;

    private double TicksToSeconds(int ticks) => ticks / (double)TicksPerSecond;

    // Clamped rather than cast blind: a NumericUpDown bound past int range would otherwise wrap to
    // int.MinValue and hand the envelope a negative duration.
    private int SecondsToTicks(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }

        double ticks = Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
        return ticks >= int.MaxValue ? int.MaxValue : (int)ticks;
    }

    /// <summary>Re-aims the toolbar's gesture hints at a freshly resolved keymap.</summary>
    /// <param name="keymap">The tab's resolved profile: the shipped table with the user's overrides on top.</param>
    public void ApplyKeymap(Playback2DKeymapProfile keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);

        _keymap = keymap;
        OnPropertyChanged(nameof(DrawToolTip));
        OnPropertyChanged(nameof(EraseToolTip));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        OnPropertyChanged(nameof(ClearAllToolTip));
    }

    // An unbound action yields "" from the profile, and " ()" reads as a bug. Every hint therefore
    // carries its own punctuation and disappears whole rather than leaving an empty bracket behind.
    private string Gesture(Playback2DAction action) =>
        _keymap.GestureText(action) is { Length: > 0 } text ? $" ({text})" : "";

    private string Held(Playback2DAction action) =>
        _keymap.GestureText(action) is { Length: > 0 } text ? $", {text} to pan" : "";

    private string Cancel(Playback2DAction action) =>
        _keymap.GestureText(action) is { Length: > 0 } text ? $", {text} to cancel" : "";

    /// <summary>Selects a tool. Idempotent.</summary>
    /// <param name="kind">The tool to select.</param>
    public void SelectTool(ToolKind kind) => ActiveTool = kind;

    [RelayCommand]
    private void SelectPanZoom() => SelectTool(ToolKind.PanZoom);

    [RelayCommand]
    private void SelectDraw() => SelectTool(ToolKind.Draw);

    [RelayCommand]
    private void SelectErase() => SelectTool(ToolKind.Erase);

    /// <summary>
    ///     Paints a recent colour back onto the PRIMARY pen. The secondary keeps its own picker: a
    ///     click-target that changed a different pen depending on some modifier would be a coin flip.
    /// </summary>
    /// <param name="swatch">The swatch that was clicked.</param>
    [RelayCommand]
    private void ApplyRecentColor(AnnotationSwatch? swatch)
    {
        if (swatch is not null)
        {
            InkColor = Color.FromUInt32(swatch.Argb);
        }
    }

    /// <summary>
    ///     Fills the Custom window from the playhead: the same "I mean here" gesture as <c>Pin to now</c>,
    ///     for the mode whose ticks are absolute and would otherwise have to be read off the timeline and
    ///     typed in by hand.
    /// </summary>
    [RelayCommand]
    private void CustomWindowFromNow()
    {
        int tick = _currentTick();
        int length = Math.Max(0, CustomUntilTick - CustomFromTick);
        CustomFromTick = tick;
        CustomUntilTick = tick + (length > 0 ? length : HoldTicks);
    }

    [RelayCommand]
    private void Undo() => _controller.Document.Undo();

    [RelayCommand]
    private void Redo() => _controller.Document.Redo();

    /// <summary>
    ///     Clears every annotation as ONE undo entry: a mis-clicked "clear all" that could not be undone
    ///     would be the single most destructive button on the surface.
    ///     <para>
    ///         Declined while a gesture is in flight. Ctrl+X is bound with the pointer captured mid-stroke,
    ///         and gestures deliberately do not nest, so opening one here threw
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

    partial void OnSecondaryInkColorChanged(Color value) => PushStyle();

    partial void OnInkWidthChanged(double value) => PushStyle();

    partial void OnInkOpacityChanged(double value) => PushStyle();

    partial void OnRightButtonErasesChanged(bool value)
    {
        Session.SecondaryTool = value ? ToolKind.Erase : null;
        PersistIfUserDriven();
    }

    partial void OnAnchorToEntitiesChanged(bool value)
    {
        Session.AnchorToEntities = value;
        PersistIfUserDriven();
    }

    partial void OnAutoSaveSidecarChanged(bool value)
    {
        _controller.AutoSave = value;

        // The status line names the destination, and with auto-save off that destination is no longer a
        // promise, so the line has to be re-read here rather than waiting for the next document change.
        _controller.RefreshStatus();
        PersistIfUserDriven();
    }

    partial void OnVisibilityChanged(EnvelopeMode value)
    {
        Session.DefaultVisibility = value;
        OnPropertyChanged(nameof(VisibilityIndex));
        OnPropertyChanged(nameof(IsEnvelopeEditorVisible));
        OnPropertyChanged(nameof(IsFadeEnvelope));
        OnPropertyChanged(nameof(IsCustomEnvelope));
        OnPropertyChanged(nameof(IsRealTimeEnvelope));
        OnPropertyChanged(nameof(IsHoldEnvelope));
        PersistIfUserDriven();
    }

    // The two ramps feed BOTH modes: Fade reads them off the session at draw time, Custom bakes them
    // into the template, so a ramp change has to re-compose the template or Custom keeps yesterday's.
    partial void OnFadeInTicksChanged(int value)
    {
        Session.FadeInTicks = Math.Max(0, value);

        // The seconds companion is a projection of this, so it is raised HERE and not from its own
        // setter: a tick value that moved for any other reason (a settings seed, a demo attach that
        // changed the rate) has to re-reach the spinner too.
        OnPropertyChanged(nameof(FadeInSeconds));
        PushCustomWindow();
    }

    partial void OnFadeOutTicksChanged(int value)
    {
        Session.FadeOutTicks = Math.Max(0, value);
        OnPropertyChanged(nameof(FadeOutSeconds));
        PushCustomWindow();
    }

    partial void OnHoldTicksChanged(int value)
    {
        Session.HoldTicks = Math.Max(0, value);
        OnPropertyChanged(nameof(HoldSeconds));
        PersistIfUserDriven();
    }

    partial void OnCustomFromTickChanged(int value) => PushCustomWindow();

    partial void OnCustomUntilTickChanged(int value) => PushCustomWindow();

    partial void OnUndoDepthChanged(int value) => OnPropertyChanged(nameof(CanUndo));

    partial void OnRedoDepthChanged(int value) => OnPropertyChanged(nameof(CanRedo));

    private void PushStyle()
    {
        float width = (float)InkWidth;
        float opacity = (float)Math.Clamp(InkOpacity, 0, 1);

        Session.Style = new AnnotationStyle(ToArgb(InkColor), width, opacity);
        Session.SecondaryStyle = new AnnotationStyle(ToArgb(SecondaryInkColor), width, opacity);

        OnPropertyChanged(nameof(InkColorHex));
        OnPropertyChanged(nameof(SecondaryInkColorHex));

        // NOT where a colour becomes "recent": the picker raises a change on every pointer move through
        // its spectrum. The controller pushes the swatch when a stroke actually commits.
        PersistIfUserDriven();
    }

    // The Custom template is composed here rather than assembled at draw time, so EnvelopeForNewElement
    // stays the pure switch it is and there is exactly ONE place that knows what a window means.
    private void PushCustomWindow()
    {
        Session.SetCustomWindow(CustomFromTick, CustomUntilTick);
        PersistIfUserDriven();
    }

    private void PersistIfUserDriven()
    {
        if (!_applyingFromSession)
        {
            _controller.PersistSettings();
        }
    }

    // Only when the controller says the list moved: RefreshFromController runs on every document change,
    // and rebuilding eight bindings per stroke would be churn with no visible effect.
    private void SyncRecentColors()
    {
        if (_recentColorsVersion == _controller.RecentColorsVersion)
        {
            return;
        }

        _recentColorsVersion = _controller.RecentColorsVersion;
        RecentColors.Clear();
        foreach (string hex in _controller.RecentColors)
        {
            if (AnnotationSwatch.TryParse(hex, out AnnotationSwatch? swatch) && swatch is not null)
            {
                RecentColors.Add(swatch);
            }
        }

        OnPropertyChanged(nameof(HasRecentColors));
    }

    // Seeds the panel from the session the controller built out of settings, without echoing every
    // assignment straight back into a settings write.
    private void PullFromSession()
    {
        _applyingFromSession = true;
        try
        {
            // The template is read FIRST. Assigning the ramps below re-composes it out of the window
            // the panel is still showing, so reading it afterwards hands back what this pull just
            // overwrote: the seeded Custom window silently becoming the previous demo's.
            TimeEnvelope custom = Session.NewElementEnvelope;

            AnnotationStyle style = Session.Style;
            InkColor = FromArgb(style.ColorArgb);
            SecondaryInkColor = FromArgb(Session.SecondaryStyle.ColorArgb);
            RightButtonErases = Session.SecondaryTool == ToolKind.Erase;
            InkWidth = style.WidthWorld;
            InkOpacity = style.Opacity;
            Visibility = Session.DefaultVisibility;
            FadeInTicks = Session.FadeInTicks;
            FadeOutTicks = Session.FadeOutTicks;
            HoldTicks = Session.HoldTicks;

            // The template is the truth the renderer reads, so the boxes show what it says, including
            // the clamp PinnedTo applied to an inverted window somebody hand-edited into settings.
            CustomFromTick = custom.FromTick ?? 0;
            CustomUntilTick = custom.UntilTick ?? 0;

            AnchorToEntities = Session.AnchorToEntities;
            ActiveTool = Session.ActiveTool;

            // Off the CONTROLLER, not the session: the session knows nothing about files. ApplySettings
            // has just re-seeded it from the persisted key, so this is that value.
            AutoSaveSidecar = _controller.AutoSave;

            // Stamped once more from what the panel now shows: an assignment that changed nothing raises
            // nothing, so without this a pull whose window matched the panel's would leave the session
            // holding whatever the ramp assignments above composed on the way past.
            Session.SetCustomWindow(CustomFromTick, CustomUntilTick);
        }
        finally
        {
            _applyingFromSession = false;
        }

        SyncTickRate();
    }

    // Only when it moved, on the same discipline as SyncRecentColors: both of this method's callers run
    // on every document change, and the rate changes once per demo.
    //
    // It has to be watched at all because it is the ONE thing on this panel a demo attach changes with no
    // tick moving anywhere: a 128-tick parse replacing a 64-tick one leaves every stored duration exactly
    // where it was and every DISPLAYED one wrong, so no [ObservableProperty] setter would fire.
    private void SyncTickRate()
    {
        if (_lastTicksPerSecond == Session.TicksPerSecond)
        {
            return;
        }

        _lastTicksPerSecond = Session.TicksPerSecond;
        OnPropertyChanged(nameof(TicksPerSecond));
        OnPropertyChanged(nameof(FadeInSeconds));
        OnPropertyChanged(nameof(FadeOutSeconds));
        OnPropertyChanged(nameof(HoldSeconds));
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
        SyncRecentColors();

        // A demo attach arrives here, and an attach is where the parse's tick rate (hence every duration
        // this panel displays) can change.
        SyncTickRate();
        OnPropertyChanged(nameof(IsEnabled));

        // Whether a sidecar is possible changes on every attach/detach and on a gate flip, and the
        // toggle is only meaningful when one is.
        OnPropertyChanged(nameof(CanAutoSave));
    }

    /// <summary>Re-reads settings into the session and re-seeds the panel. After a demo attach.</summary>
    public void Resync()
    {
        PullFromSession();
        RefreshFromController();
    }

    private static uint ToArgb(Color color) =>
        (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    private static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
