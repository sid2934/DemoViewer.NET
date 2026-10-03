#region

using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     What a lane does when the user touches it. The timeline dispatches to the lane whose track made the
///     band, so a behaviour never filters by track id. A lane registered without one is display only.
/// </summary>
public interface ILaneBehaviour
{
    /// <summary>A left press on one of the lane's bands, before the seek to the band's first frame.</summary>
    /// <param name="band">The band pressed.</param>
    /// <param name="data">The data the band was built from.</param>
    void OnBandPressed(TimelineBandViewModel band, ITimelineData data);

    /// <summary>The right-click entries for one of the lane's bands, shown before the surface's band-menu contributors'. Empty for none.</summary>
    /// <param name="band">The band right-clicked.</param>
    /// <param name="data">The data the band was built from.</param>
    IEnumerable<MenuEntry> MenuFor(TimelineBandViewModel band, ITimelineData data);

    /// <summary>A left click on the lane row where no band is, while the lane <see cref="ILaneHandle.IsEditable" />.</summary>
    /// <param name="frame">The frame under the click.</param>
    void OnLabelRequested(int frame);

    /// <summary>A handle of the lane's <see cref="ILaneHandle.EditSpan" /> dragged to new frames. The span is already moved.</summary>
    /// <param name="startFrame">The span's first frame.</param>
    /// <param name="endFrame">The span's last frame.</param>
    void OnEditSpanDragged(int startFrame, int endFrame);
}

/// <summary>
///     A lane a contribution added to the timeline. The state here is the lane's own: hiding it for a mode,
///     taking edits, and the span drawn with two handles. Disposing unregisters the track and its behaviour
///     and clears all three.
/// </summary>
public interface ILaneHandle : IDisposable
{
    /// <summary>The registered track.</summary>
    ITimelineTrack Track { get; }

    /// <summary>Hidden for a mode of the owner, without touching the user's own toggle for the track.</summary>
    bool IsSuppressed { get; set; }

    /// <summary>Takes edits: the lane row shows even with nothing on it, and a click on empty lane asks for a label.</summary>
    bool IsEditable { get; set; }

    /// <summary>The span drawn over the lane with its two handles, as frames, or null for none. One lane's span shows at a time.</summary>
    (int Start, int End)? EditSpan { get; set; }
}
