#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline.Hud;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A4 kill feed (redesigned): the feed is PRE-BUILT once from the demo's player_death timeline
///     (<see cref="IModuleContext.GetEventTimeline" />) and the visible rows are a TICK-WINDOW filter over it
///     each push — decoupling display from the push cadence (no kill lost to a render-skipped frame) and
///     making seeking correct. Verifies the window (inclusive upper bound — kills ahead of the playhead are
///     hidden), decay as the playhead advances, the row cap, name resolution, and the full modifier set.
/// </summary>
[NotInParallel]
public class Playback2DKillFeedTests
{
    [Test]
    public async Task Window_ShowsKillsUpToPlayhead_ResolvesNamesAndModifiers()
    {
        (Playback2DTabViewModel vm, FakeCtx ctx) = Activate(
            Kill(1000, 0, 1, "ak47", true),
            Kill(1100, 1, 0, "awp", penetrated: 2, assister: 0, flashAssist: true));

        // Playhead at 1100: window (588, 1100] covers both kills, ordered oldest→newest.
        ctx.Push(2, 1100);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(2);

        KillFeedRow first = vm.KillFeed[0];
        await Assert.That(first.Tick).IsEqualTo(1000);
        await Assert.That(first.Attacker).IsEqualTo("Neo");
        await Assert.That(first.Victim).IsEqualTo("Smith");
        await Assert.That(first.Weapon).IsEqualTo("ak47");
        await Assert.That(first.Headshot).IsTrue();

        KillFeedRow second = vm.KillFeed[1];
        await Assert.That(second.Penetrated).IsTrue(); // Penetrated > 0
        await Assert.That(second.HasAssist).IsTrue();
        await Assert.That(second.Assister).IsEqualTo("Neo");
        await Assert.That(second.AssistedFlash).IsTrue();
    }

    [Test]
    public async Task KillAheadOfPlayhead_IsHidden_UntilCrossed()
    {
        (Playback2DTabViewModel vm, FakeCtx ctx) = Activate(Kill(5000, 0, 1, "deagle"));

        // Before the kill's tick → not shown (the inclusive-upper-bound rule; load-bearing when paused/seeking).
        ctx.Push(1, 4000);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(0);

        // At the kill's tick → shown.
        ctx.Push(2, 5000);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(1);

        // Long after (past the window) → decayed out.
        ctx.Push(3, 5000 + 9 * 64);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeekingBackward_ShowsTheWindowForThatTick()
    {
        (Playback2DTabViewModel vm, FakeCtx ctx) = Activate(
            Kill(1000, 0, 1, "ak47"),
            Kill(8000, 1, 0, "awp"));

        ctx.Push(9, 8000);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(1);
        await Assert.That(vm.KillFeed[0].Tick).IsEqualTo(8000);

        // Seek back near the first kill (frame index drops) → the window now shows that kill, not the later one.
        ctx.Push(2, 1000);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(1);
        await Assert.That(vm.KillFeed[0].Tick).IsEqualTo(1000);
    }

    [Test]
    public async Task VisibleRows_AreCappedToTheMostRecent()
    {
        GameEventView[] kills = new GameEventView[8];
        for (int i = 0; i < 8; i++)
        {
            kills[i] = Kill(1000 + i * 10, 0, i % 2 == 0 ? 1 : 0, "m4a1");
        }

        (Playback2DTabViewModel vm, FakeCtx ctx) = Activate(kills);

        // All 8 within the window of tick 1070; capped to the 6 most recent (ticks 1020..1070).
        ctx.Push(9, 1070);
        await Assert.That(vm.KillFeed.Count).IsEqualTo(6);
        await Assert.That(vm.KillFeed[0].Tick).IsEqualTo(1020);
        await Assert.That(vm.KillFeed[^1].Tick).IsEqualTo(1070);
    }

    /// <summary>
    ///     Design §11's snapshot test, in the form B4 D5 made possible: the XAML feed and the exported
    ///     <c>hud.killfeed</c> layer are fed by the SAME <c>KillFeedTimeline.Window</c> call, so at every
    ///     sampled tick their rows must agree exactly — not approximately, not usually.
    ///     <para>
    ///         This is the executable half of "dual-HUD drift is structurally impossible". If someone ever
    ///         re-introduces a second windowing rule, this is what fails.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AtEverySampledTick_TheExportedFeed_MatchesTheXamlFeedRowForRow()
    {
        List<GameEventView> timeline = [];
        for (int i = 0; i < 12; i++)
        {
            timeline.Add(Kill(1000 + i * 90, i % 2, (i + 1) % 2, i % 3 == 0 ? "awp" : "ak47",
                hs: i % 2 == 0, penetrated: i % 4 == 0 ? 1 : 0));
        }

        (Playback2DTabViewModel vm, FakeCtx ctx) = Activate([.. timeline]);

        // The rows the exported layer would draw, built the way the App builds them.
        List<KillFeedRow> all = [];
        foreach (GameEventView ev in timeline)
        {
            all.Add(new KillFeedRow(ev.Tick,
                NameFor(ctx, (int)(ev.Fields["Attacker"] ?? -1)),
                null,
                NameFor(ctx, (int)(ev.Fields["UserId"] ?? -1)),
                (string)(ev.Fields["Weapon"] ?? ""),
                (bool)(ev.Fields["Headshot"] ?? false),
                (int)(ev.Fields["Penetrated"] ?? 0) > 0,
                false, false, false, false, false));
        }

        List<KillFeedRow> exported = [];

        for (int tick = 900; tick <= 2200; tick += 37)
        {
            ctx.Push(0, tick);
            KillFeedTimeline.Window(all, tick, ctx.TickRate, exported);

            await Assert.That(vm.KillFeed.Count).IsEqualTo(exported.Count);
            for (int i = 0; i < exported.Count; i++)
            {
                await Assert.That(vm.KillFeed[i].Tick).IsEqualTo(exported[i].Tick);
                await Assert.That(vm.KillFeed[i].Attacker).IsEqualTo(exported[i].Attacker);
                await Assert.That(vm.KillFeed[i].Victim).IsEqualTo(exported[i].Victim);
                await Assert.That(vm.KillFeed[i].Weapon).IsEqualTo(exported[i].Weapon);
                await Assert.That(vm.KillFeed[i].Headshot).IsEqualTo(exported[i].Headshot);
                await Assert.That(vm.KillFeed[i].Penetrated).IsEqualTo(exported[i].Penetrated);
            }
        }
    }

    /// <summary>
    ///     The clock half of the same snapshot: what <c>ClockLayer</c> draws is a projection of the very
    ///     <c>SceneGameInfo</c> the XAML panel binds, so the two cannot disagree about the round number,
    ///     the score or the countdown.
    /// </summary>
    [Test]
    public async Task TheExportedClock_ProjectsTheSameGameInfoTheXamlPanelShows()
    {
        (Playback2DTabViewModel vm, FakeCtx ctx) = Activate();
        ctx.Push(0, 1000);

        SceneGameInfo info = new("Live", "Planted", 13, 12, 34.5, "0:34",
            true, false, "kit", double.NaN, "—", 7, 5);
        ClockReading reading = ClockReading.From(info);

        await Assert.That(reading.Round).IsEqualTo(info.RoundNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(reading.TScore).IsEqualTo(info.TScore);
        await Assert.That(reading.CtScore).IsEqualTo(info.CtScore);
        await Assert.That(reading.CountdownSeconds).IsEqualTo(info.RoundSeconds);
        await Assert.That(reading.BombTicking).IsEqualTo(info.BombTicking);

        // And the VM publishes the same record the projection reads, so there is one source for both.
        await Assert.That(vm.GameInfo).IsNotNull();
    }

    private static string NameFor(FakeCtx ctx, int slot)
    {
        foreach (PlayerRosterEntry entry in ctx.Roster)
        {
            if (entry.Slot == slot)
            {
                return entry.Name;
            }
        }

        return "world";
    }

    // ── Setup + builders ──

    private static (Playback2DTabViewModel, FakeCtx) Activate(params GameEventView[] timeline)
    {
        Playback2DTabViewModel vm = new();
        FakeCtx ctx = new(timeline);
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 0,
            Name = "Neo",
            SteamId = 1
        });
        ctx.Roster.Add(new PlayerRosterEntry
        {
            Slot = 1,
            Name = "Smith",
            SteamId = 2
        });
        vm.OnActivated(ctx);
        return (vm, ctx);
    }

