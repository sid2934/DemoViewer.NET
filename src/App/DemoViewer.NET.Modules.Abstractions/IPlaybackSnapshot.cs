namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The transient snapshot pushed to <c>IModuleContext.Advanced</c> on each (coalesced) render
///     frame. Valid ONLY for the duration of the callback — copy what you need,
///     do not retain it.
/// </summary>
public interface IPlaybackSnapshot
{
    /// <summary>0-based frame index at this push.</summary>
    int FrameIndex { get; }

    /// <summary>Server tick at this push.</summary>
    int Tick { get; }

    /// <summary>Read-only entity view at the current tick (transient).</summary>
    IReadOnlyEntityView Entities { get; }

    /// <summary>
    ///     Host-joined per-tick player state: the host did the pawn-join + position
    ///     reconstruction once, shared by all modules. Transient — copy out the scalars you need.
    /// </summary>
    IReadOnlyList<IPlayerState> Players { get; }
}
