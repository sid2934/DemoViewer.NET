#region

using System.Globalization;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The §7.2 golden fixture (suggested-tags.md): one real round's occupancy fold, its placed
///     detonations and the map's learned region table, with the proposals the shipped profile makes
///     from them. No demo bytes. The snapshot test replays the proposals from the committed fold on
///     every run; the real-demo test recaptures the fold from the replay and holds it to the file.
/// </summary>
internal sealed class SuggestedTagsGolden
{
    internal const int SchemaVersion = 1;

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public int Schema { get; set; } = SchemaVersion;

    public string Note { get; set; } =
        "suggested-tags.md §7.2: one real round's occupancy (runs of 'from-to:place' seconds per slot, '?' alive and unplaced, " +
        "gaps dead), its detonations placed by the walk's cloud, and the map's region table learned from the same demo. " +
        "Rows are synthesised from the demo's own events on purpose, so the fixture is pinned to them; the engine's own " +
        "Round Facts rows close a round earlier and keep every round, so their fold is pinned apart under engine-rows/. " +
        "Recapture with ST_GOLDEN_UPDATE=1.";

    /// <summary>The note a fixture under <see cref="EngineRowsFixtureDirectory" /> carries.</summary>
    internal const string EngineRowsNote =
        "suggested-tags.md §7.2: the same round as ../<map>.json, folded over the engine's own Round Facts rows (the shipped " +
        "round_facts ruleset), which end the round at round_decided rather than round_officially_ended. Recapture with ST_GOLDEN_UPDATE=1.";

    public string Demo { get; set; } = "";

    public string Map { get; set; } = "";

    public int TickRate { get; set; }

    public JsonNode? RegionTable { get; set; }

    public GoldenRound Round { get; set; } = new();

    public List<GoldenEvent> Events { get; set; } = [];

    public List<GoldenProposal> Proposals { get; set; } = [];

    internal sealed class GoldenRound
    {
        public int Number { get; set; }

        public int StartTick { get; set; }

        public int EndTick { get; set; }

        public RoundBomb? Bomb { get; set; }

        /// <summary>Slot to side.</summary>
        public SortedDictionary<int, int> Sides { get; set; } = [];

        /// <summary>Slot to runs of <c>from-to:place</c>, seconds inclusive.</summary>
        public SortedDictionary<int, List<string>> Places { get; set; } = [];
    }

    internal sealed record GoldenEvent(int Tick, string Kind, int Slot, float X, float Y, float Z, string? Place);

    internal sealed record GoldenEvidence(string Kind, int Tick, string Text);

    internal sealed record GoldenProposal(
        string Id,
        string Detector,
        string Code,
        int Round,
        int Side,
        int FromTick,
        int ToTick,
        int TriggerTick,
        double Confidence,
        SortedDictionary<string, double> Factors,
        SortedDictionary<string, string> Labels,
        List<GoldenEvidence> Evidence);

    /// <summary>The fold, events and table of one round, without proposals.</summary>
    internal static SuggestedTagsGolden Capture(string demo, string map, int tickRate, SiteRegionTable table,
        RoundOccupancy round, IEnumerable<PlacedEvent> events)
    {
        SuggestedTagsGolden golden = new()
        {
            Demo = demo,
            Map = map,
            TickRate = tickRate,
            RegionTable = JsonNode.Parse(table.ToJson()),
            Round = new GoldenRound
            {
                Number = round.Round,
                StartTick = round.StartTick,
                EndTick = round.EndTick,
                Bomb = round.Bomb
            },
            Events =
            [
                .. events
                    .Where(e => e.Tick >= round.StartTick && e.Tick < round.EndTick)
                    .Select(e => new GoldenEvent(e.Tick, e.Kind, e.ThrowerSlot,
                        MathF.Round(e.Position.X, 1), MathF.Round(e.Position.Y, 1), MathF.Round(e.Position.Z, 1), e.Place))
            ]
        };

        foreach (int side in (int[])[2, 3])
        {
            foreach (int slot in round.Slots(side))
            {
                golden.Round.Sides[slot] = side;
                List<string> runs = [];
                int? from = null;
                string? current = null;
                for (int s = 0; s <= round.Seconds; s++)
                {
                    string? entry = s < round.Seconds ? Entry(round, slot, s) : null;
                    if (entry == current && s < round.Seconds)
                    {
                        continue;
                    }

                    if (current is not null && from is { } start)
                    {
                        runs.Add(string.Create(CultureInfo.InvariantCulture, $"{start}-{s - 1}:{current}"));
                    }

                    current = entry;
                    from = s;
                }

                golden.Round.Places[slot] = runs;
            }
        }

        return golden;
    }

