#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     What an <see cref="AnnotationElement" /> draws. Only <see cref="Freehand" /> ships in B2; the rest
///     exist from day one so a later shape tool is an additive branch rather than a schema migration.
/// </summary>
public enum AnnotationKind
{
    /// <summary>A pressure-varying ink stroke through its raw samples.</summary>
    Freehand,

    /// <summary>Straight segment between the first and last point.</summary>
    Line,

    /// <summary>Straight segment with a head at the last point.</summary>
    Arrow,

    /// <summary>Axis-aligned rectangle spanned by the first and last point.</summary>
    Rect,

    /// <summary>Ellipse inscribed in the rectangle spanned by the first and last point.</summary>
    Ellipse,

    /// <summary>A text label anchored at the first point.</summary>
    Text
}

/// <summary>
///     Envelope authoring mode — drives what the UI writes into <see cref="TimeEnvelope" />. Persisted as a
///     string in <c>Playback2DSettings.AnnotationDefaultVisibility</c>, so the member NAMES are a contract.
/// </summary>
public enum EnvelopeMode
{
    /// <summary>Always visible: <see cref="TimeEnvelope.Static" />.</summary>
    Always,

    /// <summary>Pinned to the authoring tick, held, then faded out.</summary>
    Fade,

    /// <summary>Explicit From/Until ticks typed by the user.</summary>
    Custom
}

/// <summary>One raw input sample in WORLD units. Pressure is 0..1; 0.5 when the device reports none.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Pressure">Stylus pressure 0..1, or 0.5 for a device without one.</param>
public readonly record struct InkPoint(float X, float Y, float Pressure);

/// <summary>
///     How one element is painted. ARGB colour, stroke width in WORLD units, and a 0..1 opacity
///     multiplier applied on top of the time envelope.
/// </summary>
/// <param name="ColorArgb">Packed ARGB (0xAARRGGBB).</param>
/// <param name="WidthWorld">Stroke width in world units — ink zooms with the map, like a map pen.</param>
/// <param name="Opacity">0..1 multiplier applied on top of <see cref="TimeEnvelope.OpacityAt" />.</param>
/// <param name="RevealOnFadeIn">
///     When true a mid-fade-in <see cref="AnnotationKind.Freehand" /> draws only its leading fraction of
///     points, giving the "draw-on reveal" animation for free (design §5.4).
/// </param>
public readonly record struct AnnotationStyle(uint ColorArgb, float WidthWorld, float Opacity,
    bool RevealOnFadeIn = false)
{
    /// <summary>Amber, 6 world units wide, fully opaque, no reveal — the app's default ink.</summary>
    public static readonly AnnotationStyle Default = new(0xFFFFC107, 6f, 1f);
}

/// <summary>
///     Where an element lives in space. A closed discriminated union: exactly two cases, and adding a
///     third is a schema change, not an extension point.
/// </summary>
public abstract record SpaceRef
{
    /// <summary>
    ///     Default anchor: a map level, keyed by its QUANTIZED lower Z
    ///     (<c>MapSpace.QuantizeZ(level.ZMin)</c>), never a slice index. Quantizing is what lets an anchor
    ///     written before a level-set rebuild still find its own floor (plan correction 10).
    /// </summary>
    /// <param name="LevelMinZ">The quantized lower world Z of the level this element belongs to.</param>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]
    public sealed record World(double LevelMinZ) : SpaceRef;

    /// <summary>
    ///     Tracked telestration: the stroke follows a player. Keyed by SteamId because roster SLOTS
    ///     RECYCLE across a demo, so a slot-keyed anchor silently re-targets (design §5.4).
    ///     <para>
    ///         <paramref name="Dx" />/<paramref name="Dy" /> is the offset from the player to the stroke's
    ///         FIRST point at authoring time. Rendering translates the whole stroke so that its first
    ///         point sits at <c>marker + (Dx, Dy)</c>, which makes the offset exactly zero at the moment
    ///         it was drawn.
    ///     </para>
    /// </summary>
    /// <param name="SteamId">The anchored player's 64-bit SteamId. 0 never resolves.</param>
    /// <param name="Dx">World X offset from the player to the stroke's first point, at authoring time.</param>
    /// <param name="Dy">World Y offset from the player to the stroke's first point, at authoring time.</param>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "Closed discriminated union; the nesting is the contract (design §5.4).")]
    public sealed record Entity(ulong SteamId, float Dx, float Dy) : SpaceRef;
}

