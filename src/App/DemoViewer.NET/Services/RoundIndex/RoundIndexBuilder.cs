#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     Folds one demo's position walk into its index document. Pure: no I/O, no cache, no UI. The
///     walk stays engine-owned (<see cref="PositionSampler.Walk" />) and yields every pawn with a
///     controller, dead or alive, so alive is decided from Round Facts <c>Kills</c> and side from
///     <c>Slots</c>: the engine sample carries neither.
///     <para>
///         Sampling: from each live round's freeze end, one row every cadence step while the tick is
///         below the round's end. Freeze time is spawns and the post-round win panel carries no situation
///         anyone searches for, so neither is sampled. A row is opened by the first sampled frame at or
///         past its due tick and takes every sample of that tick (several frames can share one, up to 39
///         on build 10231), later frames overwriting by slot; frames between two due ticks are skipped.
///     </para>
/// </summary>
public static class RoundIndexBuilder
{
    /// <summary>
    ///     Builds the document for <paramref name="demo" /> over its Round Facts rows. The demo block is
    ///     left for the caller, which has the record; the fingerprint is <paramref name="options" /> and
    ///     <paramref name="source" /> composed.
    /// </summary>
    /// <param name="demo">The held parse.</param>
    /// <param name="facts">The demo's Round Facts rows: the round windows, the sides and the kills.</param>
    /// <param name="options">The sampling parameters.</param>
    /// <param name="source">Which string names a sample's place.</param>
    /// <param name="samples">
    ///     The position walk to fold; null walks <paramref name="demo" /> through
    ///     <see cref="PositionSampler.Walk" /> at <see cref="RoundIndexOptions.FrameStride" />. Tests
    ///     hand in synthetic samples here; a synthetic parse carries no entity data to walk.
    /// </param>
    public static RoundIndexDocument Build(
        ParsedDemo demo,
        RoundFactsRows facts,
        RoundIndexOptions options,
        IPlaceSource source,
        IEnumerable<PositionSample>? samples = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        int cadenceTicks = options.CadenceTicks(demo.TickRate);
        int lastFrameTick = demo.Frames.Count > 0 ? demo.Frames[^1].ServerTick : 0;
        List<RoundWindow> windows = Windows(facts, lastFrameTick);

        RoundIndexDocument document = new()
        {
            Fingerprint = RoundIndexFingerprint.Compose(options, source),
            Clock = RoundFactsClock.From(FrameClock.IdentityFor(demo)),
            Map = demo.MapName,
            CadenceTicks = cadenceTicks,
            Rounds = [.. windows.Select(w => w.Round)]
        };

        Dictionary<string, PlaceSampleSummary> places = new(StringComparer.Ordinal);
        Dictionary<(string A, string B), int> transitions = [];

        samples ??= PositionSampler.Walk(demo, options.FrameStride);

        int current = 0;
        OpenRow? row = null;
        Dictionary<int, string> previousPlaces = [];

        foreach (PositionSample sample in samples)
        {
            // Advance to the round whose window can hold this tick. Windows are in round order and the
            // walk is in frame order, so a round left behind is never revisited.
            while (current < windows.Count && sample.Tick >= windows[current].EndTick)
            {
                CloseRow(ref row, windows[current], source, places, transitions, previousPlaces);
                previousPlaces.Clear();
                current++;
            }

            if (current >= windows.Count)
            {
                break;
            }

            RoundWindow window = windows[current];
            if (sample.Tick < window.FreezeEndTick)
            {
                continue;
            }

            if (row is { } open && sample.Tick == open.Tick)
            {
                open.Samples[sample.PlayerSlot] = sample;
                continue;
            }

            if (row is null || sample.Tick >= row.NextDueTick)
            {
                CloseRow(ref row, window, source, places, transitions, previousPlaces);
                int step = (sample.Tick - window.FreezeEndTick) / cadenceTicks;
                row = new OpenRow(step, sample.Tick, window.FreezeEndTick + (step + 1) * cadenceTicks);
                row.Samples[sample.PlayerSlot] = sample;
            }
        }

        if (current < windows.Count)
        {
            CloseRow(ref row, windows[current], source, places, transitions, previousPlaces);
        }

        document.Places = places;
        document.Transitions =
        [
            .. transitions
                .OrderByDescending(t => t.Value)
                .ThenBy(t => t.Key.A, StringComparer.Ordinal)
                .ThenBy(t => t.Key.B, StringComparer.Ordinal)
                .Select(t => new PlaceTransition(t.Key.A, t.Key.B, t.Value))
        ];
        return document;
    }

