#region

using CS2DemoKit.Parser;
using Cs2VideoGenerator.Core;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The UI-free core of F2 verify-in-CS2: computes the pre/post-roll
///     range around a frame-clock trigger tick, optionally spectates the attributed player, and
///     plays the range live in CS2, with deterministic paused arrival via the range's auto-pause.
///     The caller (the service) owns step 2/5: engine <c>BeginVerification</c> before, DV
///     playhead remote-apply + <c>EndVerification</c> after. Headlessly testable against the
///     real mock.
/// </summary>
public static class VerificationRunner
{
    /// <summary>Defaults: ~3 s of pre-roll context, ~1 s of follow-through at 64 tick.</summary>
    public const int DefaultPreRollTicks = 192;

    /// <summary>See <see cref="DefaultPreRollTicks" />.</summary>
    public const int DefaultPostRollTicks = 64;

    /// <summary>
    ///     Runs one verification playback. <paramref name="frameClockTick" /> is a FRAME-CLOCK
    ///     tick (<c>RuleChainEvent.Tick</c> / <c>GameEvent.GameTick</c>, already frame clock,
    ///     no <c>ServerStartTick</c> conversion). Precondition: a demo was loaded
    ///     through this session this run, always true in Synced.
    /// </summary>
    public static async Task<Outcome> RunAsync(
        CsvgVideoSession session,
        TickMapper mapper,
        int frameClockTick,
        int preRollTicks,
        int postRollTicks,
        string? spectateName,
        CancellationToken cancellationToken)
    {
        // The ClipWindows precedent: establish ALL clamps in the frame clock,
        // then apply the D2 TickOffset exactly once per emitted value. Clamping in CS2-tick
        // space with a literal-0 floor skews the range whenever TickOffset ≠ 0.
        long startFrameClock = Math.Max(0, (long)frameClockTick - preRollTicks);
        long maxFrameClock = mapper.DvTick(mapper.MaxCs2DemoTick);
        // Clamp the post-roll at demo end; keep at least a 1-tick range.
        long endFrameClock = Math.Max(startFrameClock + 1,
            Math.Min((long)frameClockTick + postRollTicks, maxFrameClock));
        long target = mapper.Cs2TickFromDvTick(frameClockTick);
        long start = mapper.Cs2TickFromDvTick(startFrameClock);
        long end = mapper.Cs2TickFromDvTick(endFrameClock);

        if (!string.IsNullOrWhiteSpace(spectateName))
        {
            // Exact in-demo name from the roster. Spectate is best-effort. A
            // rename mid-match breaks name targeting (known v1 limitation) but must not kill
            // the verification itself.
            try
            {
                await session.Engine.SetSpectatorTargetAsync(spectateName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best-effort by design.
            }
        }

        // CsvgVideoSession.PlayTickRangeAsync (not the Engine one) converts every playback problem
        // into a failed result rather than throwing: it's the 1.x PlayDemoTickRangeAsync(record:false)
        // replacement. record:false is now implicit: recording goes through CaptureClipAsync.
        DemoPlaybackResult result = await session
            .PlayTickRangeAsync(checked((int)start), checked((int)end), null, cancellationToken)
            .ConfigureAwait(false);

        return new Outcome(
            result.Success,
            result.Success ? null : result.ErrorMessage ?? "CS2 did not complete the range playback.",
            target,
            mapper.FrameIndexOf(checked((int)target)));
    }

    /// <summary>One verification's result.</summary>
    /// <param name="Success">
    ///     Whether the range played to its end (the client NEVER throws for
    ///     playback failures. Every failure mode lands here as false).
    /// </param>
    /// <param name="Error">The failure copy when <paramref name="Success" /> is false.</param>
    /// <param name="TargetCs2Tick">The trigger's CS2 demo tick (range midpoint-of-interest).</param>
    /// <param name="TargetFrameIndex">The trigger's DV frame: the caller's playhead remote-apply.</param>
    public sealed record Outcome(bool Success, string? Error, long TargetCs2Tick, int TargetFrameIndex);
}
