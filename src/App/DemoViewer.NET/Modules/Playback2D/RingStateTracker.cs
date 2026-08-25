#region

using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The event-driven ring-colour state machine for player markers. Pure and
///     deterministic: given the same per-slot inputs at the same frame index it always returns the
///     same ring state. No Avalonia / no entity dependency — fully unit-testable.
///     <para>
///         Keeps a tiny per-slot history of <c>(health, shotsFired)</c> and the frame index of the last
///         shoot / take-damage event, so a single-tick event remains visible across a short decay window
///         (~a few render frames, the "flash survives a render frame" requirement) without coupling to
///         wall-clock time. Precedence (highest first):
///         <b>
///             dead → blinded → taking-damage → shooting →
///             team
///         </b>
///         .
///     </para>
///     <para>
///         <b>Backward-seek / round-reset reset.</b> Health/shots deltas need the prior sample; on a
///         backward seek (or an explicit round reset) the caller invokes <see cref="Reset" /> so a stale
///         prior sample can't manufacture a false flash.
///     </para>
/// </summary>
public sealed class RingStateTracker
{
    private readonly Dictionary<int, SlotHistory> _bySlot = new(16);

    public RingStateTracker(int decayFrames = 8) => DecayFrames = Math.Max(1, decayFrames);

    /// <summary>Frames a shoot / take-damage flash stays lit before decaying back to the team colour.</summary>
    public int DecayFrames { get; }

    /// <summary>
    ///     Clears all per-slot history. Call on a backward seek (frameIndex went down) or a round
    ///     reset so cross-tick deltas don't false-flash off a stale prior sample.
    /// </summary>
    public void Reset() => _bySlot.Clear();

    /// <summary>
    ///     Computes the ring state + alpha for one player at <paramref name="frameIndex" />, updating the
    ///     per-slot delta history. Inputs are the copied-out scalars from the pawn. Precedence is
    ///     resolved here; flash states (shooting / taking-damage) decay over <see cref="DecayFrames" />.
    /// </summary>
    public (RingState State, double Alpha) Evaluate(
        int slot, int frameIndex, bool isAlive, float flashDuration, int health, int shotsFired)
    {
        if (!_bySlot.TryGetValue(slot, out SlotHistory h))
        {
            h = SlotHistory.Fresh(health, shotsFired);
        }

        // Cross-tick deltas vs the prior sample (only when we HAVE a prior sample — the first observation
        // of a slot seeds the baseline and never flashes, mirroring the "previous decoded tick" rule).
        if (h.HasPrior)
        {
            if (health < h.LastHealth)
            {
                h.LastDamageFrame = frameIndex;
            }

            if (shotsFired > h.LastShotsFired)
            {
                h.LastShootFrame = frameIndex;
            }
        }

        h.LastHealth = health;
        h.LastShotsFired = shotsFired;
        h.HasPrior = true;
        _bySlot[slot] = h;

        // Precedence: dead first, then blinded, then the decaying flash states, then team.
        if (!isAlive)
        {
            return (RingState.Dead, 1.0);
        }

        if (flashDuration > 0)
        {
            // Alpha ∝ remaining flash (clamped). Full alpha near the cap; fades as it runs out.
            double alpha = Math.Clamp(flashDuration / 2.0, 0.25, 1.0);
            return (RingState.Blinded, alpha);
        }

        bool damageLit = h.LastDamageFrame >= 0 && frameIndex - h.LastDamageFrame < DecayFrames;
        bool shootLit = h.LastShootFrame >= 0 && frameIndex - h.LastShootFrame < DecayFrames;

        if (damageLit)
        {
            return (RingState.TakingDamage, DecayAlpha(frameIndex - h.LastDamageFrame));
        }

        if (shootLit)
        {
            return (RingState.Shooting, DecayAlpha(frameIndex - h.LastShootFrame));
        }

        return (RingState.Team, 1.0);
    }

    // Linear fade from 1.0 (just happened) toward a visible floor over the decay window.
    private double DecayAlpha(int framesSince) =>
        Math.Clamp(1.0 - (double)framesSince / DecayFrames, 0.4, 1.0);

    private struct SlotHistory
    {
        public int LastHealth;
        public int LastShotsFired;
        public int LastDamageFrame;
        public int LastShootFrame;
        public bool HasPrior;

        public static SlotHistory Fresh(int health, int shotsFired) => new()
        {
            LastHealth = health,
            LastShotsFired = shotsFired,
            LastDamageFrame = -1,
            LastShootFrame = -1,
            HasPrior = false
        };
    }
}