    /// <summary>The round as the detectors read it, rebuilt from the file.</summary>
    internal RoundOccupancy Occupancy()
    {
        int seconds = (Round.EndTick - Round.StartTick + TickRate - 1) / TickRate;
        Dictionary<int, string?[]> places = [];
        foreach ((int slot, List<string> runs) in Round.Places)
        {
            string?[] bySecond = new string?[seconds];
            foreach (string run in runs)
            {
                int colon = run.IndexOf(':', StringComparison.Ordinal);
                string[] span = run[..colon].Split('-');
                for (int s = int.Parse(span[0], CultureInfo.InvariantCulture); s <= int.Parse(span[1], CultureInfo.InvariantCulture); s++)
                {
                    bySecond[s] = run[(colon + 1)..];
                }
            }

            places[slot] = bySecond;
        }

        return RoundOccupancy.FromSlots(Round.Number, Round.StartTick, Round.EndTick, TickRate,
            new Dictionary<int, int>(Round.Sides), places, Round.Bomb);
    }

    /// <summary>What the shipped profile proposes for the round in the file.</summary>
    internal List<GoldenProposal> Detect()
    {
        SiteRegionTable table = SiteRegionTable.TryParse(RegionTable!.ToJsonString())
                                ?? throw new InvalidOperationException("the fixture's region table does not parse");
        SiteRegions regions = SiteRegions.Compose(Map, null, table, DetectorProfile.Default);
        List<PlacedEvent> events =
        [
            .. Events.Select(e => new PlacedEvent(e.Tick, e.Kind, e.Slot, 0, new Vector3(e.X, e.Y, e.Z), e.Place))
        ];
        return
        [
            .. ProposalDetection.DetectRound(Map, TickRate, regions, events, Occupancy(), DetectorProfile.Default)
                .Select(p => new GoldenProposal(p.Id, p.Detector, p.Code, p.Round, p.Side, p.FromTick, p.ToTick,
                    p.TriggerTick, p.Confidence,
                    new SortedDictionary<string, double>(p.Factors.ToDictionary(), StringComparer.Ordinal),
                    new SortedDictionary<string, string>(p.Labels.ToDictionary(), StringComparer.Ordinal),
                    [.. p.Evidence.Select(e => new GoldenEvidence(e.Kind, e.Tick, e.Text))]))
        ];
    }

    internal string Serialize() => JsonSerializer.Serialize(this, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    internal static SuggestedTagsGolden Load(string path) =>
        JsonSerializer.Deserialize<SuggestedTagsGolden>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"{path} is not a golden");

    internal static string FixtureDirectory(string repo) => Path.Combine(repo, "tests", "fixtures", "suggested-tags");

    // A subfolder, so the synthesised-row snapshot tests that read FixtureDirectory's own *.json leave it out.
    internal static string EngineRowsFixtureDirectory(string repo) => Path.Combine(FixtureDirectory(repo), "engine-rows");

    private static string? Entry(RoundOccupancy round, int slot, int second) =>
        round.PlaceOf(slot, second) ?? (round.IsAlive(slot, second) ? RoundOccupancy.Unplaced : null);
}

