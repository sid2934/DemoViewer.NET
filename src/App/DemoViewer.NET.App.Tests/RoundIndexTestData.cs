#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Synthetic inputs for the round index tests: a demo-shaped parse, Round Facts rows with the
///     two sides seated and kills placed by hand, position samples, and index documents built
///     directly for the query tests. No demo file anywhere; the real-demo variants are their own class.
/// </summary>
internal static class RoundIndexTestData
{
    internal static readonly int[] CtSlots = [1, 2, 3, 4, 5];
    internal static readonly int[] TSlots = [6, 7, 8, 9, 10];

    /// <summary>A two-frame parse at <paramref name="tickRate" /> whose last frame is <paramref name="lastTick" />.</summary>
    internal static ParsedDemo Demo(int tickRate = 64, int lastTick = 20000, string map = "de_nuke")
    {
        DemoFrame[] frames =
        [
            Frame(0, 1),
            Frame(1, lastTick)
        ];
        return SyntheticParsedDemo.Create(frames, mapName: map, tickCount: lastTick, tickInterval: 1f / tickRate);
    }

    internal static DemoFrame Frame(int number, int tick) => new()
    {
        CommandKind = EDemoCommands.DemPacket,
        FrameNumber = number,
        ServerTick = tick,
        HeaderLength = 0,
        RawLength = 0,
        RawStart = 0,
        IsCompressed = false
    };

    internal static RoundFactsRows Facts(params RoundFacts[] rounds) => new()
    {
        Schema = DemoCacheRecord.RoundFactsSchema,
        Clock = new RoundFactsClock
        {
            TickRate = 64,
            FrameCount = 2,
            FirstTick = 1,
            LastTick = 20000
        },
        Rounds = [.. rounds]
    };

    internal static RoundFacts Round(
        int number,
        int freezeEnd,
        int? end,
        bool live = true,
        int[]? ctSlots = null,
        int[]? tSlots = null,
        params KillStep[] kills)
    {
        ctSlots ??= CtSlots;
        tSlots ??= TSlots;
        return new RoundFacts
        {
            Number = number,
            IsLive = live,
            FreezeEndTick = freezeEnd,
            EndTick = end,
            EndSource = end is null ? RoundEndSource.None : RoundEndSource.WinStatus,
            WinnerSide = 3,
            EndReason = RoundEndReason.CTWin,
            Ct = new SideFacts
            {
                Side = 3,
                Slots = ctSlots,
                PlayersAtFreezeEnd = ctSlots.Length,
                BuyType = BuyType.Full
            },
            T = new SideFacts
            {
                Side = 2,
                Slots = tSlots,
                PlayersAtFreezeEnd = tSlots.Length,
                BuyType = BuyType.Full
            },
            Kills = [.. kills]
        };
    }

    internal static KillStep Kill(int tick, int victimSlot) => new()
    {
        Tick = tick,
        VictimSlot = victimSlot,
        VictimSide = Array.IndexOf(CtSlots, victimSlot) >= 0 ? 3 : 2
    };

    // Team follows the seat unless a test says otherwise, so a sample agrees with the round the
    // test built; a slot on neither side reads team 0, as a spectator would.
    internal static PositionSample Sample(int tick, int slot, string? place, float x = 0, float y = 0, float z = 0,
        int frame = 0, int? team = null, bool alive = true) =>
        new(frame, tick, slot, new Vector3(x, y, z), place, team ?? SideOf(slot), alive);

    private static int SideOf(int slot) =>
        Array.IndexOf(CtSlots, slot) >= 0 ? 3 : Array.IndexOf(TSlots, slot) >= 0 ? 2 : 0;

    /// <summary>Every seated slot at one tick, the CT side in <paramref name="ctPlace" /> and the T side in <paramref name="tPlace" />.</summary>
    internal static IEnumerable<PositionSample> Everyone(int tick, string ctPlace, string tPlace, int frame = 0)
    {
        foreach (int slot in CtSlots)
        {
            yield return Sample(tick, slot, ctPlace, frame: frame);
        }

        foreach (int slot in TSlots)
        {
            yield return Sample(tick, slot, tPlace, frame: frame);
        }
    }

    /// <summary>An index document built by hand for the query tests: one round per tuple, runs as given.</summary>
    internal static RoundIndexDocument Document(
        string map,
        string fingerprint,
        params (int Number, int FreezeEnd, int End, RoundIndexRun[] Runs)[] rounds)
    {
        RoundIndexDocument document = new()
        {
            Fingerprint = fingerprint,
            Clock = new RoundFactsClock
            {
                TickRate = 64,
                FrameCount = 2,
                FirstTick = 1,
                LastTick = 20000
            },
            Map = map,
            CadenceTicks = 64
        };
        foreach ((int number, int freezeEnd, int end, RoundIndexRun[] runs) in rounds)
        {
            document.Rounds.Add(new RoundIndexRound
            {
                Number = number,
                FreezeEndTick = freezeEnd,
                EndTick = end,
                Runs = [.. runs]
            });
        }

        return document;
    }

    /// <summary>A parsed record with Round Facts rows, the state the index evaluator wants.</summary>
    internal static DemoCacheRecord ParsedRecord(string path, string map = "de_nuke", string? sha = null,
        RoundFactsRows? facts = null, long modifiedTicks = 20) => new()
    {
        Path = path,
        Size = 10,
        ModifiedTicks = modifiedTicks,
        Sha256 = sha,
        Map = map,
        Parse = new TierStamp
        {
            Schema = DemoCacheRecord.ParseSchema,
            ComputedAtTicks = 1
        },
        RoundFacts = facts,
        RoundFactsFingerprint = facts is null ? null : "rf-A"
    };

    /// <summary>Writes a document and stamps its record Indexed, the way the evaluator leaves a demo.</summary>
    internal static void Indexed(DemoCacheStore store, RoundIndexStore sidecars, string path, RoundIndexDocument document,
        long computedAt = 100, long modifiedTicks = 20, string? sha = null)
    {
        sidecars.Write(path, document);
        DemoCacheRecord record = ParsedRecord(path, document.Map, sha, modifiedTicks: modifiedTicks);
        record.RoundIndex = new TierStamp
        {
            Schema = DemoCacheRecord.RoundIndexSchema,
            ComputedAtTicks = computedAt
        };
        record.RoundIndexState = RoundIndexState.Indexed;
        record.RoundIndexFingerprint = document.Fingerprint;
        record.RoundIndexRowCount = document.RowCount;
        store.Upsert(record);
    }
}
