#region

using System.Globalization;
using System.Numerics;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The frame builder against in-memory entities — the same behaviour the App's Playback2D suite
///     covers end to end, asserted here without an Avalonia platform in the process.
/// </summary>
[NotInParallel]
public class SceneFrameBuilderTests
{
    private const int TickRate = 64;

    // Precomputed so the label lookup allocates nothing: the steady-state allocation gate is about the
    // builder, and a label formatted per player per frame would swamp it.
    private static readonly string[] _labels =
        [.. Enumerable.Range(0, 16).Select(i => "P" + i.ToString(CultureInfo.InvariantCulture))];

    [Test]
    public async Task Markers_MatchPlayerState_ForAliveAndDead()
    {
        SceneFrameBuilder builder = new();
        FakeEntity pawn = new FakeEntity("CCSPlayerPawn")
            .With("m_iHealth", 87)
            .With("m_angEyeAngles", new Vector3(-11f, 42f, 0f))
            .With("m_pMovementServices.m_flDuckAmount", 0.25f);

        FakePlayer alive = new()
        {
            Slot = 0,
            Team = 2,
            Pawn = pawn,
            WorldPosition = (100f, -200f, 64f)
        };

        Scene2DFrame frame = Build(builder, Input([alive], new FakeEntityView(), 10, 640));

        await Assert.That(frame.Markers.Count).IsEqualTo(1);
        PlayerMarker marker = frame.Markers[0];
        await Assert.That(marker.Slot).IsEqualTo(0);
        await Assert.That(marker.Team).IsEqualTo(2);
        await Assert.That(marker.WorldX).IsEqualTo(100f);
        await Assert.That(marker.WorldY).IsEqualTo(-200f);
        await Assert.That(marker.IsAlive).IsTrue();
        await Assert.That(marker.YawDegrees).IsEqualTo(42f); // yaw = .Y, pitch = .X
        await Assert.That(marker.PitchDegrees).IsEqualTo(-11f);
        await Assert.That(marker.DuckAmount).IsEqualTo(0.25f);
        await Assert.That(marker.Label).IsEqualTo("P0");
        await Assert.That(marker.SteamId).IsEqualTo(76561197960265728UL);

        // The pawn orphans on death: no live position this tick, so the death marker holds the last
        // known spot with a Dead ring rather than vanishing.
        FakeEntity deadPawn = new FakeEntity("CCSPlayerPawn").With("m_iHealth", 0).With("m_lifeState", 1);
        FakePlayer dead = new()
        {
            Slot = 0,
            Team = 2,
            HasLivePawn = false,
            Pawn = deadPawn
        };

        frame = Build(builder, Input([dead], new FakeEntityView(), 11, 704));

        await Assert.That(frame.Markers.Count).IsEqualTo(1);
        await Assert.That(frame.Markers[0].Ring).IsEqualTo(RingState.Dead);
        await Assert.That(frame.Markers[0].IsAlive).IsFalse();
        await Assert.That(frame.Markers[0].WorldX).IsEqualTo(100f);
        await Assert.That(frame.Markers[0].WorldY).IsEqualTo(-200f);
    }

