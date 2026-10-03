#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>Where a pane sits in the 2D Playback tab.</summary>
public enum PanePlacement
{
    /// <summary>The side pane over the viewport's right edge, the export pane's place. One open at a time.</summary>
    Side,

    /// <summary>
    ///     The right column under the player cards. Several panels show at once, in order; see
    ///     <see cref="IPlaybackSurface.AddPanel" /> and <see cref="IPanelHandle" />.
    /// </summary>
    RightColumn
}

/// <summary>A pane a contribution added. Opening builds the view model from the factory; closing disposes it.</summary>
public interface IPaneHandle : IDisposable
{
    /// <summary>True while the pane shows.</summary>
    bool IsOpen { get; }

    /// <summary>
    ///     Shows the pane with a fresh view model. For a side pane, open already means rebuilt and the other
    ///     side pane closes; for a right-column panel, open already is a no-op and the others stay.
    /// </summary>
    void Open();

    /// <summary>Hides the pane and disposes its view model. No-op when closed.</summary>
    void Close();

    /// <summary>The pane closed, by <see cref="Close" />, by the host's Close button or because another pane took its place.</summary>
    event Action? Closed;
}

/// <summary>
///     A right-column panel a contribution added. Several panels are open at once and show in order, each
///     while its gate is on and the column shows contributed panels (Review mode until item 18 makes the
///     mode the pack's). A panel can hold the keyboard (<see cref="HasKeyboard" />): its focus scope, under
///     which the contribution's key handlers run before the tab's keymap and its action handlers see every
///     action first.
/// </summary>
public interface IPanelHandle : IPaneHandle
{
    /// <summary>Open, gate on and the column showing contributed panels. What the user sees.</summary>
    bool IsShown { get; }

    /// <summary><see cref="IsShown" /> changed: the gate, the mode or the panel's own open state moved.</summary>
    event Action? ShownChanged;

    /// <summary>
    ///     The panel's focus scope. The contribution sets it from its own focus state; the host reads it only
    ///     while the panel <see cref="IsShown" />.
    /// </summary>
    bool HasKeyboard { get; set; }
}

/// <summary>
///     The 2D Playback tab as a contribution sees it (design §7.3). Each tab view-model owns one; a
///     pack's <see cref="IPlaybackContribution" /> adds to it on attach and removes on detach. Registrations
///     are disposable, so a contribution undoes exactly what it added.
/// </summary>
public interface IPlaybackSurface
{
    /// <summary>
    ///     The mounted viewport's map levels, for a contribution that keys a world Z to a floor. Empty when
    ///     no surface with levels is mounted (the legacy viewport, a headless test).
    /// </summary>
    IReadOnlyList<MapLevel> MapLevels { get; }

    /// <summary>The tab's resolved keymap: the shipped table under the user's overrides. Replaced whole on a rebind.</summary>
    Playback2DKeymapProfile Keymap { get; }

    /// <summary><see cref="Keymap" /> was replaced.</summary>
    event Action? KeymapChanged;

    /// <summary>
    ///     The tab deactivated (another tab took the shell, or shutdown). Raised before the tab flushes its
    ///     documents, so a contribution holding transient keyboard state gives it up and writes what it was
    ///     making. Contributions stay attached across deactivation.
    /// </summary>
    event Action? Deactivated;

    /// <summary>
    ///     Review mode, the tab's. The right column shows contributed panels only while it is on. Temporary:
    ///     item 18 makes the mode a pack-owned toggle the timeline exposes, and this leaves with it.
    /// </summary>
    bool IsReviewMode { get; }

    /// <summary><see cref="IsReviewMode" /> flipped. Temporary with it (item 18).</summary>
    event Action? ReviewModeChanged;

    /// <summary>
    ///     The tab's timeline, for a contribution whose lanes the tab still registers: the lane's edit span,
    ///     its label and drag events, the registered tracks. Temporary: item 18 replaces this with lane
    ///     contributions that carry their own behaviour.
    /// </summary>
    Playback2DTimelineViewModel Timeline { get; }

