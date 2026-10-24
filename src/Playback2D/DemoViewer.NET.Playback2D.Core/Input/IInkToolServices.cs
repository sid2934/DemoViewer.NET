#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     What the ink tools need on top of <see cref="IToolServices" />: the session they mutate, the
///     authoring clock, the two anchor lookups and the text-editor request.
///     <para>
///         A host with no ink implements <see cref="IToolServices" /> alone. The ink tools then refuse
///         the gesture instead of writing into a throwaway session, which is how the static map views
///         behave today: they registered no ink tool and answered every anchor lookup false.
///     </para>
///     <para>
///         It lives in Core rather than in the published scene contract because an extension never
///         implements it: the ink belongs to the playback tab, and publishing these five members would
///         freeze the annotation session's authoring surface as third-party API.
///     </para>
/// </summary>
public interface IInkToolServices : IToolServices
{
    /// <summary>The annotation session the tools mutate.</summary>
    AnnotationSession Session { get; }

    /// <summary>
    ///     A <b>monotonic</b> authoring clock in milliseconds from an arbitrary origin. Elapsed time,
    ///     never a wall-clock date.
    /// </summary>
    /// <remarks>
    ///     An NTP correction or a DST step landing mid-stroke would hand the cadence accumulator a
    ///     negative offset and invert its run table. It also cannot be
    ///     <see cref="IToolServices.CurrentTick" />: the playhead is FROZEN while the demo is paused,
    ///     which is when most annotation happens, so every sample of a paused stroke would share one
    ///     tick; and at <c>--speed 0.5</c> the hand moves at 1x while the clock moves at 0.5x. It
    ///     arrives through this interface because <c>BannedApiTests</c> bans
    ///     <c>DateTime</c>/<c>Stopwatch</c>/<c>Random</c> in Core outright.
    /// </remarks>
    long NowMilliseconds { get; }

    /// <summary>
    ///     Finds the nearest player marker within <paramref name="worldRadius" /> of a world point and
    ///     reports the offset from it, for entity-anchored telestration.
    /// </summary>
    /// <param name="pane">The pane the point is in, used to filter markers to this level.</param>
    /// <param name="world">The world point.</param>
    /// <param name="worldRadius">Capture radius in world units.</param>
    /// <param name="steamId">The anchored player's SteamId.</param>
    /// <param name="dx">World X offset from the player to <paramref name="world" />.</param>
    /// <param name="dy">World Y offset from the player to <paramref name="world" />.</param>
    bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
        out ulong steamId, out float dx, out float dy);

    /// <summary>
    ///     Where an existing element's stored samples are actually DRAWN in this pane, as a world-space
    ///     offset, the same resolution <c>AnnotationLayer</c> performs every frame.
    ///     <para>
    ///         Returns false when the element does not render in this pane at all: a
    ///         <see cref="SpaceRef.World" /> anchored to another floor, or a
    ///         <see cref="SpaceRef.Entity" /> whose player is absent or dead. That is what stops the
    ///         eraser from deleting a callout the user cannot see: on a stacked map both floors are on
    ///         screen at once, and the same world XY exists in both.
    ///     </para>
    /// </summary>
    /// <param name="pane">The pane the pointer is in.</param>
    /// <param name="element">The element being tested.</param>
    /// <param name="offsetX">World X offset applied to the element's samples when drawn here.</param>
    /// <param name="offsetY">World Y offset applied to the element's samples when drawn here.</param>
    bool TryResolveDrawOffset(LevelPane pane, AnnotationElement element,
        out float offsetX, out float offsetY);

    /// <summary>
    ///     Asks the host to open its text editor over a just-committed <see cref="AnnotationKind.Text" />
    ///     element. The host shows the editor at the element's anchor and hands the typed string back
    ///     through <c>TextTool.CompleteEdit</c>; Core never sees a text box.
    /// </summary>
    /// <param name="elementId">The text element being edited.</param>
    void RequestTextEdit(Guid elementId);
}
