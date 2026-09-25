#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     Folds one demo's position walk into its index document and its positions file. Pure: no I/O,
///     no cache, no UI. The walk stays engine-owned (<see cref="PositionSampler.Walk" />) and yields
///     every pawn with a controller, dead or alive, with its own <see cref="PositionSample.IsAlive" />
///     and <see cref="PositionSample.Team" /> (CS2DemoKit #58): those two fields are the gate now.
///     Round Facts' <c>Kills</c> and <c>Slots</c> are kept only as a cross-check, folded into
///     <see cref="RoundIndexBuild.Disagreements" /> when a slot's sample disagrees with its row.
///     <para>
///         Sampling: from each live round's freeze end, one row every cadence step while the tick is
///         below the round's end. Freeze time is spawns and the post-round win panel carries no situation
///         anyone searches for, so neither is sampled. A row is opened by the first sampled frame at or
///         past its due tick and takes every sample of that tick (several frames can share one, up to 39
///         on build 10231), later frames overwriting by slot; frames between two due ticks are skipped.
///     </para>
///     <para>
///         The positions file is the same rows seen the other way round: the alive tuples a row's
///         tokens were encoded from, kept as integer world units and a place id. One close writes both,
///         which is what lets a thumbnail built from a tuple list agree byte for byte with the token the
///         search matched.
///     </para>
/// </summary>
public static class RoundIndexBuilder
{
    /// <summary>
    ///     Builds the index document for <paramref name="demo" /> over its Round Facts rows. The demo
    ///     block is left for the caller, which has the record; the fingerprint is
    ///     <paramref name="options" /> and <paramref name="source" /> composed. The positions file of the
    ///     same walk is discarded; <see cref="BuildWithPositions" /> keeps it.
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
        IEnumerable<PositionSample>? samples = null) =>
        BuildWithPositions(demo, facts, options, source, samples).Index;

    /// <summary>
    ///     <see cref="Build" />, keeping the positions file the walk produced beside the index. Both
    ///     carry the same fingerprint; the evaluator writes the positions first and the index second.
    /// </summary>
    /// <param name="demo">The held parse.</param>
    /// <param name="facts">The demo's Round Facts rows.</param>
    /// <param name="options">The sampling parameters.</param>
    /// <param name="source">Which string names a sample's place.</param>
    /// <param name="samples">The position walk to fold; null walks the parse.</param>
    public static RoundIndexBuild BuildWithPositions(
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
        int lastFrameTick = demo.Frames.Count > 0 ? demo.Frames[^1].GameTick ?? demo.Frames[^1].ServerTick : 0;
        List<RoundWindow> windows = Windows(facts, lastFrameTick);
        string fingerprint = RoundIndexFingerprint.Compose(options, source);

        RoundIndexDocument document = new()
        {
            Fingerprint = fingerprint,
            Clock = RoundFactsClock.From(FrameClock.IdentityFor(demo)),
            Map = demo.MapName,
            CadenceTicks = cadenceTicks,
            Rounds = [.. windows.Select(w => w.Round)]
        };
        RoundPositionsDocument positions = new()
        {
            Fingerprint = fingerprint,
            CadenceTicks = cadenceTicks,
            Rounds = [.. windows.Select(w => w.Positions)]
        };

        Dictionary<string, PlaceSampleSummary> places = new(StringComparer.Ordinal);
        Dictionary<(string A, string B), int> transitions = [];
        PlaceTable table = new(positions.Places);

        samples ??= PositionSampler.Walk(demo, options.FrameStride);

        int current = 0;
        OpenRow? row = null;
        Dictionary<int, string> previousPlaces = [];
        RoundIndexDisagreementTally tally = new();

        foreach (PositionSample sample in samples)
        {
            // Advance to the round whose window can hold this tick. Windows are in round order and the
            // walk is in frame order, so a round left behind is never revisited.
            while (current < windows.Count && sample.Tick >= windows[current].EndTick)
            {
                CloseRow(ref row, windows[current], source, places, transitions, previousPlaces, table, tally);
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
                CloseRow(ref row, window, source, places, transitions, previousPlaces, table, tally);
                int step = (sample.Tick - window.FreezeEndTick) / cadenceTicks;
                row = new OpenRow(step, sample.Tick, window.FreezeEndTick + (step + 1) * cadenceTicks);
                row.Samples[sample.PlayerSlot] = sample;
            }
        }

        if (current < windows.Count)
        {
            CloseRow(ref row, windows[current], source, places, transitions, previousPlaces, table, tally);
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
        return new RoundIndexBuild(document, positions, tally.ToDisagreements());
    }

    /// <summary>
    ///     The sampled windows: every live round from its freeze end to its end. The end is the round's
    ///     <c>EndTick</c>, else the next round's freeze end (the record carries no freeze begin), else
    ///     one past the last frame. Each window's <c>SideBySlot</c>/<c>DeathTickBySlot</c> are Round
    ///     Facts' seating and kills, kept only so <see cref="CloseRow" /> can cross-check them against
    ///     the sample's own <c>Team</c>/<c>IsAlive</c>, never to gate on. The positions round's
    ///     <c>Ct</c> starts empty for the same reason: <see cref="CloseRow" /> fills it from the samples
    ///     the tokens were bucketed by, so a thumbnail's split can never disagree with the token.
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
                new RoundPositionsRound
                {
                    Number = round.Number,
                    FreezeEndTick = round.FreezeEndTick
                },
                sideBySlot,
                deathTickBySlot));
        }

        return windows;
    }

    // Encodes the open row's tokens, appends it to the round's runs, keeps its alive tuples for the
    // positions file and accumulates the summaries. Alive and side come from the sample itself
    // (CS2DemoKit #58); the window's facts-derived SideBySlot/DeathTickBySlot are only a cross-check now,
    // tallied rather than trusted.
    private static void CloseRow(
        ref OpenRow? row,
        RoundWindow window,
        IPlaceSource source,
        Dictionary<string, PlaceSampleSummary> places,
        Dictionary<(string A, string B), int> transitions,
        Dictionary<int, string> previousPlaces,
        PlaceTable table,
        RoundIndexDisagreementTally tally)
    {
        if (row is not { } open)
        {
            return;
        }

        row = null;

        List<string?> ct = [];
        List<string?> t = [];
        List<RoundPosition> tuples = [];
        Dictionary<int, string> currentPlaces = [];
        foreach ((int slot, PositionSample sample) in open.Samples)
        {
            bool factsSeated = window.SideBySlot.TryGetValue(slot, out int factsSide);
            bool factsDead = window.DeathTickBySlot.TryGetValue(slot, out int deathTick) && deathTick <= open.Tick;

            if (!sample.IsAlive || sample.Team is not (2 or 3))
            {
                if (sample.IsAlive && factsSeated && !factsDead)
                {
                    // The sample counts a live pawn on neither playing side; the row's facts disagree.
                    tally.Record(window.Round.Number, sideMismatch: true, aliveMismatch: false);
                }

                continue; // dead, or not seated on a playing side
            }

            if (factsSeated && factsSide != window.SideBySample.GetValueOrDefault(slot, sample.Team))
            {
                tally.Record(window.Round.Number, sideMismatch: true, aliveMismatch: false);
            }

            if (factsDead)
            {
                tally.Record(window.Round.Number, sideMismatch: false, aliveMismatch: true);
            }

            // A slot's side is fixed by its first live sample in the window and holds for the round, the
            // way the row's seating used to: the positions file keeps one CT list per round, and a window
            // with no EndTick runs on into the win panel, where a halftime swap flips every Team. Token
            // and Ct read this one side, so a thumbnail can never split the dots against the token.
            if (!window.SideBySample.TryGetValue(slot, out int side))
            {
                side = sample.Team;
                window.SideBySample[slot] = side;
                if (side == 3)
                {
                    window.Positions.Ct = [.. window.Positions.Ct.Append(slot).Order()];
                }
            }

            string? place = source.PlaceFor(in sample);
            (side == 3 ? ct : t).Add(place);
            tuples.Add(new RoundPosition(slot,
                RoundIndexBuild.Quantize(sample.Position.X),
                RoundIndexBuild.Quantize(sample.Position.Y),
                RoundIndexBuild.Quantize(sample.Position.Z),
                table.IdOf(place)));
            if (string.IsNullOrEmpty(place))
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

        // Tuples in slot order so the file is stable for a fixture and a diff; the walk's dictionary
        // order is frame order, which is not.
        tuples.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        List<List<RoundPosition>> steps = window.Positions.Pos;
        while (steps.Count < open.Step)
        {
            steps.Add([]); // a step the walk did not sample keeps the index-is-step rule
        }

        steps.Add(tuples);

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
        RoundPositionsRound Positions,
        Dictionary<int, int> SideBySlot,
        Dictionary<int, int> DeathTickBySlot)
    {
        /// <summary>Each slot's side from its first live sample in the window; the side the tokens and <c>Ct</c> read.</summary>
        public Dictionary<int, int> SideBySample { get; } = [];

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

    // The per-document place table: first seen, first numbered, with the reserved "?" for a null place.
    private sealed class PlaceTable(List<string> places)
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

        public int IdOf(string? place)
        {
            string key = place ?? PlaceCountToken.NullPlace;
            if (!_ids.TryGetValue(key, out int id))
            {
                id = places.Count;
                places.Add(key);
                _ids[key] = id;
            }

            return id;
        }
    }
}
