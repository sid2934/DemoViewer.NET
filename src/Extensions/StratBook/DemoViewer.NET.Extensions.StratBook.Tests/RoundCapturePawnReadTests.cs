#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Create Strat From Round reads its pawns straight off the tracker. It must read the same pawns, in the same
///     order and with the same identities, as the export pipeline's scene snapshot reads for the same frame.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundCapturePawnReadTests
{
    [Test]
    public async Task TheCapturesPawnRead_MatchesTheSceneSnapshot_AcrossTheDemo()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        EntityTracker tracker = new EntitySeekService(static () => new EntityTracker()).SeekToFrameNoSnapshot(0, frames).Tracker;
        TrackerSceneSnapshot snapshot = new();
        Dictionary<int, ulong> steamIds = [];

        int compared = 0;
        int full = 0;
        int stride = Math.Max(1, frames.Count / 400);
        for (int frame = 0; frame < frames.Count; frame++)
        {
            if (frame > 0)
            {
                tracker.AdvanceOneFrame(frames[frame]);
            }

            if (frame % stride != 0)
            {
                continue;
            }

            snapshot.Refresh(tracker);
            List<CapturedPawn> expected = ThroughSnapshot(tracker, snapshot);
            List<CapturedPawn> actual = RoundCaptureWalker.ReadPawns(tracker, steamIds);

            await Assert.That(actual).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching)
                .Because($"frame {frame}");
            compared++;
            if (expected.Count == 10 && expected.All(p => p.SteamId != 0))
            {
                full++;
            }
        }

        await Assert.That(compared).IsGreaterThan(100);
        await Assert.That(full).IsGreaterThan(compared / 4).Because("live rounds fill a quarter of the demo or more, ten identified players each");
        GC.KeepAlive(demo);
    }

    // The same read through the export pipeline's scene snapshot, the reference the tracker read is held to.
    private static List<CapturedPawn> ThroughSnapshot(EntityTracker tracker, TrackerSceneSnapshot snapshot)
    {
        List<CapturedPawn> pawns = [];
        foreach (IPlayerState player in snapshot.Players)
        {
            if (!player.HasLivePawn || player.Pawn is not { } pawn || player.WorldPosition is not { } world
                || player.Team is not (2 or 3)
                || PawnLookup.ResolvePawn(tracker, player.Slot) is not { } resolved || !PawnLookup.IsAlive(resolved))
            {
                continue;
            }

            float yaw = pawn.TryGet("m_angEyeAngles", out Vector3 eye) ? eye.Y : 0;
            string? place = pawn.TryGet("m_szLastPlaceName", out string? name) && !string.IsNullOrEmpty(name) ? name : null;
            string? playerName = player.Controller?["m_iszPlayerName"] as string;
            pawns.Add(new CapturedPawn(player.Slot, player.Team, snapshot.SteamIdForSlot(player.Slot), playerName,
                world.X, world.Y, world.Z, yaw, place));
        }

        return pawns;
    }
}
