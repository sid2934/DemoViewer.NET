#region

using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <c>dv2d export --encoder / --quality</c> (plan <c>P2-export-throughput</c> D4).
///     <para>
///         Every case runs a real subprocess against a real demo and a real ffmpeg, and
///         <b>
///             every one of
///             them skips cleanly without either
///         </b>
///         . What they assert is the CLI's contract: the argument
///         parsing, the additive JSON keys, and the fact that a machine with no hardware encoder gets a
///         normal export rather than a refusal. None of that needs a GPU to be true. The selection
///         LOGIC is asserted GPU-free with a fake probe in <c>EncoderSelectorTests</c>.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class EncoderFlagTests
{
    /// <summary>A four-frame range: enough to reach the encoder, short enough to be a unit test.</summary>
    private static string[] TinyExport(string output, params string[] extra) =>
    [
        "export", "--demo", Dv2d.RequireDemo(), "--from", "0", "--to", "6",
        "--size", "320x180", "--fps", "24", "--out", output, "--json", .. extra
    ];

    private static string TempOutput(string name) =>
        Path.Combine(Path.GetTempPath(), $"dv2d-p2-{name}-{Guid.NewGuid():N}.webm");

    [Test]
    public async Task Export_ReportsTheChosenEncoder_AndWhy()
    {
        string output = TempOutput("auto");
        try
        {
            CliRun run = Dv2d.Subprocess(TinyExport(output));
            if (run.ExitCode != 0)
            {
                // No ffmpeg on this machine: the export refuses with exit 6 before any of this matters.
                await Assert.That(run.StdErr).Contains("ffmpeg");
                return;
            }

            JsonObject payload = run.Json();

            // Additive, on the same schema_version 1 payload. `encoder` keeps its old meaning (WHICH
            // PROGRAM encodes) and these say which codec inside it, chosen how.
            await Assert.That(payload["schema_version"]!.GetValue<int>()).IsEqualTo(1);
            await Assert.That(payload["encoder"]!.GetValue<string>()).IsEqualTo("ffmpeg");
            await Assert.That(payload["video_encoder"]!.GetValue<string>()).IsNotEmpty();
            await Assert.That(payload["video_encoder_kind"]).IsNotNull();
            await Assert.That(payload["video_codec"]).IsNotNull();
            await Assert.That(payload["quality"]!.GetValue<string>()).IsEqualTo("standard");
            await Assert.That(payload["encoder_reason"]!.GetValue<string>()).IsNotEmpty();
            await Assert.That(payload["encoder_arguments"]!.GetValue<string>()).IsNotEmpty();
            await Assert.That(payload["encoder_attempts"]).IsNotNull();

            // A hardware encoder is not bit-reproducible (plan D6), so the file's bytes are a function of
            // this machine. Record the machine's answer or two files cannot be compared later.
            JsonArray attempts = payload["encoder_attempts"]!.AsArray();
            await Assert.That(attempts.Count).IsGreaterThanOrEqualTo(1);
            await Assert.That(attempts[^1]!["works"]!.GetValue<bool>()).IsTrue();

            foreach (string key in payload.Select(static p => p.Key))
            {
                await Assert.That(IsSnakeCase(key)).IsTrue();
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Test]
    public async Task Software_IsAlwaysAvailable_AndNamesTheSoftwareRung()
    {
        string output = TempOutput("software");
        try
        {
            CliRun run = Dv2d.Subprocess(TinyExport(output, "--encoder", "software", "--quality", "draft"));
            if (run.ExitCode != 0)
            {
                await Assert.That(run.StdErr).Contains("ffmpeg");
                return;
            }

            JsonObject payload = run.Json();

            // The one path that must work on every machine there is, GPU-less CI included. `software` is
            // also the machine-independent answer: no probe runs, so what hardware is present cannot
            // change the outcome.
            await Assert.That(payload["video_encoder"]!.GetValue<string>()).IsEqualTo("libvpx-vp9");
            await Assert.That(payload["video_encoder_kind"]!.GetValue<string>()).IsEqualTo("software");
            await Assert.That(payload["quality"]!.GetValue<string>()).IsEqualTo("draft");
            await Assert.That(payload["encoder_attempts"]!.AsArray().Count).IsEqualTo(0);
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Test]
    public async Task Mp4_Software_IsX264()
    {
        string output = Path.ChangeExtension(TempOutput("mp4"), ".mp4");
        try
        {
            CliRun run = Dv2d.Subprocess("export", "--demo", Dv2d.RequireDemo(), "--from", "0", "--to", "6",
                "--size", "320x180", "--fps", "24", "--format", "mp4", "--encoder", "software",
                "--out", output, "--json");

            if (run.ExitCode != 0)
            {
                await Assert.That(run.StdErr).Contains("ffmpeg");
                return;
            }

            await Assert.That(run.Json()["video_encoder"]!.GetValue<string>()).IsEqualTo("libx264");
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Test]
    public async Task AnUnknownQuality_IsAUsageError()
    {
        CliRun run = Dv2d.Subprocess("export", "--demo", Dv2d.RequireDemo(), "--from", "0", "--to", "1",
            "--quality", "turbo", "--out", TempOutput("bad-quality"));

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("draft, standard, best");
    }

    [Test]
    public async Task AnEncoderFromTheOtherLadder_IsRefused_WithTheChoices()
    {
        // h264_nvenc is a real encoder and a real rung, but of the MP4 ladder. Asking for it while
        // exporting a WebM cannot be honoured, and the message has to say what CAN be.
        CliRun run = Dv2d.Subprocess("export", "--demo", Dv2d.RequireDemo(), "--from", "0", "--to", "1",
            "--encoder", "h264_nvenc", "--out", TempOutput("wrong-ladder"));

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.StdErr).Contains("av1_nvenc");
        await Assert.That(run.StdErr).Contains("libvpx-vp9");
    }

    [Test]
    public async Task NoEncode_ReportsNoVideoEncoder()
    {
        CliRun run = Dv2d.Subprocess("export", "--demo", Dv2d.RequireDemo(), "--from", "0", "--to", "6",
            "--size", "320x180", "--fps", "24", "--no-encode", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);

        JsonObject payload = run.Json();

        // --no-encode is the determinism and render-ceiling path: HashingFrameSink, no ffmpeg, no ladder.
        // The keys are present and null rather than absent, so a consumer reads one shape either way.
        await Assert.That(payload["encoder"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(payload.ContainsKey("video_encoder")).IsTrue();
        await Assert.That(payload["video_encoder"]).IsNull();
        await Assert.That(payload.ContainsKey("quality")).IsTrue();
        await Assert.That(payload["quality"]).IsNull();
    }

    [Test]
    public async Task TheUsageText_DocumentsBothFlags()
    {
        CliRun run = Dv2d.Subprocess("--help");

        await Assert.That(run.StdOut).Contains("--encoder");
        await Assert.That(run.StdOut).Contains("--quality draft|standard|best");
        await Assert.That(run.StdOut).Contains("av1_nvenc");
    }

    private static bool IsSnakeCase(string key) =>
        key.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A test that leaves a temp file behind is not a failing test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
