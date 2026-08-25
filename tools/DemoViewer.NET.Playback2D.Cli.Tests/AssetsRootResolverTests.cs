#region

using DemoViewer.NET.Playback2D.Cli;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The <c>--assets</c> → <c>DV2D_ASSETS</c> → walk-up ladder, and the reported source. The source
///     matters as much as the path: a golden failure caused by a different asset root has to be
///     diagnosable from a CI log alone.
/// </summary>
[NotInParallel]
public class AssetsRootResolverTests
{
    [Test]
    public async Task Flag_WinsOverEnvironment()
    {
        using TempDirectory flagDir = new();
        using TempDirectory envDir = new();
        using EnvironmentVariable env = new(AssetsRootResolver.EnvironmentVariable, envDir.Path);

        AssetsRoot root = AssetsRootResolver.Resolve(CliArgs.Parse(["render", "--assets", flagDir.Path]));

        await Assert.That(root.Source).IsEqualTo(AssetsRootSource.Flag);
        await Assert.That(root.Path).IsEqualTo(Path.GetFullPath(flagDir.Path));
    }

    [Test]
    public async Task Environment_WinsOverProbe()
    {
        using TempDirectory envDir = new();
        using EnvironmentVariable env = new(AssetsRootResolver.EnvironmentVariable, envDir.Path);

        AssetsRoot root = AssetsRootResolver.Resolve(CliArgs.Parse(["render"]));

        await Assert.That(root.Source).IsEqualTo(AssetsRootSource.Env);
        await Assert.That(root.Path).IsEqualTo(Path.GetFullPath(envDir.Path));
    }

    [Test]
    public async Task Probe_FindsTheRepoAssetsDirectory()
    {
        using EnvironmentVariable env = new(AssetsRootResolver.EnvironmentVariable, null);

        AssetsRoot root = AssetsRootResolver.Resolve(CliArgs.Parse(["render"]));

        await Assert.That(root.Source).IsEqualTo(AssetsRootSource.Probe);
        await Assert.That(Directory.Exists(root.Path)).IsTrue();
    }

    [Test]
    public async Task NoRadar_ShortCircuitsToDisabled()
    {
        using TempDirectory flagDir = new();

        AssetsRoot root =
            AssetsRootResolver.Resolve(CliArgs.Parse(["render", "--assets", flagDir.Path, "--no-radar"]));

        await Assert.That(root.Source).IsEqualTo(AssetsRootSource.Disabled);
        await Assert.That(root.Path).IsNull();
        await Assert.That(root.SourceToken).IsEqualTo("disabled");
    }

    [Test]
    public async Task MissingFlagRoot_ReportsNoPath_AndListsWhatWasProbed()
    {
        AssetsRoot root = AssetsRootResolver.Resolve(
            CliArgs.Parse(["render", "--assets", Path.Combine(Path.GetTempPath(), "dv2d-no-such-root")]));

        await Assert.That(root.Source).IsEqualTo(AssetsRootSource.Flag);
        await Assert.That(root.Path).IsNull();
        await Assert.That(root.Probed).IsNotEmpty();
    }

    [Test]
    public async Task MissingRoot_IsExitTwo_NotASilentBlankRender()
    {
        string fixturePath = Path.Combine(Dv2d.CorpusDirectory, "scenes", "duel-mirage-b.scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixturePath,
            "--assets", Path.Combine(Path.GetTempPath(), "dv2d-no-such-root"));

        await Assert.That(run.ExitCode).IsEqualTo(2);
    }
}

/// <summary>A temporary directory that removes itself.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dv2d-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's absolute path.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }
}

/// <summary>Sets an environment variable for the scope of a test and restores it afterwards.</summary>
internal sealed class EnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    /// <summary>Sets the variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value, or null to clear it.</param>
    public EnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc />
    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
