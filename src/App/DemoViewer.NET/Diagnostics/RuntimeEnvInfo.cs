#region

using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using CS2DemoKit.Parser;
using DemoViewer.NET.Controls;

#endregion

namespace DemoViewer.NET.Diagnostics;

/// <summary>
///     App-layer helper for the Diagnostics tab's always-on info. Gathers the
///     system/session rows and the curated env-var allowlist rows as
///     <see cref="KvpRow" /> lists. Pure reflection / environment reads, cheap, synchronous, and
///     safe in any build (the compile-gated profiling panels live in the VM, not here).
/// </summary>
public static class RuntimeEnvInfo
{
    /// <summary>
    ///     Curated allowlist of runtime-affecting, non-secret environment variables surfaced in the
    ///     Diagnostics tab. <b>Extend here only</b>, never enumerate all env vars, which
    ///     can leak Steam tokens, <c>PATH</c>, or auth. On the browser host every read is <c>(unset)</c>.
    /// </summary>
    public static readonly string[] EnvAllowlist =
    [
        // GC / JIT runtime knobs (affect perf characteristics in a bug report)
        "DOTNET_gcServer",
        "DOTNET_GCHeapCount",
        "DOTNET_gcConcurrent",
        "DOTNET_TieredCompilation",
        "DOTNET_TieredPGO",
        "DOTNET_ReadyToRun",
        // App-specific
        "DEMOVIEWER_PROFILE", // runtime profiling switch (ProfilingSession)
        "DEMO_PATH", // auto-load path (DEBUG)
        // Avalonia rendering / platform (affect render bugs)
        "AVALONIA_GLOBAL_SCALE_FACTOR",
        "AVALONIA_SCREEN_SCALE_FACTORS",
        "AVALONIA_RENDER_MODE"
    ];

    /// <summary>
    ///     System/session rows: app + parser version, .NET runtime, OS/arch, GC mode,
    ///     processor count, and the runtime profiling state (the single <c>Profiling.Enabled</c> switch
    ///     plus whether the last parse captured data, both off/No by default).
    /// </summary>
    public static IReadOnlyList<KvpRow> SystemRows()
    {
        List<KvpRow> rows =
        [
            Row("app version", AppVersion()),
            Row("parser version", ParserVersion()),
            Row(".NET runtime", RuntimeInformation.FrameworkDescription),
            Row("OS", RuntimeInformation.OSDescription),
            Row("OS arch", RuntimeInformation.OSArchitecture.ToString()),
            Row("process arch", RuntimeInformation.ProcessArchitecture.ToString()),
            Row("GC mode", $"{(GCSettings.IsServerGC ? "Server" : "Workstation")} · {GCSettings.LatencyMode}"),
            Row("processors", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture))
        ];

        // At-a-glance "is profiling active" line a bug reporter wants. Profiling is a single runtime
        // switch now (Profiling.Enabled, via DEMOVIEWER_PROFILE); report its live state plus whether the
        // last parse actually captured data (the parse panel populates only from a profiled load).
        bool profiling = Profiling.Enabled;
        bool parseCaptured = ParseProfilingSnapshot.Read().Enabled;
        rows.Add(Row("profiling", profiling ? "On" : "Off"));
        rows.Add(Row("parse profile captured", parseCaptured ? "Yes" : "No"));

        return rows;
    }

    /// <summary>
    ///     Env-var rows: one per <see cref="EnvAllowlist" /> entry, value or
    ///     <c>(unset)</c>. On the browser host all read <c>(unset)</c>.
    /// </summary>
    public static IReadOnlyList<KvpRow> EnvRows()
    {
        List<KvpRow> rows = new(EnvAllowlist.Length);
        foreach (string name in EnvAllowlist)
        {
            rows.Add(Row(name, Environment.GetEnvironmentVariable(name) ?? "(unset)"));
        }

        return rows;
    }

    private static string AppVersion() => InformationalVersion(typeof(RuntimeEnvInfo).Assembly);

    // Parser version is a DIFFERENT assembly than the app, read the informational
    // version attribute off the parser assembly, not the app's ThisAssembly.
    private static string ParserVersion() => InformationalVersion(typeof(DemoParser).Assembly);

    private static string InformationalVersion(Assembly asm) =>
        asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "(unknown)";

    private static KvpRow Row(string key, string value) => new(key, value, false, null);
}
