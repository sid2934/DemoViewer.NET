using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Tools;

namespace DemoViewer.NET.Extensions.Sdk.Playback;

/// <summary>
///     One 2D Playback tab, as a contribution sees it. Every <c>Add</c> returns a handle; disposing it removes
///     what was added. Call from the UI thread.
/// </summary>
public interface IPlaybackSurface
{
    /// <summary>The mounted map's floors, lowest first; empty before a map is shown.</summary>
    IReadOnlyList<PlaybackLevel> Levels { get; }

    /// <summary>
    ///     The named places of the mounted map, distinct and in ordinal order; empty when the map has no zones
    ///     or no map is shown. The names are the ones <see cref="PlaceAt" /> answers with.
    /// </summary>
    IReadOnlyList<string> Places { get; }

    /// <summary>
    ///     The named place at a world point on one floor: a place volume on that floor, else the nearest area
    ///     of that floor within snapping distance. Null where the map has no zones, nothing is near, no floor
    ///     is shown, or <paramref name="level" /> names no floor in <see cref="Levels" />. Cheap enough to call
    ///     per pointer move; the map's zones file is read once, on the first call.
    /// </summary>
    /// <param name="level">
    ///     A <see cref="PlaybackLevel.Name" />, or null for the map's only floor. Null on a map with several
    ///     floors answers null, since the point is ambiguous.
    /// </param>
    /// <param name="worldX">World X.</param>
    /// <param name="worldY">World Y.</param>
    string? PlaceAt(string? level, double worldX, double worldY);

    /// <summary>
    ///     The ids of every action the keymap knows: the tab's own and every extension's commands. What
    ///     <see cref="GestureHint" /> and <see cref="AddActionHandler" /> name.
    /// </summary>
    IReadOnlyCollection<string> ActionIds { get; }

    /// <summary>Raised after the user rebinds a key. Refresh labels that show gestures.</summary>
    event Action? KeymapChanged;

    /// <summary>Raised when the tab is left, before it saves its state.</summary>
    event Action? Deactivated;

    /// <summary>Calls <paramref name="handler" /> when a demo is opened or reloaded in the tab.</summary>
    IDisposable OnDemoChanged(Action handler);

    /// <summary>Calls <paramref name="handler" /> with the tick on every playhead move.</summary>
    IDisposable OnPlayheadChanged(Action<int> handler);

    /// <summary>The bound gesture for an action as <c>" (Ctrl+F)"</c>, or an empty string when unbound.</summary>
    string GestureHint(string actionId);

    /// <summary>
    ///     The action a key is bound to in <paramref name="scope" /> under the user's keymap, or null. For a
    ///     key handler resolving the extension's own focus scope while its panel has focus.
    /// </summary>
    /// <param name="scope">A <see cref="CommandScope.Id" />, or <c>"playback2d"</c> or <c>"playback2d.tool"</c>.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers held.</param>
    string? ActionFor(string scope, Key key, KeyModifiers modifiers);

    /// <summary>A lane under the rounds row, drawn from <paramref name="track" />'s bands.</summary>
    ILaneHandle AddLane(ITimelineTrack track, ILaneBehaviour? behaviour = null);

    /// <summary>Entries for the right-click menu of any timeline band.</summary>
    IDisposable AddBandMenu(Func<PlaybackBand, IEnumerable<MenuEntry>> items);

    /// <summary>A mode the toolbar shows as a toggle and its action flips.</summary>
    IDisposable AddModeToggle(ModeToggle toggle);

    /// <summary>A toolbar button, also listed in the toolbar's overflow menu.</summary>
    IDisposable AddToolbarItem(ToolbarItem item);

    /// <summary>
    ///     Sees a primary press on the map before the drawing tools do. Return true to take it. Not called
    ///     for a press the tab turns into a pan.
    /// </summary>
    IDisposable AddPointerPreHandler(Func<PlaybackPointer, bool> handler);

