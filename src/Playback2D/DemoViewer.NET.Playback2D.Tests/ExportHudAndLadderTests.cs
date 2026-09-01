#region

using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SixLabors.ImageSharp;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The kill-feed window. Ported verbatim from the view-model's own cases, because the point is
///     that there is now exactly ONE of these and both feeds go through it.
/// </summary>
public class KillFeedTimelineTests
{
    private static readonly int[] _expectedOrder = [1010, 1030, 1050];

    [Test]
    public async Task AKillOnThePlayhead_IsShown_AndOneAheadOfItIsNot()
    {
        List<KillFeedRow> all = [Kill(1000), Kill(1001)];
        List<KillFeedRow> window = [];

        KillFeedTimeline.Window(all, 1000, 64, window);

        // The inclusive upper bound is load-bearing: a kill AHEAD of the playhead appearing while paused
        // or seeking is the bug this rule exists to prevent.
        await Assert.That(window.Count).IsEqualTo(1);
        await Assert.That(window[0].Tick).IsEqualTo(1000);
    }

    [Test]
    public async Task AKillExactlyAtTheWindowsLowerEdge_HasExpired()
    {
        const int tickRate = 64;
        int nowTick = 10_000;
        int lowTick = nowTick - KillFeedTimeline.DefaultWindowSeconds * tickRate;

        List<KillFeedRow> all = [Kill(lowTick), Kill(lowTick + 1)];
        List<KillFeedRow> window = [];

        KillFeedTimeline.Window(all, nowTick, tickRate, window);

        await Assert.That(window.Count).IsEqualTo(1);
        await Assert.That(window[0].Tick).IsEqualTo(lowTick + 1);
    }

    [Test]
    public async Task PastTheRowCap_TheNewestAreKept_InTickOrder()
    {
        List<KillFeedRow> all = [];
        for (int i = 0; i < 10; i++)
        {
            all.Add(Kill(1000 + i * 10));
        }

        List<KillFeedRow> window = [];
        KillFeedTimeline.Window(all, 1100, 64, window);

        await Assert.That(window.Count).IsEqualTo(KillFeedTimeline.DefaultMaxRows);
        await Assert.That(window[0].Tick).IsEqualTo(1040);
        await Assert.That(window[^1].Tick).IsEqualTo(1090);
    }

    [Test]
    public async Task TheSourceOrder_DoesNotMatter()
    {
        List<KillFeedRow> shuffled = [Kill(1050), Kill(1010), Kill(1030)];
        List<KillFeedRow> window = [];

        // AllGameEvents order is not guaranteed to be by tick, so the window sorts what it keeps.
        KillFeedTimeline.Window(shuffled, 1060, 64, window);

        await Assert.That(window.Select(r => r.Tick)).IsEquivalentTo(_expectedOrder);
    }

    /// <summary>
    ///     Two identical windows, and the SECOND is the one asserted on: the form
    ///     <see cref="BudgetTests.FullScene_SteadyState_AllocatesNothing" /> uses and documents.
    ///     <para>
    ///         This case used to measure ONE window after a warmup loop, and it failed once and passed
    ///         on retry. That is the JIT-tiering hazard <c>BudgetTests</c> describes: a single small
    ///         allocation appears at a varying iteration while the runtime re-tiers the loop body,
    ///         whatever the body does, and never recurs. Charging it to the budget either makes the gate
    ///         flaky or forces the budget above zero.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task ItReusesTheDestination_AndAllocatesNothingOnceWarm()
    {
        List<KillFeedRow> all = [];
        for (int i = 0; i < 200; i++)
        {
            all.Add(Kill(1000 + i * 7));
        }

        // Capacity 8 up front: DefaultMaxRows fits, so a growing List would be an allocation of its own
        // and would confuse "the window allocates" with "the destination is still growing".
        List<KillFeedRow> window = new(8);
        for (int i = 0; i < 64; i++)
        {
            KillFeedTimeline.Window(all, 1500 + i, 64, window);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long warm = MeasureWindow(all, window);
        long steady = MeasureWindow(all, window);

        Console.WriteLine($"[alloc] killfeed window: warm {warm} B, steady {steady} B "
                          + $"({steady / 512.0:F2} B/call)");
        await Assert.That(steady).IsEqualTo(0L);
    }

    private static long MeasureWindow(List<KillFeedRow> all, List<KillFeedRow> window)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            KillFeedTimeline.Window(all, 1500 + i, 64, window);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static KillFeedRow Kill(int tick) =>
        new(tick, "attacker", null, "victim", "ak47", false, false, false, false, false, false, false);
}

/// <summary>The tick → HUD state function: pure, cached, and reused by both HUD layers in a frame.</summary>
public class TimelineHudDataSourceTests
{
    [Test]
    public async Task TheSameTick_ReWindowsOnce_ButStillReReadsItsSources()
    {
        int clockCalls = 0;
        TimelineHudDataSource source = new([], 64, _ =>
        {
            clockCalls++;
            return ClockReading.Unknown;
        });

        source.At(1000);
        source.At(1000);
        source.At(1000);
        source.At(1001);

        // Three HUD layers ask for the same frame. Doing the window three times for one answer is three
        // times the work and, on a three-level map, three times again, so the WINDOW is cached by tick,
        // which it can be, because it is a pure function of tick.
        await Assert.That(source.WindowingsForTest).IsEqualTo(2);

        // The readers are not, and this assertion is the correction: they answer for whatever FRAME the
        // source built most recently, not for the tick. CS2 emits several demo frames per tick, so two
        // consecutive output frames can share one, and a snapshot cached by tick alone handed the second
        // of them the first one's scoreboard and the first one's roster. Re-asking costs two delegate
        // calls over state the frame source has already computed.
        await Assert.That(clockCalls).IsEqualTo(4);
    }