    [Test]
    public async Task AreaEffects_DetonatedSmokesAndBurningCellsOnly()
    {
        SceneFrameBuilder builder = new();

        FakeEntityView view = new FakeEntityView()
            .Add(new FakeEntity("CSmokeGrenadeProjectile")
                .With("m_nSmokeEffectTickBegin", 100)
                .With("m_vSmokeDetonationPos", new Vector3(370f, -1058f, -389f)))
            .Add(new FakeEntity("CSmokeGrenadeProjectile", 2) // still flying → excluded
                .With("m_nSmokeEffectTickBegin", 0)
                .With("m_vSmokeDetonationPos", new Vector3(999f, 999f, 0f)))
            .Add(new FakeEntity("CInferno")
                .With("m_fireCount", 3)
                .With("m_bFireIsBurning[0]", 1)
                .With("m_firePositions[0]", new Vector3(36f, -2212f, -413f))
                .With("m_bFireIsBurning[1]", 0) // not burning → excluded
                .With("m_firePositions[1]", new Vector3(5f, 5f, 0f))
                .With("m_bFireIsBurning[2]", 1)
                .With("m_firePositions[2]", new Vector3(-46f, -2226f, -416f)));

        Scene2DFrame frame = Build(builder, Input([], view, 1, 64));

        await Assert.That(frame.AreaEffects.Count(a => a.Kind == AreaEffectKind.Smoke)).IsEqualTo(1);
        await Assert.That(frame.AreaEffects.Count(a => a.Kind == AreaEffectKind.Fire)).IsEqualTo(2);
        await Assert.That(frame.AreaEffects.Single(a => a.Kind == AreaEffectKind.Smoke).WorldRadius)
            .IsEqualTo(144f);
        await Assert.That(frame.AreaEffects.Any(a => a.Kind == AreaEffectKind.Fire && a.WorldX == 5f))
            .IsFalse();
    }

