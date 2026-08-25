#region

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pins the CURRENT control's output as the corpus B1 must match, and captures the paired
///     <c>SceneFixture</c> from the same push, so the JSON and the PNG describe the same world state —
///     which is the entire point of the B1 parity gate.
///     <para>
///         <b>Determinism.</b> Only <see cref="CameraMode.Fit" /> is deterministic (the smooth modes lerp
///         per render frame), and marker interpolation seeds ON the player at first appearance, so the
///         first captured frame has no glide. The capture asserts both — that every smoothed marker
///         position equals its raw position — and refuses to write a golden it cannot reproduce.
///     </para>
///     <para>
///         <b>Regeneration.</b> A missing golden is a FAILURE unless <c>PB2D_GOLDEN_UPDATE=1</c> is set;
///         a golden that silently rewrites itself is a test that no longer tests. See
///         <c>scripts/update-playback2d-goldens.sh</c>.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class Playback2DGoldenCaptureTests
{
    private const int CaptureSize = 900;
    private const string UpdateEnvVar = "PB2D_GOLDEN_UPDATE";

    [Test]
    public async Task Mirage_RoundStart_MatchesGolden() =>
        await CaptureAndCompare("duel-mirage-b",
            ["furia-vs-vitality-m1-mirage.dem", "vitality-vs-fut-m1-mirage.dem"]);

    [Test]
    public async Task Nuke_TwoFloors_MatchesGolden() =>
        await CaptureAndCompare("nuke-multilevel",
            ["003816306022075596881_1029495947.dem", "match730_003826256877184877003_0981591541_410.dem"]);

    [Test]
    public async Task Dust2_RoundStart_MatchesGolden() =>
        await CaptureAndCompare("fitmap-mirage-eco", ["003801777854962729156_0256036251.dem"]);

    /// <summary>
    ///     The corpus root, resolved by walking up to the directory holding
    ///     <c>DemoViewer.NET.slnx</c>. The captures write into the repo, not into the build output.
    /// </summary>
    internal static string CorpusRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "fixtures", "playback2d");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root from " +
                                            AppContext.BaseDirectory);
    }

    private static async Task CaptureAndCompare(string name, IReadOnlyList<string> demoCandidates)
    {
        string? path = null;
        foreach (string candidate in demoCandidates)
        {
            path = DemoTestHelper.FindDemoPath(candidate);
            if (path is not null)
            {
                break;
            }
        }

        if (path is null)
        {
            throw new SkipTestException($"no demo present for golden '{name}' (tried " +
                                        string.Join(", ", demoCandidates) + ")");
        }

        Capture capture = await Render(path);
        if (capture.Png.Length == 0)
        {
            throw new SkipTestException($"headless capture produced no frame for '{name}'");
        }

        if (capture.GlidingMarkers.Count > 0)
        {
            throw new InvalidOperationException(
                $"golden '{name}' is not reproducible: markers {string.Join(",", capture.GlidingMarkers)} " +
                "were still interpolating at capture time");
        }

        string corpus = CorpusRoot();
        string goldenPath = Path.Combine(corpus, "goldens", "cpu",
            $"{name}@{CaptureSize}x{CaptureSize}.png");
        string scenePath = Path.Combine(corpus, "scenes", $"{name}.scene.json");

        // The fixture is written from the SAME push that produced the PNG, so B1 can re-render this JSON
        // and diff against this image.
        Directory.CreateDirectory(Path.GetDirectoryName(scenePath)!);
        SceneFixtureSerializer.WriteFile(new SceneFixture
        {
            Frame = capture.Frame,
            Time = capture.Frame.Time,
            Camera = capture.Camera,
            Size = new SKSizeI(CaptureSize, CaptureSize),
            MapName = capture.MapName,
            SourceDemoId = Path.GetFileName(path),
            Notes = $"Captured from the pre-v2 Playback2DViewport at frame {capture.FrameIndex}, " +
                    "CameraMode.Fit. B1 must re-render this to match the paired golden."
        }, scenePath);

        if (!File.Exists(goldenPath))
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"no golden at {goldenPath}. Regenerate deliberately with " +
                    "scripts/update-playback2d-goldens.sh (or " +
                    $"{UpdateEnvVar}=1 dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release " +
                    "-- --treenode-filter \"/*/*/Playback2DGoldenCaptureTests/*\").");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllBytesAsync(goldenPath, capture.Png);
            Console.WriteLine($"[golden] wrote {goldenPath} ({capture.Png.Length} bytes)");
            return;
        }

        byte[] expected = await File.ReadAllBytesAsync(goldenPath);
        GoldenComparison result =
            GoldenImageComparer.Compare(expected, capture.Png, GoldenTolerance.DefaultPerceptual);
        Console.WriteLine($"[golden] {name} match={result.Match} " +
                          $"maxDelta={result.MaxChannelDelta} diff={result.MismatchedFraction:P4}");

        if (!result.Match)
        {
            string actualPath = Path.Combine(HeadlessSession.ArtifactDir, $"{name}.actual.png");
            await File.WriteAllBytesAsync(actualPath, capture.Png);
            if (GoldenImageComparer.CreateDiffPng(expected, capture.Png) is { } diff)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(HeadlessSession.ArtifactDir, $"{name}.diff.png"), diff);
            }
        }

        await Assert.That(result.FailureReason).IsNull();
        await Assert.That(result.Match).IsTrue();
    }

    private static async Task<Capture> Render(string demoPath)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        int target = FindRoundStartFrame(frames);
        EntityTracker tracker = new();
        tracker.ReplayToIndex(target, frames);

        PlaybackController controller = new();
        controller.LoadDemo(frames, 64);
        controller.SyncPositionFromShell(target);
        controller.PublishTracker(tracker);

        ModuleContext context = new(controller, () => demoPath);
        context.SetRoster(demo.Players.Values.Select(p => new PlayerRosterEntry
        {
            Slot = p.Slot,
            SteamId = p.SteamId64,
            Name = p.Name
        }));
        context.SetMapName(demo.MapName);

        Playback2DTabViewModel vm = new();
        Capture capture = new()
        {
            FrameIndex = target,
            MapName = demo.MapName
        };

        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DViewport viewport = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = CaptureSize,
                Height = CaptureSize,
                Content = viewport
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.OnActivated(context);

            // Fit is the ONLY deterministic mode: AdvanceCameras skips it entirely, so nothing lerps
            // between the two render ticks below.
            viewport.Mode = CameraMode.Fit;

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            foreach (PlayerMarker marker in vm.Markers)
            {
                if (viewport.SmoothedMarkerPosition(marker.Slot) is not { } smoothed)
                {
                    continue;
                }

                if (Math.Abs(smoothed.X - marker.WorldX) > 0.01f ||
                    Math.Abs(smoothed.Y - marker.WorldY) > 0.01f)
                {
                    capture.GlidingMarkers.Add(marker.Slot);
                }
            }

            capture.Frame = vm.CurrentFrame;
            capture.Camera = viewport.PrimaryCameraTransform;

            WriteableBitmap? rendered = window.CaptureRenderedFrame();
            if (rendered is not null)
            {
                using MemoryStream stream = new();
                rendered.Save(stream);
                capture.Png = stream.ToArray();
            }

            await Task.CompletedTask;
        });

        return capture;
    }

    // First round_freeze_end past warmup, plus a few frames → all players alive, still near their spawns.
    // Lifted from ZRadarRenderTests so the two capture harnesses cannot disagree about "round start".
    internal static int FindRoundStartFrame(IReadOnlyList<DemoFrame> frames)
    {
        int start = frames.Count / 8, end = frames.Count * 3 / 4;
        for (int i = start; i < end; i++)
        {
            bool freezeEnd = frames[i].InnerMessages.Any(m =>
                m is GameEventMessage gem &&
                gem.DecodedEvent.Name.Equals("round_freeze_end", StringComparison.OrdinalIgnoreCase));
            if (freezeEnd)
            {
                return Math.Min(i + 12, frames.Count - 1);
            }
        }

        return frames.Count / 2;
    }

    private sealed class Capture
    {
        public byte[] Png { get; set; } = [];
        public Scene2DFrame Frame { get; set; } = Scene2DFrame.Empty;
        public ViewportTransform Camera { get; set; }
        public int FrameIndex { get; init; }
        public string? MapName { get; init; }
        public List<int> GlidingMarkers { get; } = [];
    }
}
