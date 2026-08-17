#region

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.GameEvents;

#endregion

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: DemoSourceDetails <source-name> <demo.dem> [demo2.dem ...]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Produces a JSON report of event types, message types, and metadata");
    Console.Error.WriteLine("for one or more demo files from the same source.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Examples:");
    Console.Error.WriteLine("  DemoSourceDetails matchmaking match1.dem match2.dem");
    Console.Error.WriteLine("  DemoSourceDetails hltv-pro furia-vs-vitality-m1.dem");
    return 1;
}

string sourceName = args[0];
string[] demoPaths = args[1..];

List<DemoReport> demoReports = new();

foreach (string path in demoPaths)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        continue;
    }

    Console.Error.Write($"Parsing {Path.GetFileName(path)}...");
    byte[] bytes = File.ReadAllBytes(path);
    string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    ParsedDemo demo = DemoParser.Parse(bytes);
    Console.Error.WriteLine(" done.");

    DemoReport report = BuildReport(path, sha256, demo);
    demoReports.Add(report);
}

SourceReport output = new(sourceName, demoReports);
Console.WriteLine(JsonSerializer.Serialize(output, JsonOpts.Default));
return 0;

static DemoReport BuildReport(string path, string sha256, ParsedDemo demo)
{
    SortedDictionary<string, GameEventInfo> gameEventCounts = new();
    foreach (GameEvent evt in demo.AllGameEvents)
    {
        string name = evt.Name;
        if (!gameEventCounts.TryGetValue(name, out GameEventInfo? info))
        {
            info = new GameEventInfo(evt.GetType().Name, 0, null);
            if (evt is UnknownGameEvent unk)
            {
                info = info with
                {
                    IsUnknown = true
                };
            }
        }

        gameEventCounts[name] = info with
        {
            Count = info.Count + 1
        };
    }

    SortedDictionary<string, List<string>> gameEventFields = new();
    HashSet<string> seen = new();
    foreach (GameEvent evt in demo.AllGameEvents)
    {
        if (!seen.Add(evt.Name))
        {
            continue;
        }

        List<string> fields = evt.GetDecodedFields()
            .Select(f => f.Item1)
            .ToList();
        if (fields.Count > 0)
        {
            gameEventFields[evt.Name] = fields;
        }
    }

    SortedDictionary<string, int> netMessageCounts = new();
    int totalMessages = 0;
    foreach (DemoFrame frame in demo.Frames)
    {
        foreach (NetMessage msg in frame.InnerMessages)
        {
            totalMessages++;
            if (msg is GameEventMessage)
            {
                continue;
            }

            string typeName = msg.Payload?.GetType().Name ?? msg.MessageTypeName ?? "unknown";
            netMessageCounts[typeName] = netMessageCounts.GetValueOrDefault(typeName) + 1;
        }
    }

    List<string> weapons = demo.AllGameEvents.Select(e => e.Payload).OfType<PlayerDeathEvent>()
        .Select(e => e.Weapon).Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();

    List<string> hurtWeapons = demo.AllGameEvents.Select(e => e.Payload).OfType<PlayerHurtEvent>()
        .Select(e => e.Weapon).Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();

    string[] roundBoundaryEvents = new[]
    {
        "round_start", "round_freeze_end", "round_end", "round_officially_ended", "round_prestart", "round_poststart", "cs_round_start_beep", "cs_round_final_beep", "cs_pre_restart", "begin_new_match", "announce_phase_end", "cs_win_panel_match", "cs_intermission", "halftime", "game_restart", "cs_match_end_restart"
    };
    SortedDictionary<string, int> roundBoundaries = new();
    foreach (string eName in roundBoundaryEvents)
    {
        int count = demo.AllGameEvents.Count(e => e.Name == eName);
        if (count > 0)
        {
            roundBoundaries[eName] = count;
        }
    }

    List<PlayerEntry> players = demo.Players
        .OrderBy(kv => kv.Key)
        .Select(kv => new PlayerEntry(kv.Key, kv.Value.Name, kv.Value.Team))
        .ToList();

    string? headerMap = null;
    foreach (DemoFrame frame in demo.Frames.Take(50))
    {
        foreach (NetMessage msg in frame.InnerMessages)
        {
            if (msg.Payload is CDemoFileHeader hdr)
            {
                headerMap = hdr.MapName;
                break;
            }
        }
    }

    return new DemoReport(
        Path.GetFileName(path),
        new FileInfo(path).Length,
        sha256,
        demo.MapName ?? headerMap,
        demo.Frames.Count,
        totalMessages,
        demo.AllGameEvents.Count,
        demo.Players.Count,
        players,
        gameEventCounts,
        gameEventFields,
        netMessageCounts,
        roundBoundaries,
        weapons,
        hurtWeapons);
}

internal sealed record SourceReport(string SourceName, List<DemoReport> Demos);

internal sealed record DemoReport(
    string FileName,
    long FileSizeBytes,
    string Sha256,
    string? Map,
    int FrameCount,
    int TotalMessages,
    int GameEventCount,
    int PlayerCount,
    List<PlayerEntry> Players,
    SortedDictionary<string, GameEventInfo> GameEvents,
    SortedDictionary<string, List<string>> GameEventFields,
    SortedDictionary<string, int> NetMessageTypes,
    SortedDictionary<string, int> RoundBoundaryEvents,
    List<string> WeaponsInDeaths,
    List<string> WeaponsInHurts);

internal sealed record GameEventInfo(string TypeName, int Count, bool? IsUnknown);

internal sealed record PlayerEntry(int Slot, string Name, int Team);

internal static class JsonOpts
{
    /// <summary>Default.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