    /// <summary>
    ///     The sampled windows: every live round from its freeze end to its end. The end is the round's
    ///     <c>EndTick</c>, else the next round's freeze end (the record carries no freeze begin), else
    ///     one past the last frame.
    /// </summary>
    private static List<RoundWindow> Windows(RoundFactsRows facts, int lastFrameTick)
    {
        List<RoundFacts.RoundFacts> rounds = [.. facts.Rounds.OrderBy(r => r.Number)];
        List<RoundWindow> windows = [];
        for (int i = 0; i < rounds.Count; i++)
        {
            RoundFacts.RoundFacts round = rounds[i];
            if (!round.IsLive)
            {
                continue;
            }

            int end = round.EndTick
                      ?? (i + 1 < rounds.Count ? rounds[i + 1].FreezeEndTick : lastFrameTick + 1);
            if (end <= round.FreezeEndTick)
            {
                continue;
            }

            Dictionary<int, int> sideBySlot = [];
            foreach (int slot in round.Ct.Slots)
            {
                sideBySlot[slot] = 3;
            }

            foreach (int slot in round.T.Slots)
            {
                sideBySlot[slot] = 2;
            }

            // The first kill of a slot is its death tick; a later kill of the same slot in one round
            // cannot happen and is ignored rather than resurrecting anyone.
            Dictionary<int, int> deathTickBySlot = [];
            foreach (KillStep kill in round.Kills)
            {
                if (kill.VictimSlot >= 0 && !deathTickBySlot.ContainsKey(kill.VictimSlot))
                {
                    deathTickBySlot[kill.VictimSlot] = kill.Tick;
                }
            }

            windows.Add(new RoundWindow(
                new RoundIndexRound
                {
                    Number = round.Number,
                    FreezeEndTick = round.FreezeEndTick,
                    EndTick = end
                },
                sideBySlot,
                deathTickBySlot));
        }

        return windows;
    }

    // Encodes the open row's tokens, appends it to the round's runs and accumulates the summaries.
    private static void CloseRow(
        ref OpenRow? row,
        RoundWindow window,
        IPlaceSource source,
        Dictionary<string, PlaceSampleSummary> places,
        Dictionary<(string A, string B), int> transitions,
        Dictionary<int, string> previousPlaces)
    {
        if (row is not { } open)
        {
            return;
        }

        row = null;

        List<string?> ct = [];
        List<string?> t = [];
        Dictionary<int, string> currentPlaces = [];
        foreach ((int slot, PositionSample sample) in open.Samples)
        {
            if (!window.SideBySlot.TryGetValue(slot, out int side))
            {
                continue; // a spectator, or a slot Round Facts did not seat
            }

            if (window.DeathTickBySlot.TryGetValue(slot, out int deathTick) && deathTick <= open.Tick)
            {
                continue; // dead from the kill tick on
            }

            string? place = source.PlaceFor(in sample);
            (side == 3 ? ct : t).Add(place);
            if (place is null)
            {
                continue;
            }

            currentPlaces[slot] = place;
            Accumulate(places, place, sample);
            if (previousPlaces.TryGetValue(slot, out string? previous)
                && !string.Equals(previous, place, StringComparison.Ordinal))
            {
                (string A, string B) key = string.CompareOrdinal(previous, place) < 0
                    ? (previous, place)
                    : (place, previous);
                transitions[key] = transitions.GetValueOrDefault(key) + 1;
            }
        }

        // A slot without an alive, placed sample this row breaks its chain: a transition is between
        // consecutive rows, never across a gap.
        previousPlaces.Clear();
        foreach ((int slot, string place) in currentPlaces)
        {
            previousPlaces[slot] = place;
        }

        string ctToken = PlaceCountToken.EncodePlaces(ct);
        string tToken = PlaceCountToken.EncodePlaces(t);
        List<RoundIndexRun> runs = window.Round.Runs;
        if (runs.Count > 0)
        {
            RoundIndexRun last = runs[^1];
            if (last.ToStep == open.Step - 1
                && string.Equals(last.Ct, ctToken, StringComparison.Ordinal)
                && string.Equals(last.T, tToken, StringComparison.Ordinal))
            {
                runs[^1] = last with
                {
                    ToStep = open.Step
                };
                return;
            }
        }

        runs.Add(new RoundIndexRun(open.Step, open.Step, ctToken, tToken));
    }

    private static void Accumulate(Dictionary<string, PlaceSampleSummary> places, string place, PositionSample sample)
    {
        if (!places.TryGetValue(place, out PlaceSampleSummary? summary))
        {
            summary = new PlaceSampleSummary();
            places[place] = summary;
        }

        summary.Count++;
        // The bucket is the quantized SAMPLE Z, never a level key: level keys are band lower bounds a
        // floor rebuild can move, so the consumer folds buckets into whatever bands it has.
        int bucket = (int)MapSpace.QuantizeZ(sample.Position.Z);
        for (int i = 0; i < summary.Buckets.Count; i++)
        {
            PlaceZBucketSum existing = summary.Buckets[i];
            if (existing.ZBucket == bucket)
            {
                summary.Buckets[i] = existing with
                {
                    Count = existing.Count + 1,
                    SumX = existing.SumX + sample.Position.X,
                    SumY = existing.SumY + sample.Position.Y
                };
                return;
            }
        }

        summary.Buckets.Add(new PlaceZBucketSum(bucket, 1, sample.Position.X, sample.Position.Y));
    }

    private sealed record RoundWindow(
        RoundIndexRound Round,
        Dictionary<int, int> SideBySlot,
        Dictionary<int, int> DeathTickBySlot)
    {
        public int FreezeEndTick => Round.FreezeEndTick;

        public int EndTick => Round.EndTick;
    }

    private sealed class OpenRow(int step, int tick, int nextDueTick)
    {
        public int Step { get; } = step;

        public int Tick { get; } = tick;

        public int NextDueTick { get; } = nextDueTick;

        public Dictionary<int, PositionSample> Samples { get; } = [];
    }
}