    /// <summary>A pane. <see cref="PanePlacement.Side" /> panes show one at a time.</summary>
    /// <param name="where">Where it docks.</param>
    /// <param name="order">Position among panes there.</param>
    /// <param name="viewModel">Builds its view model, an <see cref="IExtensionViewModel" />; the view is found by the <c>ViewModel</c> to <c>View</c> naming rule.</param>
    IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel);

    /// <summary>A right-column panel. Several can be open at once.</summary>
    /// <param name="order">Position in the column.</param>
    /// <param name="viewModel">Builds its view model.</param>
    /// <param name="view">Builds its view; null for the naming rule, which needs an <see cref="IExtensionViewModel" />.</param>
    /// <param name="featureId">Shown only while this feature is on.</param>
    /// <param name="mode">Shown only while this mode is on.</param>
    IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null,
        ModeToggle? mode = null);

    /// <summary>
    ///     A scene layer drawn on the tab's map, among the tab's own layers by <see cref="ISceneLayer.Slot" /> and
    ///     <see cref="ISceneLayer.Order" />. <paramref name="layer" /> builds a fresh instance whenever the map
    ///     builds its scene, so it may be called more than once.
    ///     <para>
    ///         The layer sees only what it is handed: the frame in <see cref="ISceneLayer.Advance" />, and the
    ///         canvas and <see cref="SceneRenderContext" /> in <see cref="ISceneLayer.Render" />, which runs on
    ///         the render thread. The canvas is restored after each call. A layer that throws stops drawing for
    ///         the session and counts against the extension.
    ///     </para>
    /// </summary>
    /// <param name="id">
    ///     The layer's id within the extension: letters, digits, '.', '-' and '_'. The host files it as
    ///     <c>ext.&lt;extension id&gt;.&lt;id&gt;</c>, so it can never collide with the tab's own layers or another
    ///     extension's; <see cref="ISceneLayer.Id" /> of the built layer is not used. Adding an id again replaces it.
    /// </param>
    /// <param name="layer">Builds the layer.</param>
    IDisposable AddLayer(string id, Func<ISceneLayer> layer);

    /// <summary>
    ///     A pointer tool on the tab's map. It is offered every primary press the tab does not turn into a pan
    ///     (Space, Control and the middle button pan), after the pointer pre-handlers and before the drawing
    ///     tools; a tool that takes the press owns the gesture until the release.
    /// </summary>
    /// <param name="tool">The tool.</param>
    IDisposable AddTool(IMapTool tool);

    /// <summary>Sees a key before the tab's keymap. Return true to take it.</summary>
    IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler);

    /// <summary>Sees an action the tab did not handle, by id. Return true to take it.</summary>
    IDisposable AddActionHandler(Func<string, bool> handler);
}

/// <summary>A floor of a multi-level map.</summary>
/// <param name="Name">The floor's name.</param>
/// <param name="ZMin">Lowest world Z on the floor.</param>
/// <param name="ZMax">Highest world Z on the floor.</param>
public sealed record PlaybackLevel(string Name, double ZMin, double ZMax);

/// <summary>A band on the timeline.</summary>
/// <param name="TrackId">The track that drew it. Round bands come from the <c>"round"</c> track.</param>
/// <param name="StartFrameIndex">Its first frame.</param>
/// <param name="EndFrameIndex">Its last frame.</param>
/// <param name="Label">Its label; a round band's is the round number.</param>
/// <param name="Tooltip">Its tooltip.</param>
public sealed record PlaybackBand(string TrackId, int StartFrameIndex, int EndFrameIndex, string Label, string Tooltip)
{
    /// <summary>True for a band the rounds track drew for a round.</summary>
    public bool IsRound => TrackId == "round" && int.TryParse(Label, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out _);
}

/// <summary>A context-menu entry.</summary>
/// <param name="Header">The entry text.</param>
/// <param name="Run">What it does.</param>
public sealed record MenuEntry(string Header, Action Run);

/// <summary>A press on the map.</summary>
/// <param name="Level">The floor shown, or null on a single-level map.</param>
/// <param name="WorldX">World X under the pointer.</param>
/// <param name="WorldY">World Y under the pointer.</param>
/// <param name="ScreenX">Pointer X in the view, in device-independent pixels.</param>
/// <param name="ScreenY">Pointer Y in the view.</param>
/// <param name="Modifiers">Keys held.</param>
/// <param name="Tick">The tick shown.</param>
/// <param name="PlaceAt">The map's named place at the press, or null where the map has no zones. Read it only if you need it.</param>
/// <param name="Frame">The frame on screen. Valid only during the call: the tab refills it for the next one.</param>
public sealed record PlaybackPointer(
    string? Level, double WorldX, double WorldY, double ScreenX, double ScreenY, KeyModifiers Modifiers, int Tick,
    Func<string?> PlaceAt, Scene2DFrame Frame);

/// <summary>The moment a toolbar item ran at.</summary>
/// <param name="Tick">The tick shown.</param>
/// <param name="FrameIndex">The frame shown.</param>
/// <param name="Frame">The frame on screen. Valid only during the call: the tab refills it for the next one.</param>
public sealed record PlaybackMoment(int Tick, int FrameIndex, Scene2DFrame Frame);

/// <summary>Where a pane docks.</summary>
public enum PanePlacement
{
    /// <summary>Beside the map, one pane at a time; opening one closes the others and the export pane.</summary>
    Side,

    /// <summary>The right column, as a panel.</summary>
    RightColumn
}

/// <summary>A pane. Dispose removes it; <see cref="Close" /> only hides it.</summary>
public interface IPaneHandle : IDisposable
{
    /// <summary>True while open.</summary>
    bool IsOpen { get; }

    /// <summary>Opens it with a fresh view model.</summary>
    void Open();

    /// <summary>Closes it and disposes a disposable view model.</summary>
    void Close();

    /// <summary>Raised after it closes.</summary>
    event Action? Closed;
}

/// <summary>A right-column panel.</summary>
public interface IPanelHandle : IPaneHandle
{
    /// <summary>Open, its feature on, its mode on, and the column showing panels.</summary>
    bool IsShown { get; }

    /// <summary>Raised when <see cref="IsShown" /> changes.</summary>
    event Action? ShownChanged;

    /// <summary>Set while the panel has keyboard focus, so the tab routes its actions here first.</summary>
    bool HasKeyboard { get; set; }
}
