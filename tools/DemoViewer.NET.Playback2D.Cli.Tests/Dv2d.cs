#region

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Cli;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>One CLI invocation's result.</summary>
/// <param name="ExitCode">The process (or <c>Main</c>) return value.</param>
/// <param name="StdOut">Everything written to stdout.</param>
/// <param name="StdErr">Everything written to stderr.</param>
internal readonly record struct CliRun(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Parses stdout as the single JSON object <c>--json</c> promises.</summary>
    /// <exception cref="JsonException">stdout is not exactly one JSON object.</exception>
    public JsonObject Json() =>
        JsonNode.Parse(StdOut) as JsonObject
        ?? throw new JsonException($"stdout was not a JSON object:\n{StdOut}");
}

/// <summary>
///     Invokes <c>dv2d</c> two ways: in-process (fast, and the only way to cover <c>Main</c>'s own
///     dispatch) and as a real subprocess (the only way to observe the loaded-assembly set and to prove
///     render determinism across process boundaries).
/// </summary>
internal static class Dv2d
{
    private static readonly Lock _consoleLock = new();

    /// <summary>The repo root, located by walking up for the slnx.</summary>
    public static string RepoRoot { get; } =
        DemoTestHelper.FindRepoRoot()
        ?? throw new InvalidOperationException("could not locate the repository root.");

    /// <summary>The committed fixture corpus.</summary>
    public static string CorpusDirectory { get; } =
        Path.Combine(RepoRoot, "tests", "fixtures", "playback2d");

    /// <summary>The baked map-asset root.</summary>
    public static string AssetsDirectory { get; } = Path.Combine(RepoRoot, "assets");

    /// <summary>
    ///     The trimmed three-round de_nuke GOTV demo committed at <c>assets/tour/</c>, or whatever
    ///     <c>DemoTestHelper</c> finds. Null when neither is present.
    /// </summary>
    public static string? DemoPath { get; } = ResolveDemo();

    /// <summary>The demo path, or a skip. Demo-dependent cases are skipped, never silently passed.</summary>
    /// <exception cref="SkipTestException">No demo is available.</exception>
    public static string RequireDemo() => DemoPath ?? throw new SkipTestException(
        "no CS2 demo available (expected assets/tour/sample-de_nuke.dem, DEMO_PATH, or demos/).");

    /// <summary>
    ///     Runs <c>Program.Main</c> in this process with stdout and stderr captured. Serialized on a lock
    ///     because console redirection is process-global; callers must also be <c>[NotInParallel]</c>.
    /// </summary>
    /// <param name="args">The arguments, without the executable name.</param>
    public static CliRun InProcess(params string[] args)
    {
        lock (_consoleLock)
        {
            TextWriter savedOut = Console.Out;
            TextWriter savedError = Console.Error;
            StringWriter capturedOut = new();
            StringWriter capturedError = new();
            try
            {
                // TUnit0055 warns that redirecting the console can break its logging. Capturing the two
                // streams IS the assertion here (the --json discipline is a split between them), the
                // redirect is restored in the finally, and every caller is [NotInParallel].
#pragma warning disable TUnit0055
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                int exit = Program.Main(args);
                return new CliRun(exit, capturedOut.ToString(), capturedError.ToString());
            }
            finally
            {
                Console.SetOut(savedOut);
                Console.SetError(savedError);
#pragma warning restore TUnit0055
            }
        }
    }

    /// <summary>Runs the built <c>dv2d</c> executable as a child process.</summary>
    /// <param name="args">The arguments, without the executable name.</param>
    public static CliRun Subprocess(params string[] args) => Subprocess(null, args);

    /// <summary>
    ///     Runs the built <c>dv2d</c> executable as a child process with extra environment variables.
    ///     <para>
    ///         A subprocess is not a convenience for the backend cases, it is a requirement:
    ///         <c>RenderSurfaceProviderFactory</c> caches its probe for the life of the process and its
    ///         <c>ResetForTests</c> is internal to Core, so an in-process run would answer every later
    ///         case from whichever environment happened to probe first.
    ///     </para>
    /// </summary>
    /// <param name="environment">Variables to set (or clear, with a null value) for the child.</param>
    /// <param name="args">The arguments, without the executable name.</param>
    public static CliRun Subprocess(IReadOnlyDictionary<string, string?>? environment,
        params string[] args)
    {
        (string fileName, IReadOnlyList<string> prefix) = LaunchTarget();

        ProcessStartInfo info = new()
        {
            FileName = fileName,
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
            {
                // Remove rather than set-to-null: on Unix an empty string is a SET variable, and
                // "DV2D_RENDER_BACKEND=" would parse as unrecognised rather than absent.
                if (value is null)
                {
                    info.Environment.Remove(name);
                }
                else
                {
                    info.Environment[name] = value;
                }
            }
        }

        foreach (string argument in prefix)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)
                                ?? throw new InvalidOperationException($"could not start {fileName}.");

        // Read both streams before waiting: a full pipe buffer on either one deadlocks the child.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new CliRun(process.ExitCode, stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    /// <summary>The directory the CLI project builds into, e.g. <c>artifacts/bin/…/release</c>.</summary>
    public static string CliOutputDirectory { get; } = ResolveCliOutputDirectory();

    private static (string FileName, IReadOnlyList<string> Prefix) LaunchTarget()
    {
        string native = Path.Combine(CliOutputDirectory,
            OperatingSystem.IsWindows() ? "dv2d.exe" : "dv2d");
        if (File.Exists(native))
        {
            return (native, []);
        }

        string managed = Path.Combine(CliOutputDirectory, "dv2d.dll");
        return File.Exists(managed)
            ? ("dotnet", new[] { managed })
            : throw new FileNotFoundException($"no dv2d host in {CliOutputDirectory}.", native);
    }

    private static string ResolveCliOutputDirectory()
    {
        // The test assembly sits in artifacts/bin/<test project>/<config>/; the CLI's own output is its
        // sibling. Reading the configuration off the path rather than a compile-time constant keeps a
        // Debug run pointing at the Debug dv2d.
        DirectoryInfo testDir = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string configuration = testDir.Name;
        DirectoryInfo? binRoot = testDir.Parent?.Parent;

        if (binRoot is not null)
        {
            string candidate = Path.Combine(binRoot.FullName, "DemoViewer.NET.Playback2D.Cli",
                configuration);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(RepoRoot, "artifacts", "bin", "DemoViewer.NET.Playback2D.Cli",
            configuration);
    }

    private static string? ResolveDemo()
    {
        // assets/tour/sample-de_nuke.dem is committed and app-loadable, but DemoTestHelper's search
        // order (DEMO_PATH / TestData / demos/) never looks there — so name it explicitly first, and
        // fall back to whatever a developer has staged.
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is not null)
        {
            string tour = Path.Combine(root, "assets", "tour", "sample-de_nuke.dem");
            if (File.Exists(tour))
            {
                return tour;
            }
        }

        return DemoTestHelper.FindDemoPath();
    }
}