    [Test]
    public async Task ItProjectsSceneGameInfo_OntoTheClockReading()
    {
        SceneGameInfo info = new("Live", "Planted", 13, 12, 34.5, "0:34",
            true, false, "kit", double.NaN, "—", 7, 5);

        ClockReading reading = ClockReading.From(info);

        await Assert.That(reading.Round).IsEqualTo("13");
        await Assert.That(reading.TScore).IsEqualTo(7);
        await Assert.That(reading.CtScore).IsEqualTo(5);
        await Assert.That(reading.BombTicking).IsTrue();
        await Assert.That(reading.CountdownSeconds).IsEqualTo(34.5);
    }

    [Test]
    public async Task AnUnknownRound_RendersAPlaceholder_RatherThanZero()
    {
        ClockReading reading = ClockReading.From(SceneGameInfo.Empty);
        await Assert.That(reading.Round).IsEqualTo("—");
    }
}

/// <summary>
///     The two export HUD layers. They are opt-in, they draw once per host rather than once per band,
///     and their text is what the snapshot test compares against the XAML feed.
/// </summary>
public class HudLayerTests
{
    [Test]
    public async Task TheClockAndTheKillFeed_DrawSomething()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(4));
        int painted = RenderAndCountInk(
            [new ClockLayer(data), new KillFeedLayer(data)], out int background);

        Console.WriteLine($"[hud] non-background pixels={painted} of {background}");
        await Assert.That(painted).IsGreaterThan(500);
    }

    [Test]
    public async Task AnEmptyKillFeed_DrawsNothing()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(0));
        int painted = RenderAndCountInk([new KillFeedLayer(data)], out _);

        // A panel with no rows in it is chrome the video did not ask for.
        await Assert.That(painted).IsEqualTo(0);
    }

    [Test]
    public async Task ABombCountdown_DrawsDifferentlyFromARoundClock()
    {
        StubHudDataSource round = new(ExportFixtures.Hud(0));
        StubHudDataSource bomb = new(ExportFixtures.Hud(0, true));

        // The C4 countdown is drawn in the bomb colour, because "0:34 until the round ends" and "0:34
        // until the site goes up" are not the same number.
        await Assert.That(RenderAndCountInk([new ClockLayer(round)], out _))
            .IsNotEqualTo(0);
        await Assert.That(Hash([new ClockLayer(round)]))
            .IsNotEqualTo(Hash([new ClockLayer(bomb)]));
    }

    [Test]
    public async Task TheHud_DrawsOnlyInTheTopBand()
    {
        SceneRenderContext top = Context(new SKRect(0, 0, 400, 200));
        SceneRenderContext lower = Context(new SKRect(0, 200, 400, 400));

        // The compositor renders every layer once per band. A scoreboard repeated on each floor of a
        // two-level Nuke export would be wrong, so the layer draws in the one band whose top edge is the
        // host's, which is also the single-pane case, whose snapshot rectangle is zero.
        await Assert.That(ClockLayer.IsTopBand(top)).IsTrue();
        await Assert.That(ClockLayer.IsTopBand(lower)).IsFalse();
        await Assert.That(ClockLayer.IsTopBand(Context(default))).IsTrue();
    }

    [Test]
    public async Task ARowsText_CarriesEveryModifierGlyph()
    {
        KillFeedRow row = new(1000, "neo", "trinity", "smith", "awp",
            true, true, true, true,
            true, true, true);

        string text = KillFeedLayer.Format(row);
        Console.WriteLine($"[hud] row = {text}");

        await Assert.That(text).Contains("neo");
        await Assert.That(text).Contains("trinity");
        await Assert.That(text).Contains("smith");
        await Assert.That(text).Contains("awp");
        await Assert.That(text).Contains("HS");
        await Assert.That(text).Contains("WB");
        await Assert.That(text).Contains("NS");
    }

    [Test]
    public async Task ARowWithoutAnAssist_HasNoAssistChip()
    {
        KillFeedRow row = new(1000, "neo", null, "smith", "ak47",
            false, false, false, false, false, false, false);

        await Assert.That(KillFeedLayer.Format(row)).DoesNotContain("+");
    }

    private static SceneRenderContext Context(SKRect paneRect) =>
        new(Scene2DFrame.Empty, default, ViewportTransform.Fit(400, 200, -100, -100, 100, 100),
            new SKRect(0, 0, paneRect.Width, paneRect.Height), 0, 0, 0,
            RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(default, 0,
                new MapLevel
                {
                    Id = default,
                    Name = "l",
                    ZMin = 0,
                    ZMax = 100
                },
                ViewportTransform.Fit(400, 200, -100, -100, 100, 100), paneRect, 0)
        };

    private static int RenderAndCountInk(ISceneLayer[] layers, out int totalPixels)
    {
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(new SKSizeI(400, 200));
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
        foreach (ISceneLayer layer in layers)
        {
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(surface.Canvas, Context(default));
            layer.Dispose();
        }

        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);

        SKColor background = ScenePalette.Dark.Background;
        int painted = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    painted++;
                }
            }
        }

        totalPixels = bitmap.Width * bitmap.Height;
        return painted;
    }

    private static string Hash(ISceneLayer[] layers)
    {
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(new SKSizeI(400, 200));
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
        foreach (ISceneLayer layer in layers)
        {
            layer.Advance(in time, Scene2DFrame.Empty);
            layer.Render(surface.Canvas, Context(default));
            layer.Dispose();
        }

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToHexString(SHA256.HashData(data.ToArray()));
    }
}

