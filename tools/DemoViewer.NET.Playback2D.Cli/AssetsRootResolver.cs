namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>Where a resolved asset root came from. Reported in every <c>--json</c> payload.</summary>
internal enum AssetsRootSource
{
    /// <summary>An explicit <c>--assets &lt;dir&gt;</c>.</summary>
    Flag,

    /// <summary>The <c>DV2D_ASSETS</c> environment variable.</summary>
    Env,

    /// <summary>A walk-up probe found an <c>assets/</c> directory.</summary>
    Probe,

    /// <summary><c>--no-radar</c>: map art is deliberately not loaded.</summary>
    Disabled,

    /// <summary>Nothing was found.</summary>
    NotFound
}

/// <summary>A resolved (or deliberately absent) map-asset root.</summary>
/// <param name="Path">The directory holding one subdirectory per map, or null.</param>
/// <param name="Source">How it was resolved.</param>
/// <param name="Probed">The candidates that were considered, for the exit-2 message.</param>
internal sealed record AssetsRoot(string? Path, AssetsRootSource Source, IReadOnlyList<string> Probed)
{
    /// <summary>The lowercase token that appears as <c>assets_source</c> in JSON payloads.</summary>
    public string SourceToken => Source switch
    {
        AssetsRootSource.Flag => "flag",
        AssetsRootSource.Env => "env",
        AssetsRootSource.Probe => "probe",
        AssetsRootSource.Disabled => "disabled",
        _ => "not-found"
    };
}

/// <summary>
///     Resolves the baked <c>assets/</c> root that <c>tools/DemoViewer.NET.AssetBaker</c> writes — one
///     subdirectory per map holding <c>bundle.json</c> plus its radar PNGs (C1 decision 6).
///     <para>
///         The ladder is <c>--assets</c> → <c>DV2D_ASSETS</c> → a walk-up probe, and the winning rung is
///         reported, not just the path: a golden failure caused by a different asset root has to be
///         diagnosable from a CI log alone.
///     </para>
/// </summary>
internal static class AssetsRootResolver
{
    /// <summary>The environment variable consulted when <c>--assets</c> is absent.</summary>
    public const string EnvironmentVariable = "DV2D_ASSETS";

    /// <summary>Runs the ladder. Consumes <c>--assets</c> and <c>--no-radar</c>.</summary>
    /// <param name="args">The parsed arguments.</param>
    public static AssetsRoot Resolve(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool noRadar = args.Flag("no-radar");
        string? flag = args.String("assets");

        if (noRadar)
        {
            return new AssetsRoot(null, AssetsRootSource.Disabled, []);
        }

        if (!string.IsNullOrWhiteSpace(flag))
        {
            // An explicit flag that does not exist is an error at the call site, not a silent
            // fall-through to the probe: the caller stated where the art is.
            return new AssetsRoot(Directory.Exists(flag) ? System.IO.Path.GetFullPath(flag) : null,
                AssetsRootSource.Flag, [flag]);
        }

        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new AssetsRoot(Directory.Exists(env) ? System.IO.Path.GetFullPath(env) : null,
                AssetsRootSource.Env, [env]);
        }

        List<string> probed = [];
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(start);
            for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "assets");
                probed.Add(candidate);
                if (Directory.Exists(candidate))
                {
                    return new AssetsRoot(candidate, AssetsRootSource.Probe, probed);
                }
            }
        }

        return new AssetsRoot(null, AssetsRootSource.NotFound, probed);
    }
}
