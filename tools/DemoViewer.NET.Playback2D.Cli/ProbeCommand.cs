#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Core.Rendering;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     <c>dv2d probe</c> — asks which render-surface backend this machine can actually provide, and says
///     why (plans/C2-gpu-provider.md §6.4).
///     <para>
///         It exists because "the GPU lane went green" and "the GPU lane ran on a GPU" are different
///         claims. The probe prints <c>GL_RENDERER</c>, so a lane that quietly fell through to WARP or
///         llvmpipe is visible in the log rather than hidden inside a plausible frame time. A CPU answer
///         is <b>not</b> an error — design §10 risk 7 makes CPU the contract baseline — so the command
///         exits 0 either way unless <c>--require-gpu</c> says otherwise.
///     </para>
/// </summary>
internal static class ProbeCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The parsed arguments.</param>
    public static ExitCode Run(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        args.ConsumeVerbs();
        bool requireGpu = args.Flag("require-gpu");
        bool wantsHardware = args.Flag("require-hardware");
        args.Flag("json");
        args.Flag("quiet");
        args.ThrowIfUnconsumed();

        // Deliberately Probe(), not Create(): the question is what this machine CAN do, so the answer
        // must not be filtered through a preference. A DV2D_RENDER_BACKEND=cpu shell still gets told
        // there is a GPU here — reported as `forced_cpu`, which is the fact somebody debugging a slow
        // CI lane actually needs.
        RenderSurfaceProbe probe = RenderSurfaceProviderFactory.Probe();
        bool forcedCpu = !probe.GpuAvailable &&
                         RenderBackendPreferenceParser.FromEnvironment() ==
                         RenderBackendPreference.ForceCpu;

        if (ConsoleOut.IsJson)
        {
            ConsoleOut.Json(new JsonObject
            {
                ["schema_version"] = 1,
                ["command"] = "probe",
                ["ok"] = true,
                ["backend"] = probe.Backend.ToString(),
                ["gpu_available"] = probe.GpuAvailable,
                ["reason"] = probe.Reason,
                ["renderer"] = probe.Renderer,
                ["vendor"] = probe.Vendor,
                ["version"] = probe.Version,
                ["software_renderer"] = probe.IsSoftwareRenderer,
                ["forced_cpu"] = forcedCpu,
                ["duration_ms"] = Math.Round(probe.Duration.TotalMilliseconds, 3)
            });
        }
        else
        {
            ConsoleOut.Info(probe.Describe());
        }

        if (requireGpu && !probe.GpuAvailable)
        {
            throw new BackendUnavailableException(string.Create(CultureInfo.InvariantCulture,
                $"--require-gpu: no GPU render surface backend is available here ({probe.Reason})."));
        }

        // A software rasterizer IS a GPU backend as far as the code path goes — the parity suites want
        // exactly that on hosted runners. It is only a throughput measurement that it invalidates, so
        // the stricter assertion is a separate flag rather than a stricter --require-gpu.
        if (wantsHardware && probe.IsSoftwareRenderer)
        {
            throw new BackendUnavailableException(string.Create(CultureInfo.InvariantCulture,
                $"--require-hardware: the backend is a software rasterizer ('{probe.Renderer}')."));
        }

        return ExitCode.Success;
    }
}