    /// <summary>
    ///     The frame on screen, for a contribution that resolves a map click against it. Temporary: item 20
    ///     moves Click To Tag behind a pointer pre-handler over a scene pointer.
    /// </summary>
    Scene2DFrame CurrentFrame { get; }

    /// <summary>The open map's zones, or null without any. Temporary with <see cref="CurrentFrame" /> (item 20).</summary>
    PlaceResolver? Zones { get; }

    /// <summary>
    ///     A right-click menu contributor for timeline bands. Asked for every band pressed; returns no
    ///     entries for bands it has nothing for. Entries are shown in contributor order after the tab's own.
    /// </summary>
    /// <returns>Removes the contributor.</returns>
    IDisposable AddBandMenu(Func<TimelineBandViewModel, IEnumerable<MenuEntry>> items);

    /// <summary>
    ///     A pane the contribution opens and closes through the handle. The view model's view comes from
    ///     the ViewLocator convention, so it must derive from <c>ViewModelBase</c> and have a <c>…View</c>.
    ///     For <see cref="PanePlacement.RightColumn" /> this is <see cref="AddPanel" /> with no gate and
    ///     the located view, and the handle is an <see cref="IPanelHandle" />.
    /// </summary>
    /// <param name="where">Which host shows it.</param>
    /// <param name="order">Among panes at the same placement, lower first.</param>
    /// <param name="viewModel">Builds the view model on every <see cref="IPaneHandle.Open" />; disposed on close when it is <see cref="IDisposable" />.</param>
    IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel);

    /// <summary>
    ///     A right-column panel. The column shows every open panel whose gate is on, lowest order first,
    ///     while it shows contributed panels at all.
    /// </summary>
    /// <param name="order">Among right-column panels, lower first.</param>
    /// <param name="viewModel">Builds the view model on <see cref="IPaneHandle.Open" />; disposed on close when it is <see cref="IDisposable" />.</param>
    /// <param name="view">
    ///     Builds the panel's control; the host sets its DataContext to the view model. Null takes the
    ///     ViewLocator convention, which needs a <c>ViewModelBase</c> with a <c>…View</c>.
    /// </param>
    /// <param name="featureId">The gate the panel shows under, read through the tab's features; null for the owning pack's alone.</param>
    IPanelHandle AddPanel(int order, Func<object> viewModel, Func<Control>? view = null, string? featureId = null);

    /// <summary>
    ///     A key handler asked before the tab's keymap, in registration order, for every key the view gets
    ///     while no text input has focus. True consumes the key. This is how a focus-scoped keymap row
    ///     (<see cref="Playback2DBindingScope.WhenPaletteFocused" />, <see cref="Playback2DBindingScope.WhenSuggestionSelected" />)
    ///     shadows the tab's own; the handler resolves the scope itself against <see cref="Keymap" />.
    /// </summary>
    /// <returns>Removes the handler.</returns>
    IDisposable AddKeyHandler(Func<Key, KeyModifiers, bool> handler);

    /// <summary>
    ///     An action handler for the tab's keymap actions. Asked for every action the tab does not handle
    ///     itself, and, while a shown panel <see cref="IPanelHandle.HasKeyboard" />, for every action before
    ///     the tab's own (undo and redo are the focused document's). True consumes the action.
    /// </summary>
    /// <returns>Removes the handler.</returns>
    IDisposable AddActionHandler(Func<Playback2DAction, bool> handler);

    /// <summary>
    ///     A left click on the map, before the pointer tools see it: the clicked pane's floor and the world
    ///     point. True takes the click. Temporary: item 20 replaces this with a pointer pre-handler
    ///     contribution over a scene pointer.
    /// </summary>
    /// <returns>Removes the handler.</returns>
    IDisposable AddMapClickHandler(Func<MapLevel, double, double, bool> handler);
}
