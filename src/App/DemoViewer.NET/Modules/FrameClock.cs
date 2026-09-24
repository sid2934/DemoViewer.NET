#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Builds the frame-clock header a persisted per-demo store writes, from the open demo's context.
///     <para>
///         One definition of <c>firstTick</c>/<c>lastTick</c>: the first and last frame's server tick as
///         the host reads them off the frame list. Every store that carries a <see cref="ClockIdentity" />
///         (annotations today; tags, round index, suggested tags and grenade walks next) fills it here
///         rather than assembling its own, so a mismatch on load means the parse differs and nothing
///         else.
///     </para>
/// </summary>
public static class FrameClock
{
    /// <summary>
    ///     The header for the demo <paramref name="context" /> currently holds. A context without a demo
    ///     yields the shipped 64-tick rate over zero frames, which is what the annotation session needs
    ///     as a divisor either way.
    /// </summary>
    public static ClockIdentity IdentityFor(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ClockIdentity(ClockIdentity.DvFrameClock,
            context.TickRate > 0 ? context.TickRate : 64,
            context.TotalFrames, context.FirstTick, context.LastTick);
    }

    /// <summary>
    ///     The header for a held parse, by the same definition: a background evaluator writing a
    ///     per-demo store has the <see cref="ParsedDemo" /> and no context.
    /// </summary>
    public static ClockIdentity IdentityFor(ParsedDemo parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        IReadOnlyList<DemoFrame> frames = parsed.Frames;
        return new ClockIdentity(ClockIdentity.DvFrameClock,
            parsed.TickRate > 0 ? parsed.TickRate : 64,
            frames.Count,
            frames.Count > 0 ? frames[0].ServerTick : 0,
            frames.Count > 0 ? frames[^1].ServerTick : 0);
    }
}