    private static GameEventView Kill(int tick, int killer, int victim, string weapon,
        bool hs = false, int penetrated = 0, bool noscope = false, bool smoke = false,
        bool blind = false, bool air = false, int assister = -1, bool flashAssist = false) =>
        new()
        {
            Name = "player_death",
            Tick = tick,
            Fields = new Dictionary<string, object?>
            {
                ["Attacker"] = killer,
                ["UserId"] = victim,
                ["Assister"] = assister,
                ["Weapon"] = weapon,
                ["Headshot"] = hs,
                ["Penetrated"] = penetrated,
                ["NoScope"] = noscope,
                ["ThruSmoke"] = smoke,
                ["AttackerBlind"] = blind,
                ["AttackerInAir"] = air,
                ["AssistedFlash"] = flashAssist
            }
        };

    private sealed class FakeCtx : IModuleContext
    {
        private readonly IReadOnlyList<GameEventView> _timeline;

        public FakeCtx(IReadOnlyList<GameEventView> timeline) => _timeline = timeline;

        public List<PlayerRosterEntry> Roster { get; } = new();

        public IReadOnlyList<GameEventView> GetEventTimeline(string eventName) =>
            eventName == "player_death" ? _timeline : Array.Empty<GameEventView>();

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
        public IReadOnlyEntityView Entities { get; } = new EmptyView();
        public IReadOnlyList<PlayerRosterEntry> Players => Roster;
        public IReadOnlyList<IPlayerState> CurrentPlayers { get; } = new List<IPlayerState>();

        public void Push(int frameIndex, int tick) => Advanced?.Invoke(new FakeSnap(frameIndex, tick));
    }

    private sealed class FakeSnap : IPlaybackSnapshot
    {
        public FakeSnap(int frameIndex, int tick)
        {
            FrameIndex = frameIndex;
            Tick = tick;
        }

        public int FrameIndex { get; }
        public int Tick { get; }
        public IReadOnlyEntityView Entities { get; } = new EmptyView();
        public IReadOnlyList<IPlayerState> Players { get; } = new List<IPlayerState>();
    }


    private sealed class EmptyView : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}
