#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d</c>: headless Playback2D render / export / bench (docs/playback2d-v2/design.md §4, §5.8,
///     §6, §11). No window is ever created, no Avalonia assembly is ever loaded, and no feature gate or
///     <c>AppSettings</c> value is ever read: a headless tool takes explicit flags (§7.7).
/// </summary>
internal static class Program
{
    /// <summary>The usage text, printed on no args, <c>--help</c>, and every usage error.</summary>
    public const string Usage = """
                                dv2d — headless Playback2D renderer (docs/playback2d-v2/dv2d.md)

                                  render   --fixture <path> | --demo <path> (--tick N | --frame N)
                                           [--out <png>]            default ./dv2d-render.png
                                           [--size WxH]             default 1920x1080
                                           [--layers a,b] [--exclude-layers a,b]
                                           [--ink <file.dvann.json>]
                                           [--camera fit-map|fit-alive|follow:<steamId>|fixed:<x>,<y>,<zoom>]
                                           [--layout stacked|single] [--level <levelId>]
                                           [--assets <dir>] [--no-radar]
                                           [--cpu | --gpu | --backend <auto|cpu|gpu|angle|gl|force-gpu>]
                                           [--strict-backend]
                                           [--json] [--quiet] [--diag-assemblies]

                                  export   --demo <path> [--from N|tN] [--to N|tN]   default whole demo
                                           [--out <file>] [--format webm|mp4|gif]   default webm
                                           [--fps N] [--size WxH] [--speed X]
                                           [--encoder auto|software|<name>]         default auto
                                           [--quality draft|standard|best]          default standard
                                           [--layers a,b] [--assets <dir>] [--no-radar]
                                           [--hud] [--annotations] [--palette dark|light]  default dark
                                           [--no-encode] [--ffmpeg-log] [--perf]
                                           [--cpu | --gpu | --backend <name>] [--strict-backend]
                                           [--json]

                                  bench    (--fixture <path> | --name <corpusEntry> | --demo <path> [--from N])
                                           [--frames N]             default 2000
                                           [--warmup N]             default 128
                                           [--size WxH] [--layers ...] [--assets <dir>]
                                           [--cpu | --gpu | --backend <name>] [--strict-backend]
                                           [--gate] [--budget-scale X] [--budget-p99-ms X]
                                           [--budget-advance-p99-ms X] [--budget-bytes-per-frame N]
                                           [--report-dir <dir>] [--perf] [--json]

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

                                --layers    the eleven ids SceneLayerCatalog registers, bare or prefixed:
                                            radar, trails, areaeffects, vision, markers, bomb, floorlabel
                                            (the scene, drawn by default) and annotations, hud.roster,
                                            hud.clock, hud.killfeed (opt-in, named or absent). render,
                                            golden and bench draw the SAME stack export does — up to D6
                                            they drew a debug grid instead, and every committed golden
                                            was a picture of it. The four opt-in ids need a source: --ink
                                            feeds the annotation layer, and the three HUD ids need a
                                            demo's clock and kill timeline, so only `export --hud` can.

                                --ink       burns a .dvann.json sidecar into a single-frame render. `golden`
                                            and `bench` take it by convention instead — annotations/<name>.dvann.json
                                            beside the corpus entry's scene — so a golden's ink is a
                                            committed artefact rather than a flag someone has to remember.

                                --perf (alias --profile, env CS2DEMOKIT_PROFILE / DEMOVIEWER_PROFILE) adds a
                                            per-layer and per-stage breakdown to bench and export: p50/p99/total/share
                                            per stage and per layer, picture-cache hit rates, the uncapped render-only
                                            fps, and a slowest-first ranking. Off by default and free when off.

                                export parity with the app's dialog: --hud, --annotations and --palette are the
                                            three things the pane had and `dv2d export` did not, so the same request
                                            used to produce two different videos. --annotations is a FLAG (unlike
                                            `fixture capture --annotations <path>`, which embeds a raw fixture blob):
                                            it burns in the demo's own .dvann.json sidecar, and says so and adds no
                                            layer id when there is none. Vision cones are off by default on both
                                            sides now; name playback2d.vision in --layers to opt in.

                                --encoder   auto walks the per-format ladder and takes the best rung this
                                            machine can actually run, verified by a two-frame test encode
                                            (webm: av1_nvenc, av1_qsv, av1_amf, libvpx-vp9 · mp4:
                                            h264_nvenc, h264_qsv, h264_amf, libx264). `software` skips the
                                            hardware rungs. Naming a rung takes it literally: if it does not
                                            verify the export is refused (exit 6) rather than substituted.
                                            The chosen rung, why, and every rejected one are in --json.

                                backend selection (design §5.8): --cpu | --gpu | --backend <name>, then
                                            DV2D_RENDER_BACKEND, then an auto-probe. --strict-backend turns a
                                            GPU request into force-gpu, so a lane fails rather than silently
                                            measuring software rendering. dv2d reads no AppSettings (§7.7).

                                exit codes: 0 ok · 1 usage · 2 missing input · 3 runtime failure
                                            4 GATE FAILURE (golden mismatch / budget exceeded) · 5 cancelled
                                            6 requested environment unavailable
                                """;

    /// <summary>The verbs the usage text lists, and the only ones <see cref="Main" /> dispatches.</summary>
    public static readonly IReadOnlyList<string> Verbs =
        ["render", "export", "bench", "golden", "fixture", "probe"];

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
        catch (EncoderUnavailableException e)
        {
            // Exit 6, not 3, and for the same reason --gpu gets 6: nothing about the request is wrong.
            // `--encoder h264_nvenc` is a perfectly valid thing to ask for; this machine's driver is what
            // cannot answer. A CI lane that treats 3 as "the change is broken" must not see this as one.
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
