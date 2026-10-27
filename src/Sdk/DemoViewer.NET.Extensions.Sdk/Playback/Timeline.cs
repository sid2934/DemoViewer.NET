using DemoViewer.NET.Playback2D.Timeline;

namespace DemoViewer.NET.Extensions.Sdk.Playback;

/// <summary>What a lane does with presses.</summary>
public interface ILaneBehaviour
{
    /// <summary>A band was pressed, before the playhead seeks to it.</summary>
    void OnBandPressed(PlaybackBand band)
    {
    }

    /// <summary>Entries for a band's right-click menu, before the other contributors'.</summary>
    IEnumerable<MenuEntry> MenuFor(PlaybackBand band) => Array.Empty<MenuEntry>();

    /// <summary>An empty part of an editable lane was pressed at <paramref name="frame" />.</summary>
    void OnLabelRequested(int frame)
    {
    }

    /// <summary>A handle of <see cref="ILaneHandle.EditSpan" /> moved.</summary>
    void OnEditSpanDragged(int startFrame, int endFrame)
    {
    }
}

/// <summary>A lane. Dispose removes it.</summary>
public interface ILaneHandle : IDisposable
{
    /// <summary>The track it draws, as handed to <see cref="IPlaybackSurface.AddLane" />.</summary>
    ITimelineTrack Track { get; }

    /// <summary>
    ///     The id the host filed the lane under, <c>ext.&lt;extension id&gt;.&lt;track id&gt;</c>, which every band
    ///     and mark of the lane carries as <see cref="PlaybackBand.TrackId" />. <see cref="Track" />'s own
    ///     <see cref="ITimelineTrack.Id" /> is the unfiled id within the extension.
    /// </summary>
    string TrackId { get; }

    /// <summary>Hidden for a mode, without touching the user's own track toggle.</summary>
    bool IsSuppressed { get; set; }

    /// <summary>The lane shows even when empty, and pressing it asks for a label.</summary>
    bool IsEditable { get; set; }

    /// <summary>
    ///     True while a press on the empty lane row reaches this lane. The row is shared, and only the
    ///     first lane that takes edits and carries a behaviour is asked, so a second editable lane can say
    ///     it is not the one taking labels instead of waiting for a press that never comes. Pull only:
    ///     the answer turns over with any lane's <see cref="IsEditable" /> and with every lane added or
    ///     disposed, and nothing raises an event for it. A handle with no host behind it answers
    ///     <see cref="IsEditable" />, the host's own answer for a lone lane that has a behaviour.
    /// </summary>
    bool OwnsLabelPresses => IsEditable;

    /// <summary>A span drawn with two drag handles; one lane's at a time.</summary>
    (int Start, int End)? EditSpan { get; set; }
}