/// <summary>
///     The ffmpeg download rung. Every case here is a way the download can go wrong, and the contract is
///     the same each time: nothing is installed, nothing is left on disk, and the caller falls through to
///     the GIF floor instead of crashing. No network. An injected handler serves a fixture archive.
/// </summary>
public class FfmpegAcquisitionTests
{
    [Test]
    public async Task AnOfferExists_OnlyWhereABuildIsPinned()
    {
        FfmpegDownloadOffer? offer = FfmpegAcquisition.Offer(Path.GetTempPath());

        if (OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture
            == Architecture.X64)
        {
            await Assert.That(offer).IsNotNull();
            await Assert.That(offer!.LicenseName).IsEqualTo("LGPL-2.1");

            // Never a "-latest-" asset: BtbN re-points those, and a pin that moves is not a pin.
            await Assert.That(offer.Url).DoesNotContain("-latest-");
            await Assert.That(offer.Url).Contains(FfmpegAcquisition.ReleaseTag);
            await Assert.That(offer.ArchiveSha256.Length).IsEqualTo(64);
        }
        else
        {
            // macOS and Linux get install instructions and the GIF floor, not a download.
            await Assert.That(offer).IsNull();
        }
    }

    [Test]
    public async Task AChecksumMismatch_AbortsAndLeavesNothingBehind()
    {
        using TempDirectory directory = new();
        byte[] archive = BuildArchive();
        FfmpegDownloadOffer offer = Offer(directory.Path, "0".PadLeft(64, '0'), archive.Length);

        using HttpClient client = new(new StubHandler(archive));

        FfmpegAcquisitionException? failure = null;
        try
        {
            await FfmpegAcquisition.AcquireAsync(offer, (_, _, _) => Task.FromResult(true), null, client,
                CancellationToken.None);
        }
        catch (FfmpegAcquisitionException ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(Directory.GetFiles(directory.Path)).IsEmpty();
    }

    [Test]
    public async Task DeclinedConsent_InstallsNothing()
    {
        using TempDirectory directory = new();
        byte[] archive = BuildArchive();
        FfmpegDownloadOffer offer = Offer(directory.Path, Sha256(archive), archive.Length);

        using HttpClient client = new(new StubHandler(archive));

        string? shownLicence = null;
        FfmpegLocation located = await FfmpegAcquisition.AcquireAsync(offer,
            (_, licence, _) =>
            {
                shownLicence = licence;
                return Task.FromResult(false);
            }, null, client, CancellationToken.None);

        await Assert.That(located.Found).IsFalse();
        await Assert.That(Directory.GetFiles(directory.Path)).IsEmpty();

        // The licence the user reads comes out of the bytes that were just verified, not a copy
        // vendored in this repository that could drift from the binary it covers.
        await Assert.That(shownLicence).IsNotNull();
        await Assert.That(shownLicence!).Contains("GNU LESSER GENERAL PUBLIC LICENSE");
    }

    [Test]
    public async Task OnSuccess_BothBinariesLand_AndTheLocatorFindsThem()
    {
        using TempDirectory directory = new();
        byte[] archive = BuildArchive();
        FfmpegDownloadOffer offer = Offer(directory.Path, Sha256(archive), archive.Length);

        using HttpClient client = new(new StubHandler(archive));

        List<double> progress = [];
        FfmpegLocation located = await FfmpegAcquisition.AcquireAsync(offer,
            (_, _, _) => Task.FromResult(true), new Progress<double>(progress.Add), client,
            CancellationToken.None);

        await Assert.That(located.Found).IsTrue();
        await Assert.That(located.Origin).IsEqualTo(FfmpegOrigin.Managed);
        await Assert.That(File.Exists(Path.Combine(directory.Path, FfmpegLocator.ExecutableName))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(directory.Path, FfmpegLocator.ProbeExecutableName)))
            .IsTrue();

        // Nothing partial survives: a leftover *.part that Locate() then reported as an install would
        // be worse than no install at all.
        await Assert.That(Directory.GetFiles(directory.Path, "*.part")).IsEmpty();
    }

