#region

using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>Which part of a token a press landed on.</summary>
public enum TokenGrip
{
    /// <summary>The disc: a drag moves the token.</summary>
    Body,

    /// <summary>The heading stub: a drag turns the token toward the pointer instead of moving it.</summary>
    Heading
}

/// <summary>
///     The strat canvas's side of a token drag (step-authoring.md §3.7). <see cref="TokenTool" /> talks
///     to this and nothing else, so the tool never sees a strat: the canvas view-model implements it,
///     writes the keyframe for the active step, and turns a closed drag into one undo entry.
///     <para>
///         A drag is <see cref="BeginDrag" />, any number of <see cref="MoveTo" />, then exactly one of
///         <see cref="EndDrag" /> or <see cref="CancelDrag" />. Cancel rolls back to the state at
///         <see cref="BeginDrag" />; forty moves and an end are one edit, never forty.
///     </para>
/// </summary>
public interface ITokenEditor
{
    /// <summary>The strat frame-clock tick of the active step: where a drag writes its keyframe.</summary>
    int ActiveTick { get; }

    /// <summary>
    ///     Finds the token under a world point in a pane, nearest first. Only tokens drawn in that pane
    ///     count: on a stacked map both floors are on screen, and the same XY exists on each.
    /// </summary>
    /// <param name="pane">The pane the pointer is in.</param>
    /// <param name="world">The pointer, world space.</param>
    /// <param name="worldRadius">A marker's radius in world units at this pane's zoom (<see cref="TokenHitTest" />).</param>
    /// <param name="slot">The token's slot.</param>
    /// <param name="grip">Which part of it was hit.</param>
    bool TryHitToken(LevelPane pane, SKPoint world, float worldRadius, out string slot, out TokenGrip grip);

    /// <summary>Opens a drag on a token. The grip decides what <see cref="MoveTo" /> means.</summary>
    /// <param name="slot">The token.</param>
    /// <param name="grip">Move it, or turn it.</param>
    void BeginDrag(string slot, TokenGrip grip);

    /// <summary>
    ///     Moves the token to a point, creating the active step's entry for the slot when it had none. On a
    ///     <see cref="TokenGrip.Heading" /> drag the point is where the token should face and the level is
    ///     not read.
    /// </summary>
    /// <param name="slot">The token.</param>
    /// <param name="world">The pointer, world space.</param>
    /// <param name="levelMinZ">
    ///     <c>MapSpace.QuantizeZ(pane.Level.ZMin)</c> of the pane under the pointer, which is how a token
    ///     is dragged onto another floor of a stacked map.
    /// </param>
    void MoveTo(string slot, SKPoint world, double levelMinZ);

    /// <summary>Closes the drag as one edit.</summary>
    /// <param name="yawDegrees">
    ///     A yaw to set on the keyframe, or null to keep the one it has: the keyframe's own after a move,
    ///     the one the drag turned it to after a heading drag.
    /// </param>
    void EndDrag(float? yawDegrees);

    /// <summary>Abandons the drag, restoring what <see cref="BeginDrag" /> found.</summary>
    void CancelDrag();
}

/// <summary>
///     Where a press lands on one token, from the same geometry <c>MarkerLayer</c> draws: a disc of
///     <see cref="SceneDefaults.MarkerRadius" /> pixels and a heading stub out to
///     <see cref="SceneDefaults.MarkerHeadingLength" /> pixels beyond it. Shared so the canvas's
///     <see cref="ITokenEditor.TryHitToken" /> and the drawn marker cannot disagree.
/// </summary>
public static class TokenHitTest
{
    // A third of the radius either side: 3 px at the default size, the shape tools' tap slop.
    private const float SlopFraction = 1f / 3f;

    /// <summary>A marker's radius in world units for a pane scale.</summary>
    /// <param name="worldUnitsPerPixel">The pane's <c>IToolServices.WorldUnitsPerPixel</c>.</param>
    public static float WorldRadius(double worldUnitsPerPixel) =>
        (float)(SceneDefaults.MarkerRadius * worldUnitsPerPixel);

    /// <summary>
    ///     Which grip a point is on, or null when it misses the token. The stub is tested first, and only
    ///     beyond the disc's edge, so a press on the disc always moves it.
    /// </summary>
    /// <param name="tokenX">Token world X.</param>
    /// <param name="tokenY">Token world Y.</param>
    /// <param name="yawDegrees">Token yaw.</param>
    /// <param name="world">The point, world space.</param>
    /// <param name="worldRadius">The marker radius in world units (<see cref="WorldRadius" />).</param>
    public static TokenGrip? Classify(float tokenX, float tokenY, float yawDegrees, SKPoint world, float worldRadius)
    {
        double dx = world.X - (double)tokenX;
        double dy = world.Y - (double)tokenY;
        double slop = worldRadius * SlopFraction;

        double yaw = yawDegrees * Math.PI / 180.0;
        double along = dx * Math.Cos(yaw) + dy * Math.Sin(yaw);
        double across = Math.Abs(-dx * Math.Sin(yaw) + dy * Math.Cos(yaw));
        double tip = worldRadius * (1 + SceneDefaults.MarkerHeadingLength / SceneDefaults.MarkerRadius);

        if (along > worldRadius && along <= tip + slop && across <= slop)
        {
            return TokenGrip.Heading;
        }

        return dx * dx + dy * dy <= (worldRadius + slop) * (worldRadius + slop) ? TokenGrip.Body : null;
    }
}
