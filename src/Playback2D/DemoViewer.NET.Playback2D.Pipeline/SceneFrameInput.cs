#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline;

/// <summary>
///     Everything <see cref="SceneFrameBuilder.Build" /> reads for one frame.
///     <para>
///         A <c>ref struct</c> on purpose: <see cref="Players" /> and <see cref="Entities" /> are the
///         host's transient/pooled read surfaces, valid only inside the <c>Advanced</c> callback. Making
///         the input non-escaping is the type system saying so.
///     </para>
/// </summary>
public readonly ref struct SceneFrameInput
{
    /// <summary>The live player states for this tick. Never retained past the call.</summary>
    public required IReadOnlyList<IPlayerState> Players { get; init; }

    /// <summary>The entity read surface for this tick. Never retained past the call.</summary>
    public required IReadOnlyEntityView Entities { get; init; }

    /// <summary>Index of the demo frame being built. Drives seek/discontinuity detection.</summary>
    public required int FrameIndex { get; init; }

    /// <summary>The DV frame clock for this frame.</summary>
    public required int Tick { get; init; }

    /// <summary>Demo tick rate; values ≤ 0 are treated as 64.</summary>
    public required int TickRate { get; init; }

    /// <summary>
    ///     The host's offset-corrected game clock for <see cref="Tick" />, aligning demo curtime to the
    ///     entity time base that <c>m_fRoundStartTime</c> / <c>m_flC4Blow</c> stamp against.
    /// </summary>
    public required double CurtimeSeconds { get; init; }

    /// <summary>
    ///     Slot → marker label. Roster display is view-model state, so the builder is handed the
    ///     projection rather than owning the roster.
    /// </summary>
    public required Func<int, string> LabelForSlot { get; init; }

    /// <summary>Slot → SteamId for annotation/camera anchoring, or null when unresolved (marker gets 0).</summary>
    public Func<int, ulong>? SteamIdForSlot { get; init; }

    /// <summary>The current map's name, or null when not yet known.</summary>
    public string? MapName { get; init; }

    /// <summary>The kill rows the host wants visible this frame. Copied onto the frame as-is.</summary>
    public IReadOnlyList<KillFeedRow>? KillFeed { get; init; }

    /// <summary>The decoded radar layers for this map, or null when no bundle is loaded.</summary>
    public IReadOnlyList<MapRadarImage>? Radars { get; init; }

    /// <summary>Solved line-of-sight geometry. B1 fills this; B0 always passes <see cref="SceneVision.Off" />.</summary>
    public SceneVision? Vision { get; init; }

    // Stored biased by one so an unset input means "nobody" (-1) rather than "slot 0". A ref struct
    // cannot carry a field initializer, so without the bias a caller that simply omits FollowSlot would
    // silently follow the first player.
    private readonly int _followSlotPlusOne;

    /// <summary>The followed roster slot, or -1 for none. Omitting it means none.</summary>
    public int FollowSlot
    {
        get => _followSlotPlusOne - 1;
        init => _followSlotPlusOne = value + 1;
    }
}
