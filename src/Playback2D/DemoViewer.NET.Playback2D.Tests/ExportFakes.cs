#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     A sink that records what it was handed and nothing else. Every export-loop assertion — frame
///     count, buffer size, dispose-exactly-once — reads off this.
/// </summary>
internal sealed class RecordingFrameSink : IFrameSink
{
    private readonly int _throwOnFrame;

    /// <summary>Creates a sink.</summary>
    /// <param name="throwOnFrame">1-based frame to fail on, or -1 to accept everything.</param>
    public RecordingFrameSink(int throwOnFrame = -1) => _throwOnFrame = throwOnFrame;

    /// <summary>One entry per accepted frame.</summary>
    public List<(int Length, int Width, int Height)> Frames { get; } = [];

    /// <summary>How many times <see cref="DisposeAsync" /> ran. Must be exactly 1 after a session.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>The first byte of every frame, for a cheap "did the pixels change" check.</summary>
    public List<byte> FirstBytes { get; } = [];

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Frames.Count + 1 == _throwOnFrame)
        {
            throw new InvalidOperationException("the encoder died");
        }

        Frames.Add((rgba.Length, width, height));
        FirstBytes.Add(rgba.Span.Length > 0 ? rgba.Span[0] : (byte)0);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A sink that cancels the run from inside, at a chosen frame. For the cancel-mid-render case.</summary>
internal sealed class CancellingFrameSink : IFrameSink
{
    private readonly int _cancelAtFrame;
    private readonly CancellationTokenSource _source;

    /// <summary>Creates the sink.</summary>
    /// <param name="source">The token source to trip.</param>
    /// <param name="cancelAtFrame">1-based frame at which to trip it.</param>
    public CancellingFrameSink(CancellationTokenSource source, int cancelAtFrame)
    {
        _source = source;
        _cancelAtFrame = cancelAtFrame;
    }

    /// <summary>Frames accepted before the cancellation took effect.</summary>
    public int Written { get; private set; }

    /// <summary>How many times <see cref="DisposeAsync" /> ran.</summary>
    public int DisposeCount { get; private set; }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Written++;

        if (Written >= _cancelAtFrame)
        {
            _source.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A tick → HUD state function with fixed content, for the HUD layer tests.</summary>
internal sealed class StubHudDataSource : IHudDataSource
{
    private readonly HudSnapshot _snapshot;

    /// <summary>Creates the source.</summary>
    /// <param name="snapshot">What every tick answers.</param>
    public StubHudDataSource(HudSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>How many times <see cref="At" /> was called. Pins the once-per-frame read.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public HudSnapshot At(int tick)
    {
        Reads++;
        return _snapshot with
        {
            Tick = tick
        };
    }
}

/// <summary>
///     A CPU provider wearing another backend's badge, so the session's backend refusal can be
///     exercised on a machine with no GPU — which is every CI runner, and the point: the refusal must be
///     testable without the hardware it refuses.
/// </summary>
internal sealed class MislabelledBackendProvider : IRenderSurfaceProvider
{
    private readonly CpuSurfaceProvider _inner = new();

    /// <summary>Creates the provider.</summary>
    /// <param name="backend">The backend to claim.</param>
    public MislabelledBackendProvider(RenderBackend backend) => Backend = backend;

    /// <summary>How many surfaces were asked for. Zero is how "refused before rendering" is asserted.</summary>
    public int SurfacesCreated { get; private set; }

    /// <inheritdoc />
    public RenderBackend Backend { get; }

    /// <inheritdoc />
    public SKSurface CreateSurface(SKSizeI size)
    {
        SurfacesCreated++;
        return _inner.CreateSurface(size);
    }

    /// <inheritdoc />
    public void Flush(SKSurface surface) => _inner.Flush(surface);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}

/// <summary>Small builders the export tests share.</summary>
internal static class ExportFixtures
{
    /// <summary>The budget scene, repeated so a source can produce a run of frames.</summary>
    /// <param name="frames">How many frames the source yields.</param>
    public static FixtureFrameSource Source(int frames)
    {
        SceneFixture[] fixtures = new SceneFixture[Math.Max(1, frames)];
        SceneFixture one = SyntheticScenes.FullSceneBudget();
        for (int i = 0; i < fixtures.Length; i++)
        {
            fixtures[i] = one;
        }

        return new FixtureFrameSource(fixtures);
    }

    /// <summary>A request over <paramref name="frames" /> frames with sane defaults.</summary>
    /// <param name="frames">Frame count.</param>
    /// <param name="format">Container id.</param>
    /// <param name="size">Output size.</param>
    /// <param name="layerIds">Layers, or null for "every enabled layer, no HUD".</param>
    /// <param name="fps">Frame rate.</param>
    public static ExportRequest Request(int frames, string? format = null, SKSizeI? size = null,
        IReadOnlySet<string>? layerIds = null, int fps = 60) =>
        new(0, Math.Max(0, frames - 1), fps, size ?? new SKSizeI(64, 64), 1.0,
            format ?? ExportFormats.WebM,
            layerIds ?? new HashSet<string>(StringComparer.Ordinal),
            new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));

    /// <summary>A HUD snapshot with a bomb ticking and <paramref name="rows" /> kills.</summary>
    /// <param name="rows">How many kill rows to synthesise.</param>
    /// <param name="bombTicking">Whether the C4 owns the countdown.</param>
    /// <param name="roster">Player cards, or null for none — <c>hud.roster</c>'s "draws nothing" case.</param>
    /// <param name="defusing">Whether a defuse is racing the detonation.</param>
    public static HudSnapshot Hud(int rows, bool bombTicking = false,
        IReadOnlyList<HudPlayerRow>? roster = null, bool defusing = false)
    {
        List<KillFeedRow> kills = new(rows);
        for (int i = 0; i < rows; i++)
        {
            kills.Add(new KillFeedRow(1000 + i, $"killer{i}", i % 2 == 0 ? $"assist{i}" : null,
                $"victim{i}", "ak47", i % 2 == 0, i % 3 == 0, i % 4 == 0, i % 5 == 0, i % 6 == 0,
                i % 7 == 0, i % 2 == 0));
        }

        return new HudSnapshot(1000, "13", 7, 5, 34.5, bombTicking, defusing,
            defusing ? 3.4 : double.NaN, kills, roster ?? []);
    }

    /// <summary>A full five-a-side roster, with one dead player per side.</summary>
    /// <param name="perSide">Cards on each of T and CT.</param>
    public static IReadOnlyList<HudPlayerRow> Roster(int perSide = 5)
    {
        List<HudPlayerRow> rows = new(perSide * 2);
        for (int team = 2; team <= 3; team++)
        {
            for (int i = 0; i < perSide; i++)
            {
                bool alive = i != 0;
                rows.Add(new HudPlayerRow((team - 2) * 5 + i, team, $"P{i}", alive,
                    alive ? 100 - i * 17 : 0, alive ? 100 - i * 20 : 0, i % 2 == 0,
                    team == 3 && i == 1, alive ? "ak47" : "—", 800 * (i + 1), i, 5 - i, i % 3));
            }
        }

        return rows;
    }
}