    [Test]
    public async Task AnHttpFailure_DegradesWithAMessage_RatherThanCrashing()
    {
        using TempDirectory directory = new();
        FfmpegDownloadOffer offer = Offer(directory.Path, "0".PadLeft(64, '0'), 0);

        using HttpClient client = new(new StubHandler([], HttpStatusCode.NotFound));

        FfmpegAcquisitionException? failure = null;
        try
        {
            await FfmpegAcquisition.AcquireAsync(offer, (_, _, _) => Task.FromResult(true), null, client,
                CancellationToken.None);
        }
        catch (FfmpegAcquisitionException ex)
        {
            failure = ex;
        }

        // A 404 on the pin is a "recheck the pin each release" event, and to the user it must read as
        // "install ffmpeg or export GIF", not as a stack trace.
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("GIF");
    }

    private static FfmpegDownloadOffer Offer(string directory, string sha, long bytes) =>
        new("https://example.invalid/ffmpeg.zip", sha, "autobuild-test", "https://example.invalid",
            "LGPL-2.1", bytes, directory);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // The shape BtbN publishes: one top-level directory carrying bin/, LICENSE.txt and README.txt.
    private static byte[] BuildArchive()
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, true))
        {
            Write(archive, "ffmpeg-n9.0.1-win64-lgpl/LICENSE.txt",
                "GNU LESSER GENERAL PUBLIC LICENSE\nVersion 2.1, February 1999\n");
            Write(archive, $"ffmpeg-n9.0.1-win64-lgpl/bin/{FfmpegLocator.ExecutableName}", "not-really-ffmpeg");
            Write(archive, $"ffmpeg-n9.0.1-win64-lgpl/bin/{FfmpegLocator.ProbeExecutableName}", "not-really-ffprobe");
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using StreamWriter writer = new(archive.CreateEntry(path).Open());
        writer.Write(content);
    }

    private sealed class StubHandler(byte[] payload, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(payload)
            });
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dv-ffmpeg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // A test that cannot clean its temp directory has still made its point.
            }
        }
    }
}

/// <summary>
///     The seam proof: the whole export path drives from Pipeline alone. If this ever needs an App
///     type, <c>dv2d export</c> cannot exist and the CLI front end is a fiction.
/// </summary>
public class ExportSeamHeadlessTests
{
    [Test]
    public async Task AFixtureExportsToAGif_WithNoAvaloniaAssemblyLoaded()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-seam-{Guid.NewGuid():N}.gif");

        try
        {
            using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
            using CpuSurfaceProvider surfaces = new();

            await new SceneExportSession(compositor).RunAsync(
                ExportFixtures.Request(8, ExportFormats.Gif, new SKSizeI(64, 48), fps: 20),
                ExportFixtures.Source(8), new ManagedGifSink(path, 20), surfaces, null,
                CancellationToken.None);

            await Assert.That(File.Exists(path)).IsTrue();
            using Image decoded = await Image.LoadAsync(path);
            await Assert.That(decoded.Frames.Count).IsEqualTo(8);
        }
        finally
        {
            File.Delete(path);
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = assembly.GetName().Name ?? string.Empty;
            await Assert.That(name.StartsWith("Avalonia", StringComparison.Ordinal)).IsFalse();
        }
    }
}
