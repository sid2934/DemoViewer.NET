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

    /// <summary>Hidden for a mode, without touching the user's own track toggle.</summary>
    bool IsSuppressed { get; set; }

    /// <summary>The lane shows even when empty, and pressing it asks for a label.</summary>
    bool IsEditable { get; set; }

    /// <summary>A span drawn with two drag handles; one lane's at a time.</summary>
    (int Start, int End)? EditSpan { get; set; }
}
