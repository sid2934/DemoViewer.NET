#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;

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

        return RoundFactsClock.For(context).ToIdentity();
    }

    /// <summary>
    ///     The header for a held parse, by the same definition: a background evaluator writing a
    ///     per-demo store has the <see cref="ParsedDemo" /> and no context.
    /// </summary>
    public static ClockIdentity IdentityFor(ParsedDemo parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        return RoundFactsClock.For(parsed).ToIdentity();
    }

    /// <summary>The header for a forward pass, by the same definition: the pass counts the frames it read.</summary>
    public static ClockIdentity IdentityFor(ForwardDemoResult pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        return new ClockIdentity(ClockIdentity.DvFrameClock,
            pass.Demo.TickRate > 0 ? pass.Demo.TickRate : 64,
            pass.FrameCount, pass.FirstServerTick, pass.LastServerTick);
    }
}
