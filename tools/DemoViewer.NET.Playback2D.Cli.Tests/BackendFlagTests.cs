#region

using System.Text.Json.Nodes;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The backend flags and <c>dv2d probe</c> (plans/C2-gpu-provider.md §6.4).
///     <para>
///         Everything that depends on the environment runs as a <b>subprocess</b>. It has to:
///         <c>RenderSurfaceProviderFactory</c> caches its probe for the life of a process and its
///         <c>ResetForTests</c> is internal to Core, so an in-process case would be answered by
///         whichever environment probed first, and would pass alone while lying in a suite.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class BackendFlagTests
{
    private const string BackendVariable = "DV2D_RENDER_BACKEND";

    private static string EmptyScene =>
        Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");

    private static string TempPng(string name) => Path.Combine(Path.GetTempPath(), name);

    /// <summary>Whether THIS machine can actually stand a GPU backend up, asked in a clean child.</summary>
    private static bool GpuAvailableHere()
    {
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = null
            },
            "probe", "--json");
        return run.Json()["gpu_available"]!.GetValue<bool>();
    }

    [Test]
    public async Task Probe_ExitsZero_AndNamesTheBackend()
    {
        // A CPU answer is not an error (design §10 risk 7): the command reports, it does not gate.
        CliRun run = Dv2d.Subprocess("probe");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdOut).Contains("backend=");
        await Assert.That(run.StdOut).Contains("reason=");
    }

    [Test]
    public async Task Probe_Json_CarriesTheRendererString()
    {
        // GL_RENDERER is the field that catches the nastiest failure: ANGLE loading fine but running
        // on WARP, which looks like a win in the log and is a 20x loss in the numbers (plan §10 R2).
        CliRun run = Dv2d.Subprocess("probe", "--json");
        JsonObject payload = run.Json();

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(payload["command"]!.GetValue<string>()).IsEqualTo("probe");
        await Assert.That(payload["reason"]!.GetValue<string>()).IsNotEmpty();
        await Assert.That(payload.ContainsKey("renderer")).IsTrue();
        await Assert.That(payload.ContainsKey("software_renderer")).IsTrue();
        await Assert.That(payload["duration_ms"]!.GetValue<double>()).IsGreaterThanOrEqualTo(0);

        bool gpu = payload["gpu_available"]!.GetValue<bool>();
        await Assert.That(payload["renderer"] is null).IsEqualTo(!gpu);
    }

    [Test]
    public async Task Probe_UnderForcedCpu_SaysSo_AndStillExitsZero()
    {
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = "cpu"
            },
            "probe", "--json");
        JsonObject payload = run.Json();

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(payload["gpu_available"]!.GetValue<bool>()).IsFalse();
        await Assert.That(payload["reason"]!.GetValue<string>()).IsEqualTo("forced-cpu");
        // The distinction the flag exists for: "no GPU here" versus "you told me not to look".
        await Assert.That(payload["forced_cpu"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Probe_RequireGpu_ExitsSix_WhenThereIsNoGpuPath()
    {
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = "cpu"
            },
            "probe", "--require-gpu");

        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdErr).Contains("--require-gpu");
    }

    [Test]
    public async Task Cpu_ResolvesToCpuRaster_AndSaysWhatWasAsked()
    {
        CliRun run = Dv2d.InProcess("render", "--fixture", EmptyScene, "--out",
            TempPng("dv2d-backend-cpu.png"), "--cpu", "--json");
        JsonObject payload = run.Json();

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(payload["backend"]!.GetValue<string>()).IsEqualTo("CpuRaster");
        await Assert.That(payload["backend_requested"]!.GetValue<string>()).IsEqualTo("cpu");
    }

    [Test]
    public async Task BackendFlag_UnknownValue_ExitsOne()
    {
        CliRun run = Dv2d.InProcess("render", "--fixture", EmptyScene, "--out",
            TempPng("dv2d-backend-bad.png"), "--backend", "nonsense");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("auto|cpu|gpu|angle|gl|force-gpu");
    }

    [Test]
    public async Task BackendFlag_CombinedWithCpu_ExitsOne()
    {
        CliRun run = Dv2d.InProcess("render", "--fixture", EmptyScene, "--out",
            TempPng("dv2d-backend-both.png"), "--cpu", "--backend", "gpu");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("cannot be combined");
    }

    [Test]
    public async Task Environment_TypoIsRejected_RatherThanSilentlyMeaningAuto()
    {
        // The library treats an unrecognised value as "unset", which is right for a library and wrong
        // for a tool: DV2D_RENDER_BACKEND=gpuu in a CI lane would quietly measure the CPU path and
        // report a green budget.
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = "gpuu"
            },
            "render", "--fixture", EmptyScene, "--out", TempPng("dv2d-backend-envtypo.png"));

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains(BackendVariable);
    }

    [Test]
    public async Task Environment_Cpu_IsHonouredWithNoFlag()
    {
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = "cpu"
            },
            "render", "--fixture", EmptyScene, "--out", TempPng("dv2d-backend-envcpu.png"), "--json");
        JsonObject payload = run.Json();

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(payload["backend"]!.GetValue<string>()).IsEqualTo("CpuRaster");
        await Assert.That(payload["backend_requested"]!.GetValue<string>()).IsEqualTo("cpu");
    }

    [Test]
    public async Task ExplicitBackend_OutranksAnEnvironmentThatSaysCpu()
    {
        // The precedence the review fix (C2 deviation 19) exists for, asserted at the CLI seam: a
        // stale shell variable must not override the flag the operator just typed. On a machine with
        // no GPU the correct answer is exit 6, never "0, quietly on the CPU".
        bool gpu = GpuAvailableHere();
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = "cpu"
            },
            "render", "--fixture", EmptyScene, "--out", TempPng("dv2d-backend-outrank.png"),
            "--backend", "force-gpu", "--json");

        if (!gpu)
        {
            await Assert.That(run.ExitCode).IsEqualTo(6);
            return;
        }

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Json()["backend"]!.GetValue<string>()).IsNotEqualTo("CpuRaster");
    }

    [Test]
    public async Task StrictBackend_TurnsAGpuRequestIntoAHardFailure_WhenThereIsNoGpu()
    {
        bool gpu = GpuAvailableHere();
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = null
            },
            "render", "--fixture", EmptyScene, "--out", TempPng("dv2d-backend-strict.png"),
            "--gpu", "--strict-backend", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(gpu ? 0 : 6);
    }

    [Test]
    public async Task GoldenLane_DefaultsToCpu_EvenOnAGpuMachine()
    {
        // The committed corpus is goldens/cpu/ and CPU is authoritative (00-overview.md §3.9). If
        // `golden verify` auto-probed, every developer with a GPU would see a rasterizer difference
        // reported as a pixel regression, on exit 4, which reads as "the change is bad".
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = null
            },
            "golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");
        JsonObject payload = run.Json();

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(payload["backend"]!.GetValue<string>()).IsEqualTo("CpuRaster");
    }

    /// <summary>
    ///     <c>export</c> pins CPU too, and for a harder reason than the golden lane's.
    ///     <para>
    ///         <c>SceneExportSession</c> awaits its sink between frames, so the loop resumes on
    ///         whatever pool thread the continuation lands on, while <c>GpuSurfaceProvider</c> is bound
    ///         to the thread that created its EGL context. An auto-probe that finds ANGLE therefore
    ///         hands the session a provider it refuses, so auto-probing here is auto-probing into a
    ///         guaranteed refusal. C2 Stage 1 owns making it work.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ExportLane_DefaultsToCpu_EvenOnAGpuMachine()
    {
        string demo = Dv2d.RequireDemo();
        string output = Path.Combine(Path.GetTempPath(), $"dv2d-export-cpu-{Guid.NewGuid():N}.gif");

        try
        {
            CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
                {
                    [BackendVariable] = null
                },
                "export", "--demo", demo, "--from", "0", "--to", "2", "--format", "gif", "--fps", "20",
                "--size", "64x64", "--out", output, "--json");

            await Assert.That(run.ExitCode).IsEqualTo(0);
            await Assert.That(run.Json()["backend"]!.GetValue<string>()).IsEqualTo("CpuRaster");
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    /// <summary>
    ///     And an explicit <c>--gpu</c> export refuses in the environment channel, not the crash one.
    ///     Exit 6 is what a lane reads as "this machine/build cannot do that"; exit 3 would say the run
    ///     broke. On a machine with no GPU the request degrades to CPU and succeeds.
    /// </summary>
    [Test]
    public async Task ExportOnAnExplicitGpu_ExitsSix_RatherThanFailingMidRun()
    {
        if (!GpuAvailableHere())
        {
            throw new SkipTestException("no GPU backend here; --gpu degrades to CPU and export succeeds");
        }

        string demo = Dv2d.RequireDemo();
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = null
            },
            "export", "--demo", demo, "--from", "0", "--to", "2", "--format", "gif", "--fps", "20",
            "--size", "64x64", "--gpu",
            "--out", Path.Combine(Path.GetTempPath(), $"dv2d-export-gpu-{Guid.NewGuid():N}.gif"));

        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdErr).Contains("C2 Stage 1");
    }

    [Test]
    public async Task PlainGpu_FallsBackQuietlyButNotSilently()
    {
        // Without --strict-backend, --gpu degrades to CPU rather than failing. It must say so, or a
        // benchmark run reports software numbers under a GPU heading.
        bool gpu = GpuAvailableHere();
        CliRun run = Dv2d.Subprocess(new Dictionary<string, string?>
            {
                [BackendVariable] = null
            },
            "render", "--fixture", EmptyScene, "--out", TempPng("dv2d-backend-gpu.png"), "--gpu",
            "--json");
        JsonObject payload = run.Json();

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(payload["backend_requested"]!.GetValue<string>()).IsEqualTo("gpu");

        if (gpu)
        {
            await Assert.That(payload["backend"]!.GetValue<string>()).IsNotEqualTo("CpuRaster");
        }
        else
        {
            await Assert.That(payload["backend"]!.GetValue<string>()).IsEqualTo("CpuRaster");
            await Assert.That(run.StdErr).Contains("using CpuRaster");
        }
    }
}
