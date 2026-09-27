#region

using System.Numerics;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Services.Strats.Mining;

/// <summary>
///     A <see cref="RoundCapture" /> built from cached files instead of a tracker walk, so a mined pattern
///     becomes steps through the same <see cref="StratFromRound" /> as Create Strat From Round. Positions are the
///     positions file's 1 s samples, so a stop reads the nearest sample, and there is no yaw.
/// </summary>
public static class CachedRoundCapture
{
    /// <summary>
    ///     The capture of one round up to <paramref name="windowEndTick" />: the freeze end, each grenade's
    ///     detonation, the plant and the 10 s sweep where no other stop is within 3 s.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="stored">The round in it.</param>
    /// <param name="facts">The round's Round Facts row.</param>
    /// <param name="grenades">The round's grenade rows, or empty.</param>
    /// <param name="players">Controller slot to SteamID64 and name, from the cache record.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="windowEndTick">The last tick to capture.</param>
    public static RoundCapture Build(RoundPositionsDocument positions, RoundPositionsRound stored, RoundFacts.RoundFacts facts,
        IReadOnlyList<GrenadeRow> grenades, IReadOnlyDictionary<int, (ulong SteamId, string Name)> players, int tickRate,
        int windowEndTick)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(grenades);
        ArgumentNullException.ThrowIfNull(players);

        int freezeEnd = stored.FreezeEndTick;
        int end = Math.Min(windowEndTick, facts.EndTick ?? int.MaxValue);
        HashSet<int> ct = [.. stored.Ct];

        List<CapturedPawn> PawnsAt(int tick)
        {
            int step = Math.Clamp(
                positions.CadenceTicks <= 0 ? 0 : (int)Math.Round((tick - freezeEnd) / (double)positions.CadenceTicks),
                0, Math.Max(0, stored.Pos.Count - 1));
            return
            [
                .. stored.At(step).OrderBy(p => p.Slot).Select(p =>
                {
                    (ulong id, string name) = players.GetValueOrDefault(p.Slot);
                    return new CapturedPawn(p.Slot, ct.Contains(p.Slot) ? 3 : 2, id, name, p.X, p.Y, p.Z, 0, positions.PlaceOf(p.PlaceId));
                })
            ];
        }

        List<CaptureMoment> stops = [];
        foreach (GrenadeRow row in grenades.OrderBy(g => g.DetonationTick ?? g.EndTick).ThenBy(g => g.Id, StringComparer.Ordinal))
        {
            int tick = row.DetonationTick ?? row.EndTick;
            if (tick < freezeEnd || tick > end || KindOf(row.Kind) is not { } kind
                || (row.DetonationPosition ?? row.Trajectory.LastOrDefault().Position.ToWorld()) is not { } landing)
            {
                continue;
            }

            stops.Add(new CaptureMoment(tick, CaptureTrigger.Utility, PawnsAt(tick), kind, row.ThrowerSlot, row.ThrowerTeam,
                landing.ToVector(), row.ReleasePosition?.ToVector()));
        }

        if (facts.PlantTick is { } plant && plant >= freezeEnd && plant <= end)
        {
            stops.Add(new CaptureMoment(plant, CaptureTrigger.Plant, PawnsAt(plant), ActorSlot: facts.PlanterSlot ?? -1, ActorTeam: 2));
        }

        int sweep = RoundCaptureWalker.SweepSeconds * tickRate;
        int quiet = RoundCaptureWalker.SweepQuietSeconds * tickRate;
        List<CaptureMoment> sweeps = [];
        for (int tick = freezeEnd + sweep; tick <= end; tick += sweep)
        {
            if (!stops.Any(s => Math.Abs(s.Tick - tick) < quiet))
            {
                sweeps.Add(new CaptureMoment(tick, CaptureTrigger.Sweep, PawnsAt(tick)));
            }
        }

        List<CaptureMoment> moments = [new CaptureMoment(freezeEnd, CaptureTrigger.FreezeEnd, PawnsAt(freezeEnd))];
        moments.AddRange(stops.Concat(sweeps).OrderBy(m => m.Tick).ThenBy(m => m.Trigger));
        return new RoundCapture(facts.Number, freezeEnd, facts.EndTick ?? end, tickRate, moments);
    }

    /// <summary>The strat's spelling of a grenade kind; a fire is a molotov whichever side threw it.</summary>
    /// <param name="kind">The Grenade Walk's kind.</param>
    public static string? KindOf(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Smoke => "smoke",
        GrenadeKind.Flash => "flash",
        GrenadeKind.He => "he",
        GrenadeKind.Molotov or GrenadeKind.Incendiary => "molotov",
        GrenadeKind.Decoy => "decoy",
        _ => null
    };

    private static WorldPoint? ToWorld(this Vector3 v) => v == Vector3.Zero ? null : WorldPoint.From(v);
}
