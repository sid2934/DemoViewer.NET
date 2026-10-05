namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     The rest of a strat token's current move: its position now, then each corner to where the move ends, in world
///     space. Pooled by the frame source and refilled in place, as <see cref="GrenadeTrail" /> is.
/// </summary>
public sealed class TokenRouteLine
{
    /// <summary>The token's side, 2 = T and 3 = CT, which colours the line.</summary>
    public int Team { get; set; }

    /// <summary>From the token's position to the move's end. Z is each point's marker Z, which picks its floor pane.</summary>
    public List<GrenadeTrailPoint> Points { get; } = new(16);
}
