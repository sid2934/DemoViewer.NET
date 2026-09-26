#region

using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Export.Pack;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.ViewModels.Review;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pack Export: the plan (sections as title cards, a clip that cannot render left out, GIF as one file
///     per clip held to the frame cap), the stitch (title cards and clips from three demos in queue order
///     through one sink, a clip's own failure against the encoder's, cancellation), the title card, and
///     the Review tab's Export pack row. The render of real demos is the integration phase's.
/// </summary>
public class PackExportTests
{
    private const string Pack = "/packs/scrims.mp4";

    private static ReviewEntry Clip(string path, int from, int to, string note = "") =>
        ReviewEntry.Clip(path, from, to, note, ReviewSources.Manual, 64);

    // The queue the stitch tests share: a clip above every card, then two sections over three demos, and
    // a section whose one clip's demo is gone.
    private static List<ReviewEntry> Queue() =>
    [
        Clip("/d/a.dem", 0, 64, "opener"),
        ReviewEntry.Section("A executes", "tuesday scrim"),
        Clip("/d/b.dem", 0, 128, "smokes"),
        Clip("/d/c.dem", 64, 128, "entry"),
        ReviewEntry.Section("Nothing here"),
        Clip("/d/missing.dem", 0, 64, "gone"),
        ReviewEntry.Section("Retakes"),
        Clip("/d/a.dem", 640, 704, "retake")
    ];

    private static bool Exists(string path) => !path.Contains("missing", StringComparison.Ordinal);

    private static PackSettings Settings(string path = Pack, string format = ExportFormats.Mp4) =>
        PackSettings.For(path, format) with { Side = 32, TitleSeconds = 0.1 };

    private static DemoFrame[] Frames(int from, int to)
    {
        DemoFrame[] frames = new DemoFrame[to - from + 1];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = new DemoFrame
            {
                CommandKind = EDemoCommands.DemPacket,
                FrameNumber = i,
                ServerTick = from + i,
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            };
        }

        return frames;
    }

    // ── The plan ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Plan_SectionsBecomeTitleCards_InQueueOrder_AndAnEmptiedSectionGetsNone()
    {
        PackPlan plan = PackPlanner.Plan(Queue(), PackSettings.For(Pack, ExportFormats.Mp4), Exists);

        List<string> shape =
        [
            .. plan.Segments.Select(s => s switch
            {
                PackTitleCard card => "card " + card.Title,
                PackClip clip => $"clip {clip.Number} {clip.Entry.Note} ({clip.Section ?? "-"})",
                _ => "?"
            })
        ];
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(" | ", shape)).IsEqualTo(
                "clip 1 opener (-) | card A executes | clip 2 smokes (A executes) | clip 3 entry (A executes) | "
                + "card Retakes | clip 4 retake (Retakes)");
            await Assert.That(plan.Skipped.Count).IsEqualTo(1);
            await Assert.That(plan.Skipped[0].Reason).IsEqualTo("demo not found");
            await Assert.That(plan.DemoCount).IsEqualTo(3);
            await Assert.That(((PackTitleCard)plan.Segments[1]).Subtitle).IsEqualTo("tuesday scrim");
            await Assert.That(((PackTitleCard)plan.Segments[1]).Frames).IsEqualTo(75).Because("2.5 s at 30 fps");
            await Assert.That(plan.Clips.All(c => c.MaxFrames is null && c.OutputPath is null)).IsTrue()
                .Because("a video pack is one file with no frame cap");
        }
    }

    [Test]
    public async Task Plan_EstimatesFrames_FromTheClipsOwnTickRate()
    {
        ReviewEntry clip = ReviewEntry.Clip("/d/a.dem", 0, 1280, "", ReviewSources.Manual, 128);

        await Assert.That(PackPlanner.EstimateFrames(clip, 30)).IsEqualTo(301).Because("ten seconds at 30 fps, both ends");
        await Assert.That(PackPlanner.EstimateFrames(clip with { TickRate = 0 }, 30)).IsEqualTo(601)
            .Because("an unknown rate is read as 64");
    }

    [Test]
    public async Task Plan_Gif_IsOneFilePerClip_WithNoTitleCards_EachHeldToTheFrameCap()
    {
        List<ReviewEntry> queue =
        [
            ReviewEntry.Section("B site"),
            Clip("/d/a.dem", 0, 640, "smoke into CT"),
            Clip("/d/b.dem", 0, 64 * 200, "a long one")
        ];

        PackPlan plan = PackPlanner.Plan(queue, PackSettings.For("/packs/scrims.gif", ExportFormats.Gif), Exists);

        string folder = PackPlanner.GifFolder("/packs/scrims.gif");
        using (Assert.Multiple())
        {
            await Assert.That(plan.Segments.OfType<PackTitleCard>().Count()).IsEqualTo(0);
            await Assert.That(Path.GetFileName(folder)).IsEqualTo("scrims").Because("the folder is the pack path without its extension");
            await Assert.That(plan.Clips.Count).IsEqualTo(2);
            await Assert.That(plan.Clips[0].OutputPath).IsEqualTo(Path.Combine(folder, "01 B site - smoke into CT.gif"));
            await Assert.That(plan.Clips[1].OutputPath).IsEqualTo(Path.Combine(folder, "02 B site - a long one.gif"));
            await Assert.That(plan.Clips.All(c => c.MaxFrames == SceneExportSession.GifMaxFrames)).IsTrue();
            await Assert.That(plan.Clips[0].Trimmed).IsFalse();
            await Assert.That(plan.Clips[1].Trimmed).IsTrue().Because("200 s at 20 fps is 4001 frames");
            await Assert.That(plan.EstimatedFrames).IsEqualTo(201 + SceneExportSession.GifMaxFrames)
                .Because("the pack's length counts each clip at its cap");
        }
    }

    [Test]
    public async Task Validate_AcceptsEachFormatsDefaults_AndRefusesWhatTheEncoderCannotTake()
    {
        using (Assert.Multiple())
        {
            foreach (string format in ExportFormats.All)
            {
                await Assert.That(PackPlanner.Validate(PackSettings.For(Pack, format))).IsNull();
            }

            await Assert.That(PackPlanner.Validate(PackSettings.For(Pack, ExportFormats.Mp4) with { Side = 1081 }))
                .IsNotNull().Because("yuv420p needs even sides");
            await Assert.That(PackPlanner.Validate(PackSettings.For(Pack, ExportFormats.Gif) with { Fps = 30 }))
                .IsNotNull().Because("a GIF delay is whole centiseconds");
            await Assert.That(PackPlanner.Validate(PackSettings.For(Pack, ExportFormats.Gif) with { Side = 4000 }))
                .IsNotNull();
            await Assert.That(PackPlanner.Validate(PackSettings.For(" ", ExportFormats.Mp4))).IsNotNull();
        }
    }

    [Test]
    public async Task BuildClipRequest_ResolvesTheQueueRange_ToDemoFrames_WithTheInkLayerNamed()
    {
        PackPlan plan = PackPlanner.Plan([Clip("/d/a.dem", 1064, 1128)], PackSettings.For(Pack, ExportFormats.Mp4), Exists);
        PackClip clip = plan.Clips[0];

        Scene2DExportRequest? request = PackPlanner.BuildClipRequest(clip, plan.Settings, Frames(1000, 2000), 64);

        using (Assert.Multiple())
        {
            await Assert.That(request).IsNotNull();
            await Assert.That(request!.DemoStartFrame).IsEqualTo(64);
            await Assert.That(request.DemoEndFrame).IsEqualTo(128);
            await Assert.That(request.OutputPath).IsEqualTo(Pack);
            await Assert.That(request.Core.Size.Width).IsEqualTo(1080);
            await Assert.That(request.Core.Fps).IsEqualTo(30);
            await Assert.That(request.Core.LayerIds.Contains(SceneLayerIds.Annotations)).IsTrue()
                .Because("ink is opt-in, so a pack names it or never burns it in");
            await Assert.That(PackPlanner.BuildClipRequest(clip, plan.Settings, Frames(5000, 6000), 64)).IsNull();
        }

        SceneExportSession.Validate(request.Core);
    }

    [Test]
    public async Task BuildClipRequest_HoldsAGifClip_ToItsCap()
    {
        PackPlan plan = PackPlanner.Plan([Clip("/d/a.dem", 0, 64 * 200)], PackSettings.For("/p/x.gif", ExportFormats.Gif), Exists);

        Scene2DExportRequest? request = PackPlanner.BuildClipRequest(plan.Clips[0], plan.Settings, Frames(0, 64 * 200), 64);

        await Assert.That(request!.Core.FrameCount).IsEqualTo(SceneExportSession.GifMaxFrames);
        SceneExportSession.Validate(request.Core);
    }

    // ── The stitch ────────────────────────────────────────────────────────────

    [Test]
    public async Task Export_Video_StitchesCardsAndClipsFromThreeDemos_IntoOneSink_InQueueOrder()
    {
        FakeEncoder encoder = new();
        FakeClips clips = new(framesPerClip: 3);
        PackPlan plan = PackPlanner.Plan(Queue(), Settings(), Exists);
        List<PackProgress> progress = [];

        PackResult result = await new PackExporter(clips, encoder).ExportAsync(plan, new InlineProgress(progress.Add), CancellationToken.None);

        RecordingSink sink = encoder.Sinks.Single();
        int card = ((PackTitleCard)plan.Segments[1]).Frames;
        List<byte> expected =
        [
            1, 1, 1,
            .. Enumerable.Repeat(RecordingSink.CardMark, card),
            2, 2, 2,
            3, 3, 3,
            .. Enumerable.Repeat(RecordingSink.CardMark, card),
            4, 4, 4
        ];
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", encoder.Opened)).IsEqualTo(Pack);
            await Assert.That(string.Join(",", sink.Marks)).IsEqualTo(string.Join(",", expected));
            await Assert.That(sink.DisposeCount).IsEqualTo(1).Because("the clips end their segment, the pack ends the file");
            await Assert.That(clips.Demos.Distinct().Count()).IsEqualTo(3);
            await Assert.That(string.Join(",", result.Outputs)).IsEqualTo(Pack);
            await Assert.That(result.ClipsRendered).IsEqualTo(4);
            await Assert.That(result.Failed.Count).IsEqualTo(0);
            await Assert.That(string.Join(",", progress.Select(p => p.Segment))).IsEqualTo("1,2,3,4,5,6");
            await Assert.That(progress[1].Label).IsEqualTo("A executes");
            await Assert.That(progress[2].Label).IsEqualTo("clip 2: smokes");
        }
    }

    [Test]
    public async Task Export_Video_LeavesOutAClipThatFailsOnItsOwn_AndGoesOn()
    {
        FakeEncoder encoder = new();
        FakeClips clips = new(framesPerClip: 2) { FailOn = 2 };
        PackPlan plan = PackPlanner.Plan(Queue(), Settings(), Exists);

        PackResult result = await new PackExporter(clips, encoder).ExportAsync(plan, null, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(result.ClipsRendered).IsEqualTo(3);
            await Assert.That(result.Failed.Count).IsEqualTo(1);
            await Assert.That(result.Failed[0].Reason).Contains("clip 2: smokes");
            await Assert.That(encoder.Sinks.Single().Marks.Contains((byte)2)).IsFalse();
            await Assert.That(encoder.Sinks.Single().Marks.Contains((byte)4)).IsTrue();
        }
    }

    [Test]
    public async Task Export_Video_EndsThePack_WhenTheEncoderFails_AndRemovesTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-pack-{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(path, "half a video");
        FakeEncoder encoder = new() { FailAfter = 5 };
        PackPlan plan = PackPlanner.Plan(Queue(), Settings(path), Exists);

        Exception? thrown = null;
        try
        {
            await new PackExporter(new FakeClips(framesPerClip: 4), encoder).ExportAsync(plan, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        using (Assert.Multiple())
        {
            await Assert.That(thrown).IsTypeOf<IOException>();
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(encoder.Sinks.Single().DisposeCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Export_Video_Cancelled_Throws_AndRemovesTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-pack-{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(path, "half a video");
        using CancellationTokenSource cts = new();
        FakeClips clips = new(framesPerClip: 2) { OnRender = n => { if (n == 2) { cts.Cancel(); } } };
        PackPlan plan = PackPlanner.Plan(Queue(), Settings(path), Exists);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new PackExporter(clips, new FakeEncoder()).ExportAsync(plan, null, cts.Token));
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Export_Refuses_AQueueWithNothingToRender_BeforeOpeningAFile()
    {
        FakeEncoder encoder = new();
        PackPlan plan = PackPlanner.Plan([Clip("/d/missing.dem", 0, 64)], Settings(), Exists);

        await Assert.ThrowsAsync<ExportRefusedException>(
            async () => await new PackExporter(new FakeClips(1), encoder).ExportAsync(plan, null, CancellationToken.None));
        await Assert.That(encoder.Opened.Count).IsEqualTo(0);
        await Assert.That(encoder.Prepared).IsFalse();
    }

    [Test]
    public async Task Export_Gif_WritesEachClipToItsOwnFile_WithNoTitleCards()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-pack-{Guid.NewGuid():N}");
        FakeEncoder encoder = new();
        PackPlan plan = PackPlanner.Plan(Queue(), Settings(Path.Combine(root, "scrims.gif"), ExportFormats.Gif), Exists);

        try
        {
            PackResult result = await new PackExporter(new FakeClips(2), encoder).ExportAsync(plan, null, CancellationToken.None);

            using (Assert.Multiple())
            {
                await Assert.That(result.Outputs.Count).IsEqualTo(4);
                await Assert.That(string.Join("|", encoder.Opened)).IsEqualTo(string.Join("|", plan.Clips.Select(c => c.OutputPath)));
                await Assert.That(encoder.Sinks.All(s => s.DisposeCount == 1)).IsTrue();
                await Assert.That(encoder.Sinks.SelectMany(s => s.Marks).Contains(RecordingSink.CardMark)).IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(root, "scrims"))).IsTrue();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task SegmentSink_EndsTheSegment_NotThePacksSink_AndSaysWhoseFailureItWas()
    {
        RecordingSink inner = new() { FailAfter = 1 };
        PackSegmentSink segment = new(inner);

        await segment.WriteAsync(new byte[4], 1, 1, CancellationToken.None);
        await segment.DisposeAsync();

        PackSegmentSink second = new(inner);
        await Assert.ThrowsAsync<IOException>(async () => await second.WriteAsync(new byte[4], 1, 1, CancellationToken.None));
        using (Assert.Multiple())
        {
            await Assert.That(inner.DisposeCount).IsEqualTo(0);
            await Assert.That(segment.FramesWritten).IsEqualTo(1);
            await Assert.That(segment.Ended).IsTrue();
            await Assert.That(segment.InnerFaulted).IsFalse();
            await Assert.That(second.InnerFaulted).IsTrue();
        }
    }

    [Test]
    public async Task TitleCard_IsOneOpaqueFrame_OnTheSceneBackground_WithTheTitleDrawn()
    {
        const int side = 256;
        byte[] rgba = PackTitleCardRenderer.Render("A executes", "tuesday scrim", side, ScenePalette.Dark);

        SkiaSharp.SKColor background = ScenePalette.Dark.Background;
        bool drawn = false;
        for (int p = 0; p < side * side; p++)
        {
            drawn |= rgba[p * 4] != background.Red;
        }

        using (Assert.Multiple())
        {
            await Assert.That(rgba.Length).IsEqualTo(side * side * 4);
            await Assert.That(rgba[0]).IsEqualTo(background.Red);
            await Assert.That(rgba[1]).IsEqualTo(background.Green);
            await Assert.That(rgba[2]).IsEqualTo(background.Blue);
            await Assert.That(Enumerable.Range(0, side * side).All(p => rgba[p * 4 + 3] == 255)).IsTrue();
            await Assert.That(drawn).IsTrue();
        }
    }

    // ── The Review tab ────────────────────────────────────────────────────────

    [Test]
    public async Task ReviewTab_WithoutAnExporter_HasNoPackRow()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 0, 64)]);
        using ReviewQueueTabViewModel vm = new(queue, isBrowser: true, fileExists: Exists);

        await Assert.That(vm.CanPack).IsFalse();
        await Assert.That(vm.ExportPackCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task ReviewTab_ExportPack_HandsTheQueuesPlan_AndReportsWhatWasLeftOut()
    {
        ReviewQueue queue = new(null);
        queue.Add(Queue());
        PackPlan? handed = null;
        using ReviewQueueTabViewModel vm = new(queue, isBrowser: false, fileExists: Exists, packDirectory: "/packs",
            exportPack: (plan, _, _) =>
            {
                handed = plan;
                return Task.FromResult(new PackResult([plan.Settings.OutputPath], plan.Clips.Count, []));
            });

        await Assert.That(vm.PackSummary).StartsWith("4 clips from 3 demos · 2 title cards · ");
        await Assert.That(vm.PackSummary).Contains("1 left out (demo not found)");

        vm.SelectedPackFormat = ExportFormats.WebM;
        await vm.ExportPackCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.PackOutputPath).EndsWith(".webm").Because("the extension follows the format");
            await Assert.That(Path.GetDirectoryName(vm.PackOutputPath)).IsEqualTo(Path.GetDirectoryName(Path.Combine("/packs", "x")));
            await Assert.That(handed).IsNotNull();
            await Assert.That(handed!.Settings.FormatId).IsEqualTo(ExportFormats.WebM);
            await Assert.That(handed.Clips.Count).IsEqualTo(4);
            await Assert.That(vm.PackStatus).StartsWith("4 clips written to ");
            await Assert.That(vm.PackStatus).EndsWith("left out: demo not found");
            await Assert.That(vm.IsPackRunning).IsFalse();
        }
    }

    [Test]
    public async Task ReviewTab_ExportPack_SaysWhyAFailedPackFailed()
    {
        ReviewQueue queue = new(null);
        queue.Add([Clip("/d/a.dem", 0, 64)]);
        using ReviewQueueTabViewModel vm = new(queue, isBrowser: false, fileExists: Exists,
            exportPack: (_, _, _) => throw new ExportRefusedException(SceneExportRunner.NoFfmpegRefusal));

        await vm.ExportPackCommand.ExecuteAsync(null);

        await Assert.That(vm.PackStatus).IsEqualTo("pack export failed: " + SceneExportRunner.NoFfmpegRefusal);
        await Assert.That(vm.ExportPackCommand.CanExecute(null)).IsTrue();
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>Progress reported on the calling thread, so a test sees every report in order.</summary>
    private sealed class InlineProgress(Action<PackProgress> report) : IProgress<PackProgress>
    {
        public void Report(PackProgress value) => report(value);
    }

    /// <summary>Keeps the first byte of every frame: a clip's number, or the title card's mark.</summary>
    private sealed class RecordingSink : IFrameSink
    {
        public const byte CardMark = 200;

        public List<byte> Marks { get; } = [];

        public int DisposeCount { get; private set; }

        public int? FailAfter { get; init; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Marks.Count >= FailAfter)
            {
                throw new IOException("the disk is full");
            }

            byte first = rgba.Span[0];
            Marks.Add(first == ScenePalette.Dark.Background.Red ? CardMark : first);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEncoder : IPackEncoder
    {
        public List<string> Opened { get; } = [];

        public List<RecordingSink> Sinks { get; } = [];

        public bool Prepared { get; private set; }

        public int? FailAfter { get; init; }

        public void Prepare(PackSettings settings, CancellationToken ct) => Prepared = true;

        public IFrameSink Open(string outputPath, PackSettings settings)
        {
            Opened.Add(outputPath);
            RecordingSink sink = new() { FailAfter = FailAfter };
            Sinks.Add(sink);
            return sink;
        }
    }

    /// <summary>A session's shape without a demo: frames stamped with the clip's number, then the sink disposed.</summary>
    private sealed class FakeClips(int framesPerClip) : IPackClipRenderer
    {
        public List<string> Demos { get; } = [];

        public int? FailOn { get; init; }

        public Action<int>? OnRender { get; init; }

        public async Task RenderAsync(PackClip clip, PackSettings settings, IFrameSink sink, CancellationToken ct)
        {
            try
            {
                OnRender?.Invoke(clip.Number);
                Demos.Add(clip.Entry.DemoPath);
                if (clip.Number == FailOn)
                {
                    throw new ExportValidationException("the demo would not parse");
                }

                byte[] frame = new byte[settings.Side * settings.Side * 4];
                frame[0] = (byte)clip.Number;
                for (int i = 0; i < framesPerClip; i++)
                {
                    await sink.WriteAsync(frame, settings.Side, settings.Side, ct);
                }
            }
            finally
            {
                await sink.DisposeAsync();
            }
        }
    }
}