/// <summary>
///     Round Facts rows for a real replay, synthesised in the test from the demo's own events (the same
///     stand-in <c>RoundIndexRealDemoTests</c> uses, carried further: this one also needs kills,
///     per-round sides and the plant for the goldens to mean anything). The committed fixtures are
///     pinned against these rows on purpose, so a later engine-rows recapture is a separate test rather
///     than a replacement of this one. Windows are the clip rounds ended at
///     <c>round_officially_ended</c>; sides follow <c>player_team</c> with the <c>OldTeam</c> rule for
///     the first half GOTV never announces; kills are <c>player_death</c>; the plant site is the planter's
///     place at the plant, which matched the site on 58 of 58 plants in the design's measurement.
///     Test code only: the app never derives rows itself.
/// </summary>
internal static class SuggestedTagsRealDemoRows
{
    internal static RoundFactsRows Synthesize(ParsedDemo parsed, IReadOnlyList<PositionSample> samples)
    {
        IReadOnlyList<ClipRound> clips = ClipRounds.Derive(parsed);
        List<GameEvent> events = [.. parsed.AllGameEvents.OrderBy(e => e.GameTick)];
        List<int> officiallyEnded = [.. events.Where(e => e.Name == "round_officially_ended").Select(e => e.GameTick)];

        Dictionary<int, List<(int Tick, int Team, int OldTeam)>> changes = [];
        foreach (GameEvent e in events)
        {
            if (e.Payload is PlayerTeamEvent team)
            {
                if (!changes.TryGetValue(team.UserId, out List<(int, int, int)>? list))
                {
                    list = [];
                    changes[team.UserId] = list;
                }

                list.Add((e.GameTick, team.Team, team.OldTeam));
            }
        }

        Dictionary<int, List<(int Tick, string Place)>> placed = [];
        foreach (PositionSample sample in samples)
        {
            if (string.IsNullOrEmpty(sample.Place))
            {
                continue;
            }

            if (!placed.TryGetValue(sample.PlayerSlot, out List<(int, string)>? list))
            {
                list = [];
                placed[sample.PlayerSlot] = list;
            }

            list.Add((sample.Tick, sample.Place));
        }

        RoundFactsRows rows = new() { Schema = DemoCacheRecord.RoundFactsSchema };
        for (int i = 0; i < clips.Count; i++)
        {
            int start = clips[i].StartTickFrameClock;
            int? next = i + 1 < clips.Count ? clips[i + 1].StartTickFrameClock : null;
            int? end = officiallyEnded.Where(t => t > start && (next is null || t <= next)).Select(t => (int?)t).FirstOrDefault();
            int windowEnd = end ?? next ?? int.MaxValue;

            Dictionary<int, int> sides = [];
            foreach ((int slot, PlayerInfo player) in parsed.Players)
            {
                int side = SideAt(slot, start, player.Team, changes);
                if (side is 2 or 3)
                {
                    sides[slot] = side;
                }
            }

            RoundFacts round = new()
            {
                Number = clips[i].Number,
                IsLive = end is not null || next is not null,
                FreezeEndTick = start,
                EndTick = end,
                EndSource = end is null ? RoundEndSource.None : RoundEndSource.OfficiallyEndedEvent,
                Ct = new SideFacts { Side = 3, Slots = [.. sides.Where(s => s.Value == 3).Select(s => s.Key).Order()] },
                T = new SideFacts { Side = 2, Slots = [.. sides.Where(s => s.Value == 2).Select(s => s.Key).Order()] }
            };

            foreach (GameEvent e in events.Where(e => e.GameTick >= start && e.GameTick < windowEnd))
            {
                switch (e.Payload)
                {
                    case PlayerDeathEvent death:
                        round.Kills.Add(new KillStep
                        {
                            Tick = e.GameTick,
                            VictimSlot = death.UserId,
                            AttackerSlot = death.Attacker,
                            VictimSide = sides.GetValueOrDefault(death.UserId)
                        });
                        break;
                    case BombPlantedEvent plant when round.PlantTick is null:
                        round.PlantTick = e.GameTick;
                        round.PlanterSlot = plant.UserId;
                        round.PlantSiteEntity = plant.Site;
                        round.PlantSite = PlaceAt(placed, plant.UserId, e.GameTick) switch
                        {
                            SiteRegions.SiteA => BombSite.A,
                            SiteRegions.SiteB => BombSite.B,
                            _ => BombSite.Unknown
                        };
                        break;
                    case BombDefusedEvent when round.DefuseTick is null:
                        round.DefuseTick = e.GameTick;
                        break;
                    case BombExplodedEvent when round.ExplodeTick is null:
                        round.ExplodeTick = e.GameTick;
                        break;
                }
            }

            rows.Rounds.Add(round);
        }

        return rows;
    }

    // The last change at or before the tick; the first change's OldTeam when every change lies ahead;
    // the final roster team for a slot that never changed.
    private static int SideAt(int slot, int tick, int finalTeam, Dictionary<int, List<(int Tick, int Team, int OldTeam)>> changes)
    {
        if (!changes.TryGetValue(slot, out List<(int Tick, int Team, int OldTeam)>? list))
        {
            return finalTeam;
        }

        int team = 0;
        foreach ((int at, int to, int from) in list)
        {
            if (at > tick)
            {
                return team != 0 ? team : from;
            }

            team = to;
        }

        return team;
    }

    private static string? PlaceAt(Dictionary<int, List<(int Tick, string Place)>> placed, int slot, int tick)
    {
        if (!placed.TryGetValue(slot, out List<(int Tick, string Place)>? list))
        {
            return null;
        }

        string? place = null;
        foreach ((int at, string name) in list)
        {
            if (at > tick)
            {
                break;
            }

            place = name;
        }

        return place;
    }
}
