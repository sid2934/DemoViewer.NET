namespace DemoViewer.NET.Extensions.Sdk.Playback;

/// <summary>A game event on the timeline.</summary>
/// <param name="Tick">Its tick.</param>
/// <param name="FrameIndex">Its frame.</param>
/// <param name="Fields">Its fields by name.</param>
public sealed record TimelineEvent(int Tick, int FrameIndex, IReadOnlyDictionary<string, string> Fields);

/// <summary>The open demo's timeline.</summary>
public interface ITimelineData
{
    /// <summary>Frames in the demo.</summary>
    int TotalFrames { get; }

    /// <summary>Ticks per second.</summary>
    int TickRate { get; }

    /// <summary>The frame at or after <paramref name="tick" />.</summary>
    int FrameIndexAtTick(int tick);

    /// <summary>Every event of a game-event type, in order.</summary>
    IReadOnlyList<TimelineEvent> EventsOfType(string eventName);

    /// <summary>True when the demo has at least one event of the type.</summary>
    bool HasEvent(string eventName);
}

/// <summary>A band a lane draws.</summary>
/// <param name="StartFrameIndex">Its first frame.</param>
/// <param name="EndFrameIndex">Its last frame.</param>
/// <param name="Label">Its label.</param>
/// <param name="Tooltip">Its tooltip.</param>
/// <param name="Argb">Its colour, 0xAARRGGBB.</param>
public sealed record TimelineBand(int StartFrameIndex, int EndFrameIndex, string Label, string Tooltip, uint Argb);

/// <summary>What a lane draws. Raise <see cref="Changed" /> when its bands change.</summary>
public interface ITimelineTrack
{
    /// <summary>Unique within the tab.</summary>
    string Id { get; }

    /// <summary>The name in the timeline's track menu.</summary>
    string DisplayName { get; }

    /// <summary>Whether the open demo can have bands on this track.</summary>
    bool IsAvailable(ITimelineData data);

    /// <summary>The bands for the open demo.</summary>
    IReadOnlyList<TimelineBand> BuildBands(ITimelineData data);

    /// <summary>Raised when the bands should be built again.</summary>
    event Action? Changed;
}

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
    /// <summary>The track it draws.</summary>
    ITimelineTrack Track { get; }

    /// <summary>Hidden for a mode, without touching the user's own track toggle.</summary>
    bool IsSuppressed { get; set; }

    /// <summary>The lane shows even when empty, and pressing it asks for a label.</summary>
    bool IsEditable { get; set; }

    /// <summary>A span drawn with two drag handles; one lane's at a time.</summary>
    (int Start, int End)? EditSpan { get; set; }
}
