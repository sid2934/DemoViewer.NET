namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     Phase boundaries, derived from a round's stored ticks rather than stored themselves. Every
///     other Strat Room design takes round bounds and alive state from here (overview correction 11).
///     Intervals are half-open on the frame clock:
///     <list type="bullet">
///         <item><c>Freeze</c>: before <c>FreezeEndTick</c> (the buy time).</item>
///         <item><c>Opening</c>: from <c>FreezeEndTick</c> until the opening kill.</item>
///         <item><c>MidRound</c>: after the opening kill, before a plant.</item>
///         <item><c>PostPlant</c> / <c>Retake</c>: from the plant to the end, named by the side asking.</item>
///         <item><c>PostRound</c>: from <c>EndTick</c> on (the win panel; kills here are real).</item>
///     </list>
///     A round with no opening kill runs <c>Opening</c> to the plant or the end; a round with no end
///     (truncated) runs its last phase to the last frame. Which round a tick belongs to is
///     <c>IRoundFactsSource.RoundAt</c>'s question, so a tick past the next freeze end never reaches here
///     with this round.
/// </summary>
public static class RoundPhases
{
    /// <summary>The phase of <paramref name="tick" /> within <paramref name="round" />.</summary>
    /// <param name="round">The round the tick falls in, or null before the first freeze end.</param>
    /// <param name="tick">Frame clock.</param>
    /// <param name="side">
    ///     2 = T, 3 = CT, or null. Only the post-plant window reads it: the CT side gets
    ///     <see cref="RoundPhase.Retake" />, everyone else <see cref="RoundPhase.PostPlant" />.
    /// </param>
    public static RoundPhase At(RoundFacts? round, int tick, int? side = null)
    {
        if (round is null)
        {
            return RoundPhase.Warmup;
        }

        if (!round.IsLive)
        {
            return RoundPhase.None;
        }

        if (tick < round.FreezeEndTick)
        {
            return RoundPhase.Freeze;
        }

        if (round.EndTick is int end && tick >= end)
        {
            return RoundPhase.PostRound;
        }

        if (round.PlantTick is int plant && tick >= plant)
        {
            return side == 3 ? RoundPhase.Retake : RoundPhase.PostPlant;
        }

        if (round.OpeningKillTick is int opening && tick >= opening)
        {
            return RoundPhase.MidRound;
        }

        return RoundPhase.Opening;
    }

    /// <summary>
    ///     Players alive on each side at <paramref name="tick" />: the freeze-end count until the first
    ///     kill, then the count after the last kill at or before the tick.
    /// </summary>
    public static (int Ct, int T) AliveAt(RoundFacts round, int tick)
    {
        ArgumentNullException.ThrowIfNull(round);

        int ct = round.Ct.PlayersAtFreezeEnd;
        int t = round.T.PlayersAtFreezeEnd;
        foreach (KillStep kill in round.Kills)
        {
            if (kill.Tick > tick)
            {
                break;
            }

            ct = kill.CtAlive;
            t = kill.TAlive;
        }

        return (ct, t);
    }
}
