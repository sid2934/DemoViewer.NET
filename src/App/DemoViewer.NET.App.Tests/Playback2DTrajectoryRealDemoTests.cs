#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Gates the A4 grenade flight-trail overlay end-to-end on a REAL demo: stepping the real
///     <see cref="EntityTracker" /> through the frames LEADING UP TO a real <c>smokegrenade_detonate</c>
///     (when the smoke projectile is in flight), the VM must accumulate a multi-point <c>GrenadeKind.Smoke</c>
///     trail at sane in-world coordinates, proving the projectile classes resolve through the live entity
///     view and that <c>CBodyComponent</c> cell coords reconstruct the flight path (not just synthetic
///     doubles). Drives the VM through a minimal harness backed by the REAL <see cref="ReadOnlyEntityView" />
///     so the multi-push accumulation is exercised without pumping the host's coalescing dispatcher.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DTrajectoryRealDemoTests
{
    [Test]
    public async Task RealDemo_SmokeInFlight_AccumulatesSmokeTrail()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int detonate = FirstEventFrame(frames, "smokegrenade_detonate");
        if (detonate < 0)
        {
            throw new SkipTestException("no smokegrenade_detonate in demo");
        }

        // Flight window: the ~0.75s of frames before detonation, when CSmokeGrenadeProjectile is airborne.
        const int WindowFrames = 48;
        int start = Math.Max(0, detonate - WindowFrames);

        EntityTracker tracker = new();
        tracker.ReplayToIndex(start, frames);
        ReadOnlyEntityView view = new(tracker.CurrentEntities); // CurrentEntities mutates in place per step

        Ctx ctx = new();
        ctx.Roster.AddRange(demo.Players.Values.Select(p =>
            new PlayerRosterEntry
            {
                Slot = p.Slot,
                SteamId = p.SteamId64,
                Name = p.Name
            }));

        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);

        GrenadeTrail? bestSmoke = null;
        for (int i = start; i <= detonate; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            ctx.Push(new Snap(i, frames[i].ServerTick, view));

            GrenadeTrail? smoke = vm.GrenadeTrails
                .Where(t => t.Kind == GrenadeKind.Smoke)
                .OrderByDescending(t => t.Points.Count)
                .FirstOrDefault();
            if (smoke is not null && (bestSmoke is null || smoke.Points.Count > bestSmoke.Points.Count))
            {
                bestSmoke = smoke;
            }
        }

        Console.WriteLine($"[trail] smokegrenade_detonate @frame {detonate}; best smoke trail = " +
                          $"{bestSmoke?.Points.Count ?? 0} pts");

        await Assert.That(bestSmoke).IsNotNull();
        await Assert.That(bestSmoke!.Points.Count).IsGreaterThanOrEqualTo(2); // a real multi-point flight path

        // The flight points are at sane in-world positions (not the (0,0) failure spot, within world extent).
        foreach (GrenadeTrailPoint pt in bestSmoke.Points)
        {
            await Assert.That(Math.Abs(pt.X) + Math.Abs(pt.Y)).IsGreaterThan(1f);
            await Assert.That(Math.Abs(pt.X)).IsLessThan(20000f);
            await Assert.That(Math.Abs(pt.Y)).IsLessThan(20000f);
        }
    }

    [Test]
    public async Task RealDemo_LandedSmoke_HoldsPosition_SoFlightTrailFades()
    {
        // The review-pass fade fix assumes a LANDED smoke stops MOVING (so its flight trail fades over ~2s
        // even though the cloud entity persists ~18s). Verify on real data: a smoke that persists across the
        // whole post-detonation window holds its per-axis movement under the 0.5u SamePoint append threshold,
        // so it never re-appends and the trail fades cleanly instead of flickering for the cloud's whole life.
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int detonate = FirstEventFrame(frames, "smokegrenade_detonate");
        if (detonate < 0)
        {
            throw new SkipTestException("no smokegrenade_detonate in demo");
        }

        int start = detonate + 20; // a bit after detonation → fully landed
        int end = Math.Min(frames.Count - 1, start + 128); // ~2s fade window
        if (end - start < 64)
        {
            throw new SkipTestException("not enough frames after detonation");
        }

        EntityTracker tracker = new();
        tracker.ReplayToIndex(start, frames);

        Dictionary<int, float> maxPerAxisMove = new(); // serial → worst single-frame per-axis move
        Dictionary<int, int> seen = new();
        Dictionary<int, Vector3> prev = new();
        int steps = 0;

        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            steps++;
            foreach ((int _, EntityState e) in tracker.CurrentEntities.AllIndexed())
            {
                if (e.ClassName != "CSmokeGrenadeProjectile" || PositionUtil.CellToWorld(e) is not { } pos)
                {
                    continue;
                }

                seen[e.Serial] = seen.GetValueOrDefault(e.Serial) + 1;
                if (prev.TryGetValue(e.Serial, out Vector3 p))
                {
                    // Match SamePoint's PER-AXIS test (each axis < 0.5 ⇒ no append), not a summed distance.
                    float perAxis = MathF.Max(MathF.Max(MathF.Abs(pos.X - p.X), MathF.Abs(pos.Y - p.Y)),
                        MathF.Abs(pos.Z - p.Z));
                    maxPerAxisMove[e.Serial] = MathF.Max(maxPerAxisMove.GetValueOrDefault(e.Serial), perAxis);
                }

                prev[e.Serial] = pos;
            }
        }

        // Persistent smokes = present across (almost) the whole window, the landed cloud(s).
        List<int> persistent = seen.Where(kv => kv.Value >= steps - 2).Select(kv => kv.Key).ToList();
        if (persistent.Count == 0)
        {
            throw new SkipTestException("no smoke persisted the full post-detonation window");
        }

        float mostStationary = persistent.Select(s => maxPerAxisMove.GetValueOrDefault(s, 0f)).Min();
        Console.WriteLine($"[landed-smoke] persistent={persistent.Count} " +
                          $"mostStationary maxPerAxisMove={mostStationary:F4}u over {steps} frames");

        await Assert.That(mostStationary).IsLessThan(0.5f); // < the SamePoint threshold ⇒ no re-append ⇒ fades
    }

    private static int FirstEventFrame(IReadOnlyList<DemoFrame> frames, string name)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].InnerMessages.Any(m => m is GameEventMessage gem &&
                                                 gem.DecodedEvent.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    // ── Minimal harness: a context whose Entities/snapshot are backed by the REAL entity view. ──

    private sealed class Ctx : IModuleContext
    {
        public List<PlayerRosterEntry> Roster { get; } = new();

        public bool HasDemo => true;
        public string? DemoPath => null;
        public int TickRate => 64;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1;
        public double CurtimeSeconds(int tick) => tick / 64.0;

        public void RequestSeekToFrame(int frameIndex)
        {
        }

        public void RequestSeekToTick(int tick)
        {
        }

        public void RequestPlay()
        {
        }

        public void RequestPause()
        {
        }

        public event Action<IPlaybackSnapshot>? Advanced;
        public IReadOnlyEntityView Entities { get; } = new ReadOnlyEntityView(new EntitySet());
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers { get; } = new List<IPlayerState>();
        public void Push(Snap snap) => Advanced?.Invoke(snap);
    }

    private sealed class Snap : IPlaybackSnapshot
    {
        public Snap(int frameIndex, int tick, IReadOnlyEntityView entities)
        {
            FrameIndex = frameIndex;
            Tick = tick;
            Entities = entities;
        }

        public int FrameIndex { get; }
        public int Tick { get; }
        public IReadOnlyEntityView Entities { get; }
        public IReadOnlyList<IPlayerState> Players { get; } = new List<IPlayerState>();
    }
}
