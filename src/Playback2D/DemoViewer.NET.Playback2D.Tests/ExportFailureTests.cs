#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What an export does when the thing on the other end of the pipe dies.
///     <para>
///         These are the cases the review found unpinned: every existing failure test uses a sink that
///         throws <i>synchronously from <c>WriteAsync</c></i>, which is the one shape a real encoder
///         never has. A real one fails <b>out of band</b> — its process exits while the render loop is
///         still handing it frames — and it fails <b>on disposal</b>, when the container is finalised.
///     </para>
/// </summary>
public class ExportFailureTests
{
    /// <summary>
    ///     An ffmpeg that cannot open its output must fail the export, not hang it.
    ///     <para>
    ///         Risk R2's exact failure mode. ffmpeg exits within milliseconds of a bad output path; the
    ///         bounded channel then fills, and nothing will ever drain it again. Before the fix the
    ///         write loop blocked forever — <c>dv2d export</c> to a non-existent directory never
    ///         returned, and the 30 s disposal timeout never got a chance to run because disposal was
    ///         never reached.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task AnFfmpegThatCannotOpenItsOutput_FailsTheWrites_RatherThanBlockingForever()
    {
        FfmpegLocation located = FfmpegLocator.Locate(null);
        if (!located.Found)
        {
            throw new SkipTestException("no ffmpeg on PATH; the subprocess sink cannot be exercised");
        }

        // An output directory that is gone by the time ffmpeg opens the file, so it fails at
        // output-open and exits immediately.
        //
        // The sink's constructor creates the directory (ExportOutputPath — a missing parent is the one
        // export failure worth pre-empting, since ffmpeg's refusal would otherwise arrive only after the
        // whole range had been rendered). That is a courtesy at CONSTRUCTION, not a guarantee at WRITE,
        // and R2's deadlock is a property of the write loop: whatever kills ffmpeg early, a full channel
        // with no reader must still fault its writer rather than park it forever. Removing the directory
        // between the two reproduces that with ffmpeg's original "No such file or directory".
        string directory = Path.Combine(Path.GetTempPath(), $"dv-nodir-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "out.webm");
        FfmpegFrameSink sink = new(new FfmpegSinkOptions(path, ExportFormats.WebM, 64, 64, 30,
            located.Directory));
        Directory.Delete(directory);

        byte[] frame = new byte[64 * 64 * 4];

        // Far more frames than the channel can hold, so the writer must reach the full-channel wait.
        Task loop = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                await sink.WriteAsync(frame, 64, 64, CancellationToken.None);
            }
        });

        Task finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(30)));
        bool endedOnItsOwn = ReferenceEquals(finished, loop);

        if (endedOnItsOwn)
        {
            // Disposal re-raises the encoder's failure — that is what turns this into a Failed export
            // rather than a silent one — so it is expected to throw here too.
            try
            {
                await sink.DisposeAsync();
            }
            catch (Exception)
            {
                // Asserted through `loop` instead; see below.
            }
        }

        await Assert.That(endedOnItsOwn).IsTrue();
        await Assert.That(loop.IsFaulted).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();

        // And the failure must say what ffmpeg said. The pipe breaks before FFMpegCore observes the
        // process exit, so the raw fault is "Pipe is broken" — true, and no help to anybody.
        Exception surfaced = loop.Exception!.GetBaseException();
        await Assert.That(surfaced).IsTypeOf<FfmpegEncodeException>();
        await Assert.That(surfaced.Message).Contains("Error opening output");
    }

    /// <summary>
    ///     A sink that only fails when it is closed must fail the run.
    ///     <para>
    ///         This is the partial-file lie: every frame rendered, so the loop succeeded, but the
    ///         container was never finalised. Muxing happens on disposal, so "all frames written" is not
    ///         "a file exists that plays" — reporting <see cref="ExportPhase.Completed" /> here would
    ///         point a user at a file that does not decode.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ASinkThatFailsOnlyWhenClosed_FailsTheRun_AndNeverReportsCompleted()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        FailOnCloseFrameSink sink = new();
        List<ExportProgress> reports = [];

        Exception? failure = null;
        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(6),
                ExportFixtures.Source(6), sink, surfaces, new DirectProgress(reports.Add),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(sink.DisposeCount).IsEqualTo(1);

        foreach (ExportProgress report in reports)
        {
            await Assert.That(report.Phase).IsNotEqualTo(ExportPhase.Completed);
        }
    }

    /// <summary>
    ///     The terminal progress report is a contract: exactly one arrives, on every path.
    ///     <para>
    ///         A caller that drives a progress bar off <see cref="ExportProgress.Phase" /> has nothing
    ///         else to go on. Before the fix a throwing disposal escaped the session's <c>finally</c>
    ///         before the terminal report was made, so the last thing such a caller ever saw was
    ///         <see cref="ExportPhase.Rendering" /> — a bar frozen at 100 % on an export that failed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AThrowingDisposal_StillReportsATerminalPhase()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        FailOnCloseFrameSink sink = new();
        List<ExportProgress> reports = [];

        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(4),
                ExportFixtures.Source(4), sink, surfaces, new DirectProgress(reports.Add),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The point of the test is what was reported, not what was thrown.
        }

        await Assert.That(reports.Count).IsGreaterThan(0);
        await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Failed);
        await Assert.That(reports[^1].Detail).IsNotNull();
    }

    /// <summary>A cancelled run still reports its terminal phase, and reports it as cancelled.</summary>
    [Test]
    public async Task ACancelledRun_ReportsCancelled_AsItsLastWord()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        using CancellationTokenSource source = new();
        CancellingFrameSink sink = new(source, 3);
        List<ExportProgress> reports = [];

        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(40),
                ExportFixtures.Source(40), sink, surfaces, new DirectProgress(reports.Add),
                source.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Cancelled);
    }

    /// <summary>Reports straight through, so a test sees them in the order the session made them.</summary>
    private sealed class DirectProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }

    /// <summary>Accepts every frame and then fails on close — a container that will not mux.</summary>
    private sealed class FailOnCloseFrameSink : IFrameSink
    {
        public int DisposeCount { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            throw new InvalidOperationException("the container could not be finalised");
        }
    }
}

/// <summary>
///     The bridge must never leave a writer parked on a queue that has stopped being drained. Its
///     <c>Fault</c> path existed from the start and had no caller — these pin the caller.
/// </summary>
public class ChannelVideoFrameSourceFaultTests
{
    [Test]
    public async Task FaultingTheChannel_ReleasesAWriterAlreadyBlockedOnAFullQueue()
    {
        ChannelVideoFrameSource source = new(2);
        byte[] frame = new byte[16];

        Task writer = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await source.WriteAsync(frame, 2, 2, CancellationToken.None);
            }
        });

        // Let the writer fill the queue and park on it.
        Stopwatch spin = Stopwatch.StartNew();
        while (!writer.IsCompleted && spin.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10);
        }

        await Assert.That(writer.IsCompleted).IsFalse();

        source.Fault(new InvalidOperationException("the encoder exited"));

        Task finished = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(finished, writer)).IsTrue();
        await Assert.That(writer.IsFaulted).IsTrue();

        source.Dispose();
    }
}
