#region

using System.Globalization;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>
///     Read-only reconnaissance over a demo: where the bytes are, where the round boundaries are, and
///     what the container's tail looks like. Everything the trim ladder's parameters are chosen from.
/// </summary>
internal static class DemoInspector
{
    /// <summary>Round-lifecycle events worth listing before picking a cut point.</summary>
    private static readonly string[] RoundEventNames =
        ["begin_new_match", "round_announce_match_start", "round_start", "round_freeze_end", "round_end", "round_officially_ended"];

    public static void Inspect(ParsedDemo demo, byte[] raw, string path, int listBoundaries)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"size          : {Mib(raw.LongLength)} ({raw.LongLength:N0} bytes)"));

        DemoTail tail = DemoTail.Read(raw, demo);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"header[8..12] : {BitConverter.ToInt32(raw, 8):N0}  -> DEM_FileInfo    {Describe(tail.FileInfo)}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"header[12..16]: {BitConverter.ToInt32(raw, 12):N0}  -> DEM_SpawnGroups {Describe(tail.SpawnGroups)}"));
        Console.WriteLine($"post-DEM_Stop : DEM_Stop {Describe(tail.Stop)}");

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"map={demo.MapName} tickRate={demo.TickRate} ticks={demo.TickCount} frames={demo.Frames.Count} " +
            $"players={demo.Players.Count} build={demo.BuildNumber} source={demo.Profile.SourceKind}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"last 4 parsed : {string.Join(", ", demo.Frames.TakeLast(4).Select(f => $"{f.Command}@{f.ServerTick}"))}"));
        Console.WriteLine(
            "note          : DemoParser stops AT DEM_Stop, so the three tail frames above are absent from ParsedDemo.Frames.");

        Console.WriteLine();
        Console.WriteLine("-- frame command histogram (raw on-disk bytes) --");
        var byCommand = demo.Frames
            .GroupBy(f => f.Command, StringComparer.Ordinal)
            .Select(g => (Command: g.Key, Count: g.Count(), Bytes: g.Sum(f => (long)f.RawLength)))
            .OrderByDescending(x => x.Bytes);
        foreach ((string command, int count, long bytes) in byCommand)
        {
            // Rare commands get their tick positions listed: whether a frame type sits in the signon run
            // or is scattered through the stream decides if a checkpoint-entry trim may carry pre-entry
            // copies of it forward.
            string where = count is > 0 and <= 32
                ? "  ticks: " + string.Join(",", demo.Frames
                    .Where(f => string.Equals(f.Command, command, StringComparison.Ordinal))
                    .Take(8).Select(f => f.ServerTick))
                : "";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {command,-24} {count,8:N0} frames  {Mib(bytes),12}  {100.0 * bytes / raw.LongLength,6:F1}%{where}"));
        }

        Console.WriteLine();
        Console.WriteLine("-- round-lifecycle events --");
        foreach (string name in RoundEventNames)
        {
            List<GameEvent> events = demo.AllGameEvents
                .Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
            string ticks = string.Join(", ", events.Take(listBoundaries).Select(e => e.GameTick));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name,-28} {events.Count,4}  first {listBoundaries}: {ticks}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {"player_death",-28} {demo.AllGameEvents.Count(e => e.Name == "player_death"),4}"));

        Console.WriteLine();
        Console.WriteLine("-- DEM_FullPacket checkpoints --");
        List<int> fullPacketTicks = demo.Frames
            .Where(f => string.Equals(f.Command, "DEM_FullPacket", StringComparison.Ordinal))
            .Select(f => f.ServerTick).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {fullPacketTicks.Count} checkpoints, ticks: {string.Join(", ", fullPacketTicks.Take(12))}{(fullPacketTicks.Count > 12 ? ", ..." : "")}"));
        Console.WriteLine();
    }

    /// <summary>Inner net-message byte breakdown over a retained window — the "what is actually inside" table.</summary>
    public static void InspectWindowMessages(ParsedDemo demo, TrimWindow window)
    {
        Dictionary<string, (int Count, long Bytes)> byType = new(StringComparer.Ordinal);
        long total = 0;
        for (int i = window.EntryIndex; i <= window.EndIndex; i++)
        {
            foreach (NetMessage message in demo.Frames[i].InnerMessages)
            {
                long bytes = message.DecompressedLength ?? 0;
                byType.TryGetValue(message.MessageTypeName, out (int Count, long Bytes) prior);
                byType[message.MessageTypeName] = (prior.Count + 1, prior.Bytes + bytes);
                total += bytes;
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"-- inner messages over frames {window.EntryIndex}..{window.EndIndex} " +
            $"(ticks {window.StartTick}..{window.EndTick}), {Mib(total)} decompressed --"));
        foreach ((string name, (int count, long bytes)) in byType.OrderByDescending(kv => kv.Value.Bytes).Take(12))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name,-28} {count,9:N0} msgs  {Mib(bytes),12}  {100.0 * bytes / Math.Max(1, total),6:F1}%"));
        }

        long windowRaw = 0;
        for (int i = window.EntryIndex; i <= window.EndIndex; i++)
        {
            windowRaw += demo.Frames[i].RawLength;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  window on-disk (verbatim, before setup frames): {Mib(windowRaw)}"));
        Console.WriteLine();
    }

    /// <summary>Formats a byte count as MiB.</summary>
    public static string Mib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:F2} MiB");

    private static string Describe(RawFrame? frame) => frame?.ToString() ?? "(not found)";
}