    [Test]
    public async Task Trails_AccumulateThenFadeThenPrune()
    {
        SceneFrameBuilder builder = new();
        FakeEntity nade = new FakeEntity("CHEGrenadeProjectile", 42);

        // Four moving samples → a visible polyline; the projectile moved on the last one, so alpha holds.
        Scene2DFrame frame = Scene2DFrame.Empty;
        for (int i = 0; i < 4; i++)
        {
            nade.AtWorld(100f + i * 50, 0f, 64f);
            frame = Build(builder, Input([], new FakeEntityView().Add(nade), i, i * TickRate));
        }

        await Assert.That(frame.Trails.Count).IsEqualTo(1);
        await Assert.That(frame.Trails[0].Points.Count).IsEqualTo(4);
        await Assert.That(frame.Trails[0].Kind).IsEqualTo(GrenadeKind.He);
        await Assert.That(frame.Trails[0].Alpha).IsEqualTo(1.0);

        // The projectile is gone. The fade runs on time since the last MOVE over a 2 s window, so one
        // second later it is half faded and still drawn...
        int lastMoveTick = 3 * TickRate;
        frame = Build(builder, Input([], new FakeEntityView(), 4, lastMoveTick + TickRate));
        await Assert.That(frame.Trails.Count).IsEqualTo(1);
        await Assert.That(frame.Trails[0].Alpha).IsEqualTo(0.5).Within(0.001);

        // ...and past the window it is pruned outright.
        frame = Build(builder, Input([], new FakeEntityView(), 5, lastMoveTick + 3 * TickRate));
        await Assert.That(frame.Trails.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Trails_ClearOnDiscontinuity_AndTheFrameSaysSo()
    {
        SceneFrameBuilder builder = new();
        FakeEntity nade = new FakeEntity("CFlashbangProjectile", 7);
        for (int i = 0; i < 3; i++)
        {
            nade.AtWorld(i * 40f, 0f, 64f);
            Build(builder, Input([], new FakeEntityView().Add(nade), i, i * TickRate));
        }

        await Assert.That(Build(builder, Input([], new FakeEntityView().Add(nade), 3, 3 * TickRate))
            .Trails.Count).IsEqualTo(1);

        // A forward seek well past a normal push: without the clear, the polyline would streak from the
        // pre-seek point to the post-seek point.
        Scene2DFrame afterSeek = Build(builder, Input([], new FakeEntityView(), 3 + 500,
            (3 + 500) * TickRate));

        await Assert.That(afterSeek.Trails.Count).IsEqualTo(0);
        await Assert.That(afterSeek.Time.IsDiscontinuity).IsTrue();
    }

    [Test]
    public async Task BackwardSeek_ResetsRingHistory_AndIsFlaggedDiscontinuous()
    {
        SceneFrameBuilder builder = new();
        FakeEntity pawn = new FakeEntity("CCSPlayerPawn").With("m_iHealth", 100);
        FakePlayer player = new()
        {
            Slot = 0,
            Pawn = pawn,
            WorldPosition = (0f, 0f, 64f)
        };

        Build(builder, Input([player], new FakeEntityView(), 10, 640));

        // Damage on the next forward frame lights the ring...
        pawn.With("m_iHealth", 40);
        Scene2DFrame hit = Build(builder, Input([player], new FakeEntityView(), 11, 704));
        await Assert.That(hit.Markers[0].Ring).IsEqualTo(RingState.TakingDamage);

        // ...but stepping BACKWARD must not manufacture a flash off a stale prior sample.
        pawn.With("m_iHealth", 100);
        Scene2DFrame back = Build(builder, Input([player], new FakeEntityView(), 5, 320));
        await Assert.That(back.Time.IsDiscontinuity).IsTrue();
        await Assert.That(back.Markers[0].Ring).IsEqualTo(RingState.Team);
    }

    [Test]
    public async Task Bomb_DetonationFraction_TracksC4Blow()
    {
        SceneFrameBuilder builder = new();
        FakeEntity c4 = new FakeEntity("CPlantedC4")
            .With("m_bBombTicking", 1)
            .With("m_flC4Blow", 140f)
            .With("m_flTimerLength", 40f);
        c4.AtWorld(300f, -150f, 64f);

        // The bomb timers are read only when the game-rules entity decoded this frame — the pre-v2
        // view-model nested UpdateBombTimers inside that branch, and this extraction keeps it there.
        FakeEntity rules = new FakeEntity("CCSGameRulesProxy").With("m_pGameRules.m_bBombPlanted", 1);

        // 20 s before detonation, on a 40 s timer → half the ring left.
        Scene2DFrame frame = Build(builder,
            Input([], new FakeEntityView().Add(rules).Add(c4), 1, 64, curtime: 120));

        await Assert.That(frame.Bomb).IsNotNull();
        await Assert.That(frame.Bomb!.Value.DetonationFraction).IsEqualTo(0.5).Within(0.001);
        await Assert.That(frame.Bomb!.Value.WorldX).IsEqualTo(300f);
        await Assert.That(frame.GameInfo.BombTicking).IsTrue();
        await Assert.That(frame.GameInfo.RoundSeconds).IsEqualTo(20.0).Within(0.001);
        await Assert.That(frame.GameInfo.RoundTime).IsEqualTo("0:20");

        // A defuse in progress fills the second timer and the defuse arc.
        c4.With("m_bBeingDefused", 1).With("m_flDefuseCountDown", 125f).With("m_flDefuseLength", 5f);
        frame = Build(builder, Input([], new FakeEntityView().Add(rules).Add(c4), 2, 128, curtime: 120));

        await Assert.That(frame.GameInfo.DefuseInProgress).IsTrue();
        await Assert.That(frame.GameInfo.DefuseKitNote).IsEqualTo("with kit");
        await Assert.That(frame.Bomb!.Value.BeingDefused).IsTrue();
        await Assert.That(frame.Bomb!.Value.DefuseFraction).IsEqualTo(1.0).Within(0.001);
    }

    [Test]
    public async Task GameInfo_RoundClock_UsesNetworkedRoundTime()
    {
        SceneFrameBuilder builder = new();
        FakeEntityView view = new FakeEntityView()
            .Add(new FakeEntity("CCSGameRulesProxy")
                .With("m_pGameRules.m_totalRoundsPlayed", 4)
                .With("m_pGameRules.m_fRoundStartTime", 300f)
                .With("m_pGameRules.m_iRoundTime", 115))
            .Add(new FakeEntity("CCSTeam", 2).With("m_iTeamNum", 2).With("m_iScore", 3))
            .Add(new FakeEntity("CCSTeam", 3).With("m_iTeamNum", 3).With("m_iScore", 1));

        Scene2DFrame frame = Build(builder, Input([], view, 1, 64, curtime: 350));

        await Assert.That(frame.GameInfo.Phase).IsEqualTo("Live");
        await Assert.That(frame.GameInfo.RoundNumber).IsEqualTo(5);
        await Assert.That(frame.GameInfo.RoundsPlayed).IsEqualTo(4);
        await Assert.That(frame.GameInfo.RoundSeconds).IsEqualTo(65.0).Within(0.001);
        await Assert.That(frame.GameInfo.RoundTime).IsEqualTo("1:05");
        await Assert.That(frame.GameInfo.TScore).IsEqualTo(3);
        await Assert.That(frame.GameInfo.CtScore).IsEqualTo(1);
    }

    [Test]
    public async Task GameInfo_WithoutRulesEntity_KeepsThePreviousRoundState()
    {
        SceneFrameBuilder builder = new();
        FakeEntityView withRules = new FakeEntityView()
            .Add(new FakeEntity("CCSGameRulesProxy")
                .With("m_pGameRules.m_bFreezePeriod", 1)
                .With("m_pGameRules.m_totalRoundsPlayed", 2));

        Scene2DFrame frame = Build(builder, Input([], withRules, 1, 64));
        await Assert.That(frame.GameInfo.Phase).IsEqualTo("Freeze");

        // A frame in which the rules entity is not decoded (a seek can land there) must leave the panel
        // alone rather than blanking it — the pre-v2 view-model mutated its GameInfo in place.
        frame = Build(builder, Input([], new FakeEntityView(), 2, 128));
        await Assert.That(frame.GameInfo.Phase).IsEqualTo("Freeze");
        await Assert.That(frame.GameInfo.RoundNumber).IsEqualTo(3);
    }

    [Test]
    public async Task Map_SectionHeights_AreReadOnce_AndAscendingOnly()
    {
        SceneFrameBuilder builder = new();
        FakeEntityView view = new FakeEntityView().Add(new FakeEntity("CCSGameRulesProxy")
            .With("m_pGameRules.m_MinimapVerticalSectionHeights[0]", 1.81f)
            .With("m_pGameRules.m_MinimapVerticalSectionHeights[1]", 51.54f)
            .With("m_pGameRules.m_MinimapVerticalSectionHeights[2]", 287f)
            .With("m_pGameRules.m_MinimapVerticalSectionHeights[3]", 3.4e38f) // sentinel → stop
            .With("m_pGameRules.m_vMinimapMins", new Vector3(-2573f, -1497f, 0f))
            .With("m_pGameRules.m_vMinimapMaxs", new Vector3(2043f, 3358f, 0f)));

        Scene2DFrame frame = Build(builder, Input([], view, 1, 64));

        await Assert.That(frame.Map.SectionHeights).IsNotNull();
        await Assert.That(frame.Map.SectionHeights!.Count).IsEqualTo(3);
        await Assert.That(frame.Map.NetworkedBounds).IsNotNull();
        await Assert.That(frame.Map.NetworkedBounds!.Value.MinX).IsEqualTo(-2573.0).Within(0.01);

        // Read-once: the second frame publishes the SAME SceneMapInfo instance, which is what keeps a
        // steady-state push allocation-free.
        Scene2DFrame second = Build(builder, Input([], view, 2, 128));
        await Assert.That(ReferenceEquals(second.Map, frame.Map)).IsTrue();
    }

    [Test]
    public async Task Reset_ClearsTrailsRingsAndSectionHeights()
    {
        SceneFrameBuilder builder = new();
        FakeEntity nade = new FakeEntity("CDecoyProjectile", 3);
        for (int i = 0; i < 3; i++)
        {
            nade.AtWorld(i * 30f, 0f, 64f);
            Build(builder, Input([], new FakeEntityView().Add(nade), i, i * TickRate));
        }

        await Assert.That(Build(builder, Input([], new FakeEntityView().Add(nade), 3, 3 * TickRate))
            .Trails.Count).IsEqualTo(1);

        builder.Reset();

        Scene2DFrame after = Build(builder, Input([], new FakeEntityView(), 3, 3 * TickRate));
        await Assert.That(after.Trails.Count).IsEqualTo(0);
        await Assert.That(after.Map.SectionHeights).IsNull();

        // The first build after a reset is not a seek: there is no previous frame to have jumped from.
        await Assert.That(after.Time.IsDiscontinuity).IsFalse();
    }

    [Test]
    public async Task Build_PublishesAlternatingDoubleBufferedFrames()
    {
        SceneFrameBuilder builder = new();
        Scene2DFrame a = Build(builder, Input([], new FakeEntityView(), 1, 64));
        Scene2DFrame b = Build(builder, Input([], new FakeEntityView(), 2, 128));
        Scene2DFrame c = Build(builder, Input([], new FakeEntityView(), 3, 192));

        await Assert.That(ReferenceEquals(a, b)).IsFalse();
        await Assert.That(ReferenceEquals(a, c)).IsTrue();
    }

    [Test]
    public async Task Build_TwiceInARow_AllocatesUnderTheSteadyStateBudget()
    {
        SceneFrameBuilder builder = new();
        FakeEntity pawn = new FakeEntity("CCSPlayerPawn").With("m_iHealth", 100);
        List<IPlayerState> players = [];
        for (int slot = 0; slot < 10; slot++)
        {
            players.Add(new FakePlayer
            {
                Slot = slot,
                Team = slot < 5 ? 2 : 3,
                Pawn = pawn,
                WorldPosition = (slot * 10f, slot * -10f, 64f)
            });
        }

        // The rules entity publishes its section heights, so the once-per-demo read latches on the first
        // frame. A map that publishes NONE deliberately re-scans every frame (the array may simply not
        // be decoded yet after a seek) — that is pre-v2 behaviour carried over verbatim, and it is not
        // what "steady state" means.
        FakeEntityView view = new FakeEntityView().Add(new FakeEntity("CCSGameRulesProxy")
            .With("m_pGameRules.m_fRoundStartTime", 0f)
            .With("m_pGameRules.m_iRoundTime", 115)
            .With("m_pGameRules.m_MinimapVerticalSectionHeights[0]", 1.81f)
            .With("m_pGameRules.m_MinimapVerticalSectionHeights[1]", 287f));

        // Warm up: the pooled lists grow to size, the map info latches, and the clock string is cached.
        for (int i = 0; i < 64; i++)
        {
            Build(builder, Input(players, view, i, i * TickRate, curtime: 10));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++)
        {
            Build(builder, Input(players, view, 64 + i, (64 + i) * TickRate, curtime: 10));
        }

        long perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / 16;
        Console.WriteLine($"[alloc] {perBuild} bytes/build");

        // Measured at ~72 bytes: the builder's own frame path allocates nothing (pooled lists, a cached
        // SceneMapInfo, and a clock string keyed on the rounded second), and what remains is the boxed
        // enumerator IEnumerable<IReadOnlyEntity> costs per OfClass call — an entity-read-surface cost,
        // not the builder's. §6 makes ZERO a hard budget from B1's dv2d bench; until then this is B0's
        // risk-register R7 ceiling, set close enough to the measurement to catch a real regression.
        await Assert.That(perBuild).IsLessThan(128);
    }

    // SceneFrameInput is a ref struct, so an `in` parameter cannot bind a call's return value directly —
    // the implicit temporary would have no ref-safe scope. One local per call is what the language wants.
    private static Scene2DFrame Build(SceneFrameBuilder builder, SceneFrameInput input) =>
        builder.Build(in input);

    private static string LabelFor(int slot) =>
        slot >= 0 && slot < _labels.Length ? _labels[slot] : "P?";

    private static SceneFrameInput Input(IReadOnlyList<IPlayerState> players, IReadOnlyEntityView entities,
        int frameIndex, int tick, double curtime = 0) => new()
    {
        Players = players,
        Entities = entities,
        FrameIndex = frameIndex,
        Tick = tick,
        TickRate = TickRate,
        CurtimeSeconds = curtime,
        LabelForSlot = LabelFor,
        SteamIdForSlot = static slot => 76561197960265728UL + (ulong)slot
    };
}
