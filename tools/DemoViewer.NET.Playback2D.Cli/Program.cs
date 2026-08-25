#region

using System.Text.Json;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d</c> — headless Playback2D render / export / bench (docs/playback2d-v2/design.md §4, §5.8,
///     §6, §11). No window is ever created, no Avalonia assembly is ever loaded, and no feature gate or
///     <c>AppSettings</c> value is ever read: a headless tool takes explicit flags (§7.7).
/// </summary>
internal static class Program
{
    /// <summary>The verbs the usage text lists, and the only ones <see cref="Main" /> dispatches.</summary>
    public static readonly IReadOnlyList<string> Verbs =
        ["render", "export", "bench", "golden", "fixture", "probe"];

    /// <summary>The usage text, printed on no args, <c>--help</c>, and every usage error.</summary>
    public const string Usage = """
        dv2d — headless Playback2D renderer (docs/playback2d-v2/dv2d.md)

          render   --fixture <path> | --demo <path> (--tick N | --frame N)
                   [--out <png>]            default ./dv2d-render.png
                   [--size WxH]             default 1920x1080
                   [--layers a,b] [--exclude-layers a,b]
                   [--camera fit-map|fit-alive|follow:<steamId>|fixed:<x>,<y>,<zoom>]
                   [--layout stacked|single] [--level <levelId>]
                   [--assets <dir>] [--no-radar]
                   [--cpu | --gpu | --backend <auto|cpu|gpu|angle|gl|force-gpu>]
                   [--strict-backend]
                   [--json] [--quiet] [--diag-assemblies]

          export   --demo <path> (--from N --to N | --round N)
                   [--out <file>] [--format webm|mp4|gif]   default webm
                   [--fps N] [--size WxH] [--speed X]
                   [--layers ...] [--camera ...] [--assets <dir>]
                   [--ffmpeg <path>] [--cpu | --gpu | --backend <name>] [--strict-backend]
                   [--json] [--progress]

          bench    (--fixture <path> | --name <corpusEntry> | --demo <path> [--from N])
                   [--frames N]             default 2000
                   [--warmup N]             default 128
                   [--size WxH] [--layers ...] [--assets <dir>]
                   [--cpu | --gpu | --backend <name>] [--strict-backend]
                   [--gate] [--budget-scale X] [--budget-p99-ms X]
                   [--budget-advance-p99-ms X] [--budget-bytes-per-frame N]
                   [--report-dir <dir>] [--json]

          golden   verify | update
                   [--corpus <dir>] [--name <fixture>]
                   [--cpu | --gpu | --backend <name>] [--strict-backend]
                   [--tolerance byte-exact|perceptual] [--diff-dir <dir>] [--json]

          fixture  capture --demo <path> (--tick N | --frame N) --name <id>
                           [--corpus <dir>] [--size WxH] [--camera ...]
                           [--annotations <path>] [--layers ...] [--json]
                   list   [--corpus <dir>] [--json]
                   verify [--corpus <dir>] [--json]

          probe    [--json] [--require-gpu] [--require-hardware] [--quiet]
                   reports the render-surface backend this machine provides, and why.
                   A CPU answer is not an error (exit 0); --require-gpu makes it exit 6,
                   and --require-hardware additionally rejects WARP / llvmpipe.

        backend selection (design §5.8): --cpu | --gpu | --backend <name>, then
                    DV2D_RENDER_BACKEND, then an auto-probe. --strict-backend turns a
                    GPU request into force-gpu, so a lane fails rather than silently
                    measuring software rendering. dv2d reads no AppSettings (§7.7).

        exit codes: 0 ok · 1 usage · 2 missing input · 3 runtime failure
                    4 GATE FAILURE (golden mismatch / budget exceeded) · 5 cancelled
                    6 requested environment unavailable
        """;

    /// <summary>The process entry point.</summary>
    /// <param name="args">The raw arguments.</param>
    public static int Main(string[] args)
    {
        ConsoleOut.Reset();
        CliArgs parsed;
        try
        {
            parsed = CliArgs.Parse(args);
        }
        catch (ArgumentNullException)
        {
            Console.Out.WriteLine(Usage);
            return ExitCode.Usage.ToInt();
        }

        ConsoleOut.IsJson = parsed.Flag("json");
        ConsoleOut.IsQuiet = parsed.Flag("quiet");

        if (parsed.Verb is null)
        {
            // No verb at all: --help is a success, anything else (including nothing) is a usage error.
            Console.Out.WriteLine(Usage);
            return parsed.WantsHelp ? ExitCode.Success.ToInt() : ExitCode.Usage.ToInt();
        }

        if (parsed.WantsHelp)
        {
            Console.Out.WriteLine(Usage);
            return ExitCode.Success.ToInt();
        }

        using CancellationTokenSource cts = new();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // let the command unwind and kill its children rather than dying mid-encode
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            return Dispatch(parsed, cts.Token).ToInt();
        }
        catch (CliUsageException e)
        {
            ConsoleOut.Error(e.Message);
            Console.Error.WriteLine(Usage);
            return ExitCode.Usage.ToInt();
        }
        catch (BackendUnavailableException e)
        {
            ConsoleOut.Error(e.Message);
            return ExitCode.EnvironmentUnavailable.ToInt();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            ConsoleOut.Error(e.Message);
            return ExitCode.InputMissing.ToInt();
        }
        catch (OperationCanceledException)
        {
            ConsoleOut.Error("cancelled.");
            return ExitCode.Cancelled.ToInt();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException
                                      or JsonException or NotSupportedException or ArgumentException
                                      or UnauthorizedAccessException)
        {
            ConsoleOut.Error(e.Message);
            return ExitCode.RuntimeFailure.ToInt();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static ExitCode Dispatch(CliArgs args, CancellationToken ct) => args.Verb switch
    {
        "render" => RenderCommand.Run(args),
        "bench" => BenchCommand.Run(args),
        "golden" => GoldenCommand.Run(args),
        "fixture" => FixtureCommand.Run(args),
        "probe" => ProbeCommand.Run(args),
        "export" => ExportCommand.RunAsync(args, ct).GetAwaiter().GetResult(),
        _ => throw new CliUsageException($"unknown command '{args.Verb}'.")
    };
}
