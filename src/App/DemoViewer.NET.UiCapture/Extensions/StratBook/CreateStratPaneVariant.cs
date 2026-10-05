#region

using System.Numerics;
using Avalonia.Controls;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The 2D Playback tab with the Create Strat review open in the contributed side pane: the
///     pack's contribution attached through the tab's surface, the pane opened on a synthetic round 7
///     capture, and the walk already back so the side, the slots and the step list show.
/// </summary>
public static partial class Variants
{
    private static Playback2DView Playback2DCreateStratPane()
    {
        const int freeze = 6400;
        const int rate = 64;
        PaneContext ctx = new();
        CreateStratPlaybackContribution contribution = new();
        StratBookPack pack = new();
        PlaybackContributionHost host = new([(pack, [new SdkPlaybackContribution(contribution, ExtensionGuard.Standalone(pack))])], null);
        Playback2DTabViewModel vm = new() { Contributions = host };
        vm.OnActivated(ctx);

        // Ours are slots 0 to 4 (T), theirs 5 to 9 (CT); a smoke from slot 1 at 12 s after it moved up.
        static CapturedPawn Pawn(int slot, float x, float y) =>
            new(slot, slot < 5 ? 2 : 3, (ulong)(100 + slot), _paneNames[slot], x, y, 0, 90, slot < 5 ? "TSpawn" : "CTSpawn");
        List<CapturedPawn> spawn = [.. Enumerable.Range(0, 10).Select(s => Pawn(s, s < 5 ? -1200 + s * 80 : 1400 + (s - 5) * 80, s < 5 ? -1900 : 2200))];
        List<CapturedPawn> moved = [.. spawn.Select(p => p.PlayerSlot == 1 ? p with { X = -900, Y = -1500, Place = "OutsideLong" } : p)];
        RoundCapture capture = new(7, freeze, freeze + 100 * rate, rate,
        [
            new CaptureMoment(freeze, CaptureTrigger.FreezeEnd, spawn),
            new CaptureMoment(freeze + 12 * rate, CaptureTrigger.Utility, moved, "smoke", 1, 2, new Vector3(-400, 1200, 0)),
            new CaptureMoment(freeze + 40 * rate, CaptureTrigger.Plant, moved, null, 3, 2)
        ]);
        StratCaptureRequest request = new("de_dust2", 7, freeze, freeze + 120 * rate, "ab", "match730_capture_dust2.dem", null,
            ["100", "101", "102"], StratOwner.Me(), "me", StratOwner.MeKind, StratFromRound.QuantizedLevel);

        contribution.OpenWith(request, (_, _) => capture, new StratCaptureHost(() => null, new StratStore(null), null));

        // The walk runs on the pool and reports synchronously (no post): wait for it before anything binds.
        ((CreateStratDialogViewModel)vm.Surface.SidePane!).Walking.GetAwaiter().GetResult();

        return new Playback2DView { DataContext = vm };
    }

    private static readonly string[] _paneNames =
        ["b1t", "iM", "w0nderful", "jL", "Aleksib", "ropz", "Twistzz", "broky", "frozen", "karrigan"];

    // A loaded de_dust2 demo as the tab sees it: 24 rounds across 90 000 frames and the ten players, no live
    // states (the review is the subject; the viewport and cards stay at their no-push state).
    private sealed class PaneContext : IModuleContext
    {
        private static readonly int[] _freezeEnds = [.. Enumerable.Range(0, 24).Select(r => 1200 + r * 3700)];

        public bool HasDemo => true;
        public string? DemoPath => "/demos/match730_capture_dust2.dem";
        public string? MapName => "de_dust2";
        public string? DemoSha256 => "ab";
        public int TickRate => 64;
        public int CurrentFrameIndex => _freezeEnds[6] + 12 * 32;
        public int CurrentTick => CurrentFrameIndex * 2;
        public bool IsPlaying => false;
        public double Speed => 1;
        public int TotalFrames => 90_000;
        public int FirstTick => 1;
        public int LastTick => 180_000;
        public IReadOnlyEntityView Entities { get; } = new NoEntities();
        public IReadOnlyList<PlayerRosterEntry> Players { get; } =
            [.. _paneNames.Select((n, i) => new PlayerRosterEntry { Slot = i, Name = n, SteamId = (ulong)(100 + i) })];
        public IReadOnlyList<IPlayerState> CurrentPlayers => [];
        public IReadOnlyCollection<string> AvailableEventNames => ["round_freeze_end"];
        public double CurtimeSeconds(int tick) => tick / 64.0;
        public int FrameIndexAtTick(int tick) => tick / 2 >= TotalFrames ? -1 : tick / 2;
        public IReadOnlyList<int> EventFrames(string eventName) => eventName == "round_freeze_end" ? _freezeEnds : [];

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

        public event Action<IPlaybackSnapshot>? Advanced
        {
            add { }
            remove { }
        }

        private sealed class NoEntities : IReadOnlyEntityView
        {
            public IEnumerable<IReadOnlyEntity> All() => [];
            public IEnumerable<IReadOnlyEntity> OfClass(string className) => [];
            public IReadOnlyEntity? BySerial(int serial) => null;
            public IReadOnlyEntity? ByIndex(int entityIndex) => null;
            public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
        }
    }
}
