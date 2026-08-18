#region

using System.Globalization;
using DemoViewer.NET.DemoTrimmer;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>
///     CLI for the demo-trimmer proof of concept: produces a small <c>.dem</c> containing only the first
///     few rounds of a large one, for use as a bundled first-run-tour demo.
///     <para>
///         Deliberately single-demo-at-a-time — a 170-450 MB demo plus its <see cref="ParsedDemo" /> plus
///         an <c>EntityTracker</c> replay is already most of a 16 GB machine's headroom. Never run two
///         instances concurrently.
///     </para>
/// </summary>
internal static class Program
{
    private const string Usage = """
        DemoViewer.NET demo trimmer (proof of concept)

          inspect <demo.dem> [--boundaries N]
              Frame/byte breakdown, round-boundary ladder, container tail.

          trim <demo.dem> --out <dir> [options]
              --rounds 1,3            round counts to emit (default 1,3)
              --variants v0,v1,...    ladder rungs to emit (default all: v0 v1 v2 v3 v2c v3c)
              --boundary <event>      round boundary event (default round_freeze_end)
              --skip-boundaries N     skip N leading boundaries (warmup) (default 0)
              --prefix <name>         output filename prefix (default: demo file stem)
              --no-verify             skip re-parse + entity verification
              --no-baseline           skip the informational full-source (D0) replay
              --no-identity-check     skip the empty-drop-set encoder round-trip gate
        """;

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "inspect" => RunInspect(args),
                "trim" => RunTrim(args),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunInspect(string[] args)
    {
        string path = args[1];
        int boundaries = IntOption(args, "--boundaries", 8);
        (byte[] raw, ParsedDemo demo) = Load(path);
        DemoInspector.Inspect(demo, raw, path, boundaries);

        // Also show the "what is inside the packets" split for a 3-round checkpoint window — the
        // same table the original feasibility measurement reported.
        try
        {
            TrimWindow window = WindowSelector.Select(demo, 3, enterAtCheckpoint: true);
            DemoInspector.InspectWindowMessages(demo, window);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"(no 3-round window: {ex.Message})");
        }

