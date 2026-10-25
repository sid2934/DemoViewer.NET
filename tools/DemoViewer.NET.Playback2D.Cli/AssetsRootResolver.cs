#region

using DemoViewer.NET.Playback2D.Pipeline.Headless;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Runs the ladder that finds the baked <c>assets/</c> root: <c>--assets</c> →
///     <c>DV2D_ASSETS</c> → a walk-up probe.
///     <para>
///         This is the half that reads process state, so it stays with the argument parser. What the
///         answer means to a render is <see cref="AssetsRoot" />.
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
            return new AssetsRoot(Directory.Exists(flag) ? Path.GetFullPath(flag) : null,
                AssetsRootSource.Flag, [flag]);
        }

        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new AssetsRoot(Directory.Exists(env) ? Path.GetFullPath(env) : null,
                AssetsRootSource.Env, [env]);
        }

        List<string> probed = [];
        foreach (string start in new[]
                 {
                     AppContext.BaseDirectory, Directory.GetCurrentDirectory()
                 })
        {
            DirectoryInfo? dir = new(start);
            for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "assets");
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
