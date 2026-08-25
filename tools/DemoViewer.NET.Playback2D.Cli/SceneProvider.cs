#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Where the frames a command renders come from: a serialized fixture, or a private tracker replay
///     over a demo. One abstraction so <c>render</c>, <c>bench</c> and <c>fixture capture</c> take the
///     same <c>--fixture</c>/<c>--demo</c> pair without three copies of the wiring.
/// </summary>
internal abstract class SceneProvider : IDisposable
{
    /// <summary>How many distinct frames this provider can produce.</summary>
    public abstract int Count { get; }

    /// <summary>The kind token reported in JSON: <c>fixture</c> or <c>demo</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>A human name for the source: the fixture name, or the demo file name.</summary>
    public abstract string Name { get; }

    /// <summary>The camera the source suggests; <c>--camera</c> overrides it.</summary>
    public abstract ViewportTransform Camera { get; }

    /// <summary>The size the source suggests; <c>--size</c> overrides it.</summary>
    public abstract SKSizeI DefaultSize { get; }

    /// <summary>The map the scene belongs to, or null.</summary>
    public abstract string? MapName { get; }

    /// <summary>The bundle version the fixture was captured against, or null.</summary>
    public virtual string? MapVersion => null;

    /// <summary>Wall-clock milliseconds spent parsing a demo; 0 for a fixture.</summary>
    public virtual double ParseMs => 0;

    /// <summary>The identity to stamp into a captured fixture's <c>SourceDemoId</c>.</summary>
    public virtual string? SourceDemoId => null;

    /// <summary>The frame at an index. Indices are clamped into range by the concrete providers.</summary>
    /// <param name="index">A zero-based frame index.</param>
    public abstract Scene2DFrame FrameAt(int index);

    /// <summary>The clock at an index.</summary>
    /// <param name="index">A zero-based frame index.</param>
    public abstract SceneTime TimeAt(int index);

    /// <inheritdoc />
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Builds the provider the shared flags describe. Consumes <c>--fixture</c>, <c>--demo</c>,
    ///     <c>--tick</c>, <c>--frame</c>, <c>--from</c>, <c>--fps</c>, <c>--speed</c>.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="requireOne">When true, neither flag present is a usage error.</param>
    /// <exception cref="CliUsageException">Both or neither source flag was given.</exception>
    public static SceneProvider Build(CliArgs args, bool requireOne = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? fixturePath = args.String("fixture");
        string? demoPath = args.String("demo");

        if (fixturePath is not null && demoPath is not null)
        {
            throw new CliUsageException("--fixture and --demo are mutually exclusive.");
        }

        if (fixturePath is not null)
        {
            return FixtureSceneProvider.Load(fixturePath);
        }

        if (demoPath is not null)
        {
            return DemoSceneProvider.Open(args, demoPath);
        }

        if (requireOne)
        {
            throw new CliUsageException("one of --fixture <path> or --demo <path> is required.");
        }

        throw new CliUsageException("no scene source given.");
    }
}

/// <summary>A single serialized scene, replayed as many times as a caller asks for.</summary>
internal sealed class FixtureSceneProvider : SceneProvider
{
    private readonly SceneFixture _fixture;

    private FixtureSceneProvider(SceneFixture fixture, string name)
    {
        _fixture = fixture;
        Name = name;
    }

    /// <inheritdoc />
    public override int Count => 1;

    /// <inheritdoc />
    public override string Kind => "fixture";

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override ViewportTransform Camera => _fixture.Camera;

    /// <inheritdoc />
    public override SKSizeI DefaultSize =>
        _fixture.Size.Width > 0 && _fixture.Size.Height > 0 ? _fixture.Size : new SKSizeI(1920, 1080);

    /// <inheritdoc />
    public override string? MapName => string.IsNullOrEmpty(_fixture.MapName) ? null : _fixture.MapName;

    /// <inheritdoc />
    public override string? MapVersion => _fixture.MapVersion;

    /// <inheritdoc />
    public override string? SourceDemoId => _fixture.SourceDemoId;

