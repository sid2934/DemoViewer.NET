#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Answers one question the spray-control metric depends on: is the networked aim-punch column
///     the RESOLVED punch, or a damped-spring sample that has to be integrated forward before it
///     means anything in degrees?
///     <para>
///         <c>bullet_damage</c> carries <c>AimPunchX/Y/Z</c>, the server's own resolved punch at the
///         instant of the shot, so every landed bullet is a labelled example. This walks them and
///         reports how far the entity column sits from that oracle. If the two agree the column is
///         already usable and the integration is unnecessary; if they diverge, the size and shape of
///         the divergence is what any integration has to reproduce.
///     </para>
///     <para>
///         Mostly diagnostic: the comparison itself is printed rather than asserted, because what it
///         measures is a property of the demo's schema vintage and not of our code. It exists because
///         the alternative was guessing a spring constant and shipping a smooth, believable, wrong
///         number, and <c>docs/handoff/cs2demokit-aim-providers.md</c> §8 names this file as the way
///         to reproduce the finding.
///     </para>
///     <para>
///         <b>What it does assert</b> is that it measured anything at all. A full entity replay that
///         resolves no punch field, or resolves no attacker pawn, prints <c>compared=0</c> and used
///         to pass on <c>Frames.Count &gt; 0</c> — a green run over a probe that had quietly stopped
///         probing. Either of those is a real regression in the field paths or in pawn resolution,
///         so on a demo that carries <c>bullet_damage</c> an empty population is now a red.
///     </para>
/// </summary>
[Category("RealDemo")]
[Category("Probe")]
[NotInParallel]
public class AimPunchOracleProbe
{
    /// <summary>Fields the two schema vintages spell the punch with, newest first.</summary>
    private static readonly string[] _punchPaths =
    [
        "m_pAimPunchServices.m_predictableBaseAngle",
        "m_aimPunchAngle"
    ];

    [Test]
    public async Task Probe_NetworkedAimPunch_AgainstTheServersResolvedValue()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        Console.WriteLine($"[punch] {Path.GetFileName(path)} map={demo.MapName} frames={demo.Frames.Count}");

        EntityStateLayer layer = new(demo.Frames);
        string? resolved = null;

        int landed = 0;
        int compared = 0;
        int agreed = 0;
        double sumAbsColumn = 0;
        double sumAbsOracle = 0;
        double sumAbsDelta = 0;
        double worst = 0;
        Dictionary<int, List<(double X, double Y)>> byIndex = [];

        for (int i = 0; i < demo.Frames.Count; i++)
        {
            DemoFrame frame = demo.Frames[i];
            foreach (GameEvent evt in EventsIn(frame))
            {
                if (evt.Payload is not BulletDamageEvent shot)
                {
                    continue;
                }

                // Counted before anything can drop the shot, so the assertions below can tell "this
                // demo has no oracle" apart from "the oracle is here and we failed to read it".
                landed++;
                layer.SeekToTick(frame.ServerTick);
                EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, shot.Attacker);
                if (pawn is null)
                {
                    continue;
                }

                resolved ??= _punchPaths.FirstOrDefault(p => pawn.TryGet<Vector3>(p) is not null);
                if (resolved is null || pawn.TryGet<Vector3>(resolved) is not { } column)
                {
                    continue;
                }

                // The wire carries a QAngle over [0, 360), so a real -2 degree kick arrives as 358.
                double columnPitch = Wrap(column.X);
                double delta = Math.Abs(columnPitch - shot.AimPunchX);

                if (compared < 16)
                {
                    Vector3? vel = pawn.TryGet<Vector3>("m_aimPunchAngleVel");
                    object? tb = pawn["m_aimPunchTickBase"];
                    Vector3? eye = pawn.TryGet<Vector3>("m_angEyeAngles");
                    Console.WriteLine($"[ang]  eyeX={eye?.X,8:F3} shootX={shot.ShootAngX,8:F3} "
                                      + $"punchX={shot.AimPunchX,7:F3} eye-shoot={(eye?.X - shot.ShootAngX),7:F3}");
                    Console.WriteLine($"[pair] rawX={column.X,9:F3} rawY={column.Y,9:F3} "
                                      + $"wrapX={columnPitch,8:F3} oracleX={shot.AimPunchX,8:F3} "
                                      + $"oracleY={shot.AimPunchY,8:F3} velX={vel?.X,8:F3} tickBase={tb} tick={frame.ServerTick}");
                }

                int ri = (int)Math.Round(shot.RecoilIndex);
                if (!byIndex.TryGetValue(ri, out List<(double X, double Y)>? bucket))
                {
                    bucket = [];
                    byIndex[ri] = bucket;
                }

                bucket.Add((shot.AimPunchX, shot.AimPunchY));

                compared++;
                sumAbsColumn += Math.Abs(columnPitch);
                sumAbsOracle += Math.Abs(shot.AimPunchX);
                sumAbsDelta += delta;
                worst = Math.Max(worst, delta);
                if (delta <= 0.25)
                {
                    agreed++;
                }
            }
        }

        // Is the punch CUMULATIVE along the spray? If AimPunch at recoil index n is the pattern
        // offset rather than the per-shot kick, the ideal pattern is directly readable off the wire
        // and needs neither the PRNG nor entity state.
        Console.WriteLine("[idx] recoil  n   meanPunchX  meanPunchY");
        foreach (int idx in byIndex.Keys.OrderBy(k => k).Take(14))
        {
            List<(double X, double Y)> v = byIndex[idx];
            Console.WriteLine($"[idx] {idx,6} {v.Count,3} {v.Average(t => t.X),11:F3} {v.Average(t => t.Y),11:F3}");
        }

        Console.WriteLine($"[punch] field={resolved ?? "NONE"} landed={landed} compared={compared}");
        if (compared > 0)
        {
            Console.WriteLine($"[punch] mean |column| = {sumAbsColumn / compared:F3} deg");
            Console.WriteLine($"[punch] mean |oracle| = {sumAbsOracle / compared:F3} deg");
            Console.WriteLine($"[punch] mean |delta|  = {sumAbsDelta / compared:F3} deg   worst={worst:F3}");
            Console.WriteLine($"[punch] within 0.25 deg: {agreed} / {compared} "
                              + $"({100.0 * agreed / compared:F1}%)");
        }

        if (landed == 0)
        {
            throw new SkipTestException(
                "This demo carries no bullet_damage, so there is no server-resolved punch to compare "
                + "against. The trimmed GOTV sources routinely omit it; the Valve matchmaking demos "
                + "carry it.");
        }

        await Assert.That(resolved).IsNotNull()
            .Because("neither punch field path resolved on any shooting pawn, so a whole entity "
                     + "replay produced no comparison at all");
        await Assert.That(compared).IsGreaterThan(0)
            .Because($"{landed} landed bullets carry a server punch and none of them reached the "
                     + "comparison, so the attacker pawns are not resolving");
    }

    private static double Wrap(double degrees)
    {
        double d = degrees % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }
        else if (d <= -180.0)
        {
            d += 360.0;
        }

        return d;
    }

    private static IEnumerable<GameEvent> EventsIn(DemoFrame frame)
    {
        foreach (NetMessage msg in frame.DecodedMessages)
        {
            if (msg is GameEventMessage gem && gem.DecodedEvent is { } evt)
            {
                yield return evt;
            }
        }
    }
}