/// <summary>
///     When an element is visible, as a Kinovea-style trapezoid over the DV FRAME CLOCK.
///     <para>
///         <b>The fades sit OUTSIDE the window</b> (plan decision D5): full opacity over
///         <c>[FromTick, UntilTick]</c>, a 0→1 lead-in over <c>[FromTick − FadeInTicks, FromTick)</c>, a
///         1→0 lead-out over <c>(UntilTick, UntilTick + FadeOutTicks]</c>, and 0 outside all three. A
///         null bound is ±∞. That is what makes <c>default</c> — null bounds, zero fades — a constant
///         1.0, which is exactly what <see cref="Static" /> has to be.
///     </para>
///     <para>
///         <b>Ticks here are DV frame-clock ticks, never CS2 server ticks.</b> LiveSync's servo bends the
///         playhead between 0.75× and 1.5×, so a CS2-tick anchor would drift against what the user saw.
///     </para>
/// </summary>
/// <param name="FromTick">First fully-opaque tick, or null for "since the beginning".</param>
/// <param name="UntilTick">Last fully-opaque tick, or null for "until the end".</param>
/// <param name="FadeInTicks">Length of the 0→1 lead-in before <paramref name="FromTick" />.</param>
/// <param name="FadeOutTicks">Length of the 1→0 lead-out after <paramref name="UntilTick" />.</param>
public readonly record struct TimeEnvelope(int? FromTick, int? UntilTick, int FadeInTicks, int FadeOutTicks)
{
    /// <summary>
    ///     Always visible at full opacity. Structurally <c>default</c> — design §5.4 requires exactly
    ///     that, which is why the fades sit outside the window (D5) rather than inside it.
    /// </summary>
    public static readonly TimeEnvelope Static;

    /// <summary>True when either bound is set, i.e. this element has a place on the timeline.</summary>
    public bool IsAnchored => FromTick.HasValue || UntilTick.HasValue;

    /// <summary>
    ///     The opacity multiplier at a tick. Pure: the same tick always gives the same answer regardless
    ///     of call order, which is what makes scrubbing backwards identical to scrubbing forwards.
    /// </summary>
    /// <param name="tick">A DV frame-clock tick.</param>
    public double OpacityAt(int tick)
    {
        if (FromTick is { } from)
        {
            if (tick < from)
            {
                int fadeIn = FadeInTicks;
                if (fadeIn <= 0)
                {
                    return 0;
                }

                long lead = (long)from - tick;
                return lead > fadeIn ? 0 : 1.0 - (double)lead / fadeIn;
            }
        }

        if (UntilTick is { } until)
        {
            if (tick > until)
            {
                int fadeOut = FadeOutTicks;
                if (fadeOut <= 0)
                {
                    return 0;
                }

                long trail = (long)tick - until;
                return trail > fadeOut ? 0 : 1.0 - (double)trail / fadeOut;
            }
        }

        return 1.0;
    }

    /// <summary>
    ///     "Pin to now": an envelope opening at <paramref name="tick" />, held for
    ///     <paramref name="holdTicks" />, with the given lead-in and lead-out ramps.
    /// </summary>
    /// <param name="tick">The tick the element becomes fully opaque.</param>
    /// <param name="holdTicks">How many ticks it stays fully opaque. Negative is clamped to 0.</param>
    /// <param name="fadeIn">Lead-in length in ticks.</param>
    /// <param name="fadeOut">Lead-out length in ticks.</param>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance by contract (design §5.4): the UI calls it on the element's existing " +
                        "envelope, and it becomes stateful the day 'pin' starts preserving custom fades.")]
    public TimeEnvelope PinnedTo(int tick, int holdTicks, int fadeIn, int fadeOut) =>
        new(tick, tick + Math.Max(0, holdTicks), Math.Max(0, fadeIn), Math.Max(0, fadeOut));
}

/// <summary>
///     One drawn thing: an identity, a shape kind, a paint, a space anchor, a time envelope and the raw
///     input samples. Immutable — an edit is a <c>DocDelta.Replace</c>, which is what makes undo a stack
///     of value swaps rather than a diff of mutable objects.
/// </summary>
/// <param name="Id">Stable identity. Survives undo/redo, persistence and level remaps.</param>
/// <param name="Kind">What this element draws.</param>
/// <param name="Style">How it is painted.</param>
/// <param name="Space">Where it lives.</param>
/// <param name="Time">When it is visible.</param>
/// <param name="Points">Raw WORLD-space samples, oldest first. Never empty for a committed element.</param>
/// <param name="Text">Label content for <see cref="AnnotationKind.Text" />; null otherwise.</param>
public sealed record AnnotationElement(
    Guid Id,
    AnnotationKind Kind,
    AnnotationStyle Style,
    SpaceRef Space,
    TimeEnvelope Time,
    IReadOnlyList<InkPoint> Points,
    string? Text);
