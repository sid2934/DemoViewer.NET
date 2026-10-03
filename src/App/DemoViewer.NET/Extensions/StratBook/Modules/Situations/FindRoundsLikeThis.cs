#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The seam the 2D Playback tab hands its current tick through on <c>Ctrl+F</c>. The playback tab
///     is built by a bare <c>new()</c> and references no other module, so it resolves this from the
///     container and a test hands it a fake; the shipped implementation is below.
/// </summary>
public interface IFindRoundsLikeThis
{
    /// <summary>
    ///     Snapshots the alive players onto the Query Canvas and switches to the Situations tab.
    ///     Returns false when nothing could be shown: nobody alive on either side, or no Situations tab
    ///     on this host (the feature is gated off), so the caller leaves the key unhandled.
    /// </summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="time">The scene time the markers were built for.</param>
    /// <param name="markers">The scene's markers at that time.</param>
    bool Show(string map, SceneTime time, IReadOnlyList<PlayerMarker> markers);
}

/// <summary>
///     The shipped seam: the snapshot is minted through the place source in force for the map (so the
///     zones mode resolves the marker positions and the default reads the pawn's place), loaded onto
///     the Situations tab's canvas, and the shell is asked to show that tab.
///     <para>
///         The tab VM is resolved lazily: it is the container singleton the module's own factory
///         returns, so the canvas the key fills is the canvas the tab shows, whether or not the tab
///         has ever been opened. The tab switch goes first, because a host whose gate hides the tab
///         has nowhere to show the query, and loading it anyway would leave a stale snapshot for the
///         next time the tab is turned on.
///     </para>
/// </summary>
public sealed class FindRoundsLikeThis : IFindRoundsLikeThis
{
    /// <summary>The Situations tab's persisted id, the one <c>SituationsModule</c> contributes.</summary>
    public const string TabId = "situations.search";

    private readonly Func<string, bool> _selectTab;
    private readonly RoundIndexPlaceSources _sources;
    private readonly Func<SituationsTabViewModel> _tab;

    /// <param name="tab">The Situations tab VM, resolved on first use.</param>
    /// <param name="sources">The place source in force per map, the one the index builder mints through.</param>
    /// <param name="selectTab">Shows a workspace tab by id; false when no such tab is on the strip.</param>
    public FindRoundsLikeThis(Func<SituationsTabViewModel> tab, RoundIndexPlaceSources sources,
        Func<string, bool> selectTab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(selectTab);
        _tab = tab;
        _sources = sources;
        _selectTab = selectTab;
    }

    /// <inheritdoc />
    public bool Show(string map, SceneTime time, IReadOnlyList<PlayerMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(markers);

        SituationSnapshot snapshot = SituationSnapshot.Capture(map, time, markers, _sources.SourceFor(map));
        if (snapshot.IsEmpty || !_selectTab(TabId))
        {
            return false;
        }

        _tab().Canvas.LoadSnapshot(snapshot);
        return true;
    }
}
