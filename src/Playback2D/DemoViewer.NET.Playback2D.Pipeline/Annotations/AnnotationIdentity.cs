#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Annotations;

/// <summary>
///     Which demo a sidecar belongs to. <see cref="Sha256" /> is lowercase hex of the <c>.dem</c> bytes,
///     matching the existing <c>GraphBreakpointStore.ComputeDemoKey</c> convention; the name and size are
///     diagnostics only and never take part in the match.
/// </summary>
/// <param name="Sha256">Lowercase-hex SHA-256 of the demo file's bytes.</param>
/// <param name="FileName">The demo's file name, for a human reading the sidecar.</param>
/// <param name="SizeBytes">The demo's size in bytes, for a human reading the sidecar.</param>
public sealed record DemoIdentity(string Sha256, string FileName, long SizeBytes);

/// <summary>
///     Which parse the tick anchors were authored against.
///     <para>
///         <b>The DV frame clock, never CS2 server ticks.</b> LiveSync's servo bends the playhead between
///         0.75× and 1.5×, so a CS2-tick anchor would drift against what the annotator was looking at.
///         Recording the clock lets a load that meets a different parse WARN instead of silently
///         mis-placing every time-anchored stroke.
///     </para>
/// </summary>
/// <param name="Kind">Clock discriminator; always <see cref="DvFrameClock" /> today.</param>
/// <param name="TickRate">Ticks per second of the parse the anchors were written against.</param>
/// <param name="FrameCount">Total demo frames in that parse.</param>
/// <param name="FirstTick">First tick in that parse.</param>
/// <param name="LastTick">Last tick in that parse.</param>
public sealed record ClockIdentity(string Kind, int TickRate, int FrameCount, int FirstTick, int LastTick)
{
    /// <summary>The only clock kind that exists today.</summary>
    public const string DvFrameClock = "dv-frame-clock";

    /// <summary>The "we do not know" clock. Never matches a real one, and never warns against one.</summary>
    public static ClockIdentity Unknown { get; } = new(DvFrameClock, 0, 0, 0, 0);

    /// <summary>Whether two clocks describe the same parse.</summary>
    /// <param name="other">The clock read from a sidecar, or null when it carried none.</param>
    public bool Matches(ClockIdentity? other)
    {
        if (other is null)
        {
            return true; // a sidecar without a clock predates the field; nothing to disagree with
        }

        // An unknown clock on either side is not a mismatch: the caller could not supply one, so a
        // warning would be noise rather than a signal.
        if (IsUnknown(this) || IsUnknown(other))
        {
            return true;
        }

        return string.Equals(Kind, other.Kind, StringComparison.Ordinal)
               && TickRate == other.TickRate
               && FrameCount == other.FrameCount
               && FirstTick == other.FirstTick
               && LastTick == other.LastTick;
    }

    private static bool IsUnknown(ClockIdentity clock) =>
        clock.FrameCount == 0 && clock.TickRate == 0 && clock.FirstTick == 0 && clock.LastTick == 0;
}

/// <summary>Where a document was (or would be) persisted.</summary>
public enum AnnotationStoreLocation
{
    /// <summary>Nowhere — the demo directory is not writable and there is no app-data root (WASM).</summary>
    None,

    /// <summary><c>&lt;demo&gt;.dvann.json</c>, beside the demo.</summary>
    DemoSidecar,

    /// <summary><c>&lt;appDataRoot&gt;/annotations/&lt;sha256&gt;.dvann.json</c>.</summary>
    AppData
}

/// <summary>
///     The outcome of a load. Carries the two mismatch flags rather than throwing, because neither is an
///     error the user can act on mid-session and both have a correct degraded behaviour.
/// </summary>
/// <param name="Elements">The loaded elements; empty when there was nothing to load.</param>
/// <param name="Location">Where the document was read from.</param>
/// <param name="Path">The file that was read, or null.</param>
/// <param name="DemoMismatch">
///     The sidecar's demo hash names a different demo. The file is IGNORED and never overwritten — it
///     belongs to someone else's demo that happens to share a path.
/// </param>
/// <param name="ClockMismatch">
///     The sidecar was authored against a different parse. Everything still loads: static elements are
///     unaffected, and the UI warns that time anchors may be off rather than discarding them.
/// </param>
/// <param name="SchemaVersion">The schema version the file declared.</param>
public sealed record AnnotationLoadResult(
    IReadOnlyList<AnnotationElement> Elements,
    AnnotationStoreLocation Location,
    string? Path,
    bool DemoMismatch,
    bool ClockMismatch,
    int SchemaVersion)
{
    /// <summary>Nothing on disk, nothing wrong.</summary>
    /// <param name="location">Where the store would have looked.</param>
    /// <param name="path">Where the store would have looked, or null.</param>
    public static AnnotationLoadResult Empty(AnnotationStoreLocation location, string? path) =>
        new([], location, path, false, false, AnnotationStore.SchemaVersion);
}
