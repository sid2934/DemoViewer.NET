#region

using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>Where a pane sits in the 2D Playback tab.</summary>
public enum PanePlacement
{
    /// <summary>The side pane over the viewport's right edge, the export pane's place. One open at a time.</summary>
    Side,

    /// <summary>The right column under the player cards. Not hosted yet (item 17).</summary>
    RightColumn
}

/// <summary>A pane a contribution added. Opening builds the view model from the factory; closing disposes it.</summary>
public interface IPaneHandle : IDisposable
{
    /// <summary>True while the pane shows.</summary>
    bool IsOpen { get; }

    /// <summary>Shows the pane with a fresh view model. Open already: the pane is rebuilt. Another pane at the same placement closes.</summary>
    void Open();

    /// <summary>Hides the pane and disposes its view model. No-op when closed.</summary>
    void Close();

    /// <summary>The pane closed, by <see cref="Close" />, by the host's Close button or because another pane took its place.</summary>
    event Action? Closed;
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

    /// <summary>
    ///     A right-click menu contributor for timeline bands. Asked for every band pressed; returns no
    ///     entries for bands it has nothing for. Entries are shown in contributor order after the tab's own.
    /// </summary>
    /// <returns>Removes the contributor.</returns>
    IDisposable AddBandMenu(Func<TimelineBandViewModel, IEnumerable<MenuEntry>> items);

    /// <summary>
    ///     A pane the contribution opens and closes through the handle. The view model's view comes from
    ///     the ViewLocator convention, so it must derive from <c>ViewModelBase</c> and have a <c>…View</c>.
    /// </summary>
    /// <param name="where">Which host shows it. Only <see cref="PanePlacement.Side" /> is hosted today.</param>
    /// <param name="order">Among panes at the same placement, lower first.</param>
    /// <param name="viewModel">Builds the view model on every <see cref="IPaneHandle.Open" />; disposed on close when it is <see cref="IDisposable" />.</param>
    IPaneHandle AddPane(PanePlacement where, int order, Func<object> viewModel);
}