        return 0;
    }

    private static int RunTrim(string[] args)
    {
        string path = args[1];
        string outDir = StringOption(args, "--out") ?? Fail<string>("--out <dir> is required.");
        IReadOnlyList<int> roundCounts = (StringOption(args, "--rounds") ?? "1,3")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        IReadOnlyList<TrimVariant> variants = StringOption(args, "--variants") is { } spec
            ? TrimVariant.Parse(spec)
            : TrimVariant.All;
        string boundaryEvent = StringOption(args, "--boundary") ?? WindowSelector.DefaultBoundaryEvent;
        int skipBoundaries = IntOption(args, "--skip-boundaries", 0);
        string prefix = StringOption(args, "--prefix") ?? Path.GetFileNameWithoutExtension(path);
        bool verify = !args.Contains("--no-verify", StringComparer.Ordinal);
        bool baseline = !args.Contains("--no-baseline", StringComparer.Ordinal);
        bool identityCheck = !args.Contains("--no-identity-check", StringComparer.Ordinal);

        (byte[] raw, ParsedDemo demo) = Load(path);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"source: {Path.GetFileName(path)}  {DemoInspector.Mib(raw.LongLength)}  " +
            $"map={demo.MapName} frames={demo.Frames.Count} ticks={demo.TickCount}"));

        // player_team synthesis inputs — sampled ONCE per source (every window shares round 1's
        // seating). A missing descriptor or an empty sample set skips synthesis with a warning
        // rather than emitting a file that will render every player on one team.
        IReadOnlyList<TeamSample> teamSamples = TeamEventSynthesizer.Sample(demo);
        CMsgSource1LegacyGameEventList.Types.descriptor_t? teamDescriptor =
            TeamEventSynthesizer.FindDescriptor(demo, out int teamListFrame);
        byte[]? teamPacket = teamDescriptor is not null && teamSamples.Count > 0
            ? TeamEventSynthesizer.BuildPacketPayload(teamDescriptor, teamSamples)
            : null;
        Console.WriteLine(teamPacket is null
            ? "WARNING: no player_team synthesis (descriptor or seated controllers missing) — " +
              "trims ending before halftime will show every player on one team"
            : string.Create(CultureInfo.InvariantCulture,
                $"synthesizing {teamSamples.Count} player_team seatings: {string.Join(", ", teamSamples.Select(s => $"{s.Name}→{(s.Team == 3 ? "CT" : "T")}"))}"));
        Console.WriteLine();

        int failures = 0;
        foreach (int rounds in roundCounts)
        {
            foreach (TrimVariant variant in variants)
            {
                TrimWindow window = WindowSelector.Select(
                    demo, rounds, variant.EnterAtCheckpoint, boundaryEvent, skipBoundaries);
                string outPath = Path.Combine(outDir, $"{prefix}-{variant.Id}-{rounds}r.dem");

                TrimResult result = DemoTrimWriter.Write(demo, raw, window, variant, outPath, identityCheck,
                    teamPacket, teamSamples, teamListFrame);
                PrintResult(result, raw.LongLength);

                if (verify)
                {
                    failures += VerifyEmitted(demo, raw, result, baseline) ? 0 : 1;
                }

                Console.WriteLine();
            }
        }

        Console.WriteLine(failures == 0
            ? "all emitted candidates verified."
            : $"{failures} candidate(s) FAILED verification.");
        return failures == 0 ? 0 : 2;
    }

    private static bool VerifyEmitted(ParsedDemo source, byte[] sourceRaw, TrimResult result, bool baseline)
    {
        byte[] trimmedRaw = File.ReadAllBytes(result.Path);
        ParsedDemo trimmed;
        try
        {
            trimmed = DemoParser.Parse(trimmedRaw.AsMemory());
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            Console.WriteLine($"  VERIFY: FAILED — re-parse threw: {ex.Message}");
            return false;
        }

        VerificationReport report = TrimVerifier.Verify(source, sourceRaw, result, trimmed, trimmedRaw, baseline);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  VERIFY: {(report.Ok ? "PASS" : "FAIL")}  ({report.Passed.Count} checks passed)"));
        foreach (string note in report.Notes)
        {
            Console.WriteLine($"    note: {note}");
        }

        foreach (string failure in report.Failures)
        {
            Console.WriteLine($"    FAIL: {failure}");
        }

        return report.Ok;
    }

    private static void PrintResult(TrimResult result, long sourceBytes)
    {
        Console.WriteLine($"[{result.Variant.Id} / {result.Window.RoundsKept}r] {Path.GetFileName(result.Path)}");
        Console.WriteLine($"  {result.Variant.Description}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  size    : {DemoInspector.Mib(result.BytesWritten)}  ({100.0 * result.BytesWritten / sourceBytes:F1}% of source)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  window  : frames {result.Window.EntryIndex}..{result.Window.EndIndex} " +
            $"ticks {result.Window.StartTick}..{result.Window.EndTick} " +
            $"(entry={(result.Window.EnteredAtCheckpoint ? "DEM_FullPacket" : "frame 0")})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  frames  : {result.SetupFrameCount} setup + {result.WindowFrameCount} window " +
            $"({result.DroppedFrameCount} whole frames dropped, {result.RewrittenFrameCount} payloads rewritten)"));
        if (result.Variant.RewritesPayloads)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  strip   : kept {result.Strip.Kept:N0} inner msgs, dropped {result.Strip.Dropped:N0} " +
                $"({DemoInspector.Mib(result.Strip.DroppedBytes)} decompressed); " +
                $"{result.LeftUncompressed:N0} frame(s) left uncompressed"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  identity: exact={result.IdentityExact:N0} prefix-shorter={result.IdentityShorter:N0} " +
                $"MISMATCH={result.IdentityMismatch:N0}"));
        }

        Console.WriteLine($"  fileinfo: {result.FileInfoBefore}  ->  {result.FileInfoAfter}");
    }

    private static (byte[] Raw, ParsedDemo Demo) Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Demo not found: {path}", path);
        }

        byte[] raw = File.ReadAllBytes(path);
        return (raw, DemoParser.Parse(raw.AsMemory()));
    }

    private static string? StringOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntOption(string[] args, string name, int fallback) =>
        StringOption(args, name) is { } raw && int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine(Usage);
        return 1;
    }

    private static T Fail<T>(string message) => throw new ArgumentException(message);
}