    /// <summary>The loaded fixture, for commands that need more than the frame.</summary>
    public SceneFixture Fixture => _fixture;

    /// <summary>Reads a fixture from disk.</summary>
    /// <param name="path">Path to the <c>.scene.json</c>.</param>
    /// <exception cref="FileNotFoundException">No file at <paramref name="path" />.</exception>
    public static FixtureSceneProvider Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"fixture not found: {path}", path);
        }

        string name = Path.GetFileName(path);
        if (name.EndsWith(".scene.json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".scene.json".Length];
        }

        return new FixtureSceneProvider(SceneFixture.Load(path), name);
    }

    /// <inheritdoc />
    public override Scene2DFrame FrameAt(int index) => _fixture.Frame;

    /// <inheritdoc />
    public override SceneTime TimeAt(int index) => _fixture.Time;
}

/// <summary>A private tracker replay over a parsed demo. Never touches the app's playback clock.</summary>
internal sealed class DemoSceneProvider : SceneProvider
{
    private readonly ParsedDemo _demo;
    private readonly TrackerFrameSource _source;
    private bool _disposed;

    private DemoSceneProvider(ParsedDemo demo, TrackerFrameSource source, string name, int demoFrameIndex,
        double parseMs)
    {
        _demo = demo;
        _source = source;
        Name = name;
        DemoFrameIndex = demoFrameIndex;
        ParseMs = parseMs;
    }

    /// <inheritdoc />
    public override int Count => _source.FrameCount;

    /// <inheritdoc />
    public override string Kind => "demo";

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The demo frame index output frame 0 sits on.</summary>
    public int DemoFrameIndex { get; }

    /// <summary>The server tick at <see cref="DemoFrameIndex" />.</summary>
    public int Tick => _demo.Frames[DemoFrameIndex].ServerTick;

    /// <inheritdoc />
    public override double ParseMs { get; }

    /// <inheritdoc />
    public override string? SourceDemoId => Name;

    /// <inheritdoc />
    public override string? MapName => string.IsNullOrEmpty(_demo.MapName) ? null : _demo.MapName;

    /// <summary>A demo has no fixture camera; the caller falls back to <c>fit-map</c>.</summary>
    public override ViewportTransform Camera => default;

    /// <inheritdoc />
    public override SKSizeI DefaultSize => new(1920, 1080);

    /// <summary>Opens the demo and seeds a private tracker at the resolved frame.</summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    public static DemoSceneProvider Open(CliArgs args, string demoPath)
    {
        ArgumentNullException.ThrowIfNull(args);

        ParsedDemo demo = DemoInput.Load(demoPath, out double parseMs);

        // --from is bench's spelling of a start frame; --tick/--frame is render's. Either resolves to
        // the same thing: the demo frame output frame 0 sits on.
        int start = args.String("from") is not null
            ? args.Int("from", 0)
            : DemoInput.ResolveFrameIndex(args, demo);

        if (start < 0 || start >= demo.Frames.Count)
        {
            throw new CliUsageException(
                $"--from {start} is outside the demo (frames 0..{demo.Frames.Count - 1}).");
        }

        int tickRate = DemoInput.TickRate(demo);
        int fps = args.Int("fps", tickRate);
        double speed = args.Double("speed", 1.0);

        TrackerFrameSource source = new(demo.Frames, new SceneFrameBuilder(), start,
            demo.Frames.Count - 1, fps, speed, tickRate)
        {
            MapName = demo.MapName
        };
        source.Prepare(CancellationToken.None);

        return new DemoSceneProvider(demo, source, Path.GetFileName(demoPath), start, parseMs);
    }

    /// <inheritdoc />
    public override Scene2DFrame FrameAt(int index) =>
        _source.FrameAt(Math.Clamp(index, 0, _source.FrameCount - 1));

    /// <inheritdoc />
    public override SceneTime TimeAt(int index) =>
        _source.TimeAt(Math.Clamp(index, 0, _source.FrameCount - 1));

    /// <inheritdoc />
    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _source.Dispose();
        }

        base.Dispose();
    }
}
