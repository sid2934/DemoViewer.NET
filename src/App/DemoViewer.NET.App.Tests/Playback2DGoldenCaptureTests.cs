#region

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
///     Pins the CURRENT control's output as the corpus this parity gate must match, and captures the
///     paired <c>SceneFixture</c> from the same push, so the JSON and the PNG describe the same world
///     state.
///     <para>
///         Only <see cref="CameraMode.Fit" /> is deterministic (the smooth modes lerp per render frame),
///         and marker interpolation seeds ON the player at first appearance, so the first captured frame
///         has no glide. The capture asserts both: every smoothed marker position must equal its raw
///         position, and it refuses to write a golden it cannot reproduce. A missing golden is a FAILURE
///         unless <c>PB2D_GOLDEN_UPDATE=1</c> is set; see <c>scripts/update-playback2d-goldens.sh</c>.
///     </para>
///     <para>
///         <b>The scene write shares the PNG's regeneration guard</b>, or an App-suite run silently
///         rewrites <c>scenes/nuke-multilevel.scene.json</c> — the input to <c>GoldenParityTests</c> and
///         <c>LevelGoldenTests</c> — because the tour sample ships in every checkout. These captures own
///         the <c>prev2-</c> namespace exclusively, so a capture on a machine with Mirage demos staged
///         cannot overwrite a hand-authored dv2d fixture. <c>nuke-multilevel</c> keeps its name because
///         that scene, golden and expectation all originate here.
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
        await CaptureAndCompare("prev2-mirage-roundstart",
            ["furia-vs-vitality-m1-mirage.dem", "vitality-vs-fut-m1-mirage.dem"]);

    [Test]
    public async Task Nuke_TwoFloors_MatchesGolden() =>
        await CaptureAndCompare("nuke-multilevel",
        [
            "003816306022075596881_1029495947.dem",
            "match730_003826256877184877003_0981591541_410.dem",

            // Repo-relative, and therefore present in EVERY checkout and on CI: the bundled tour
            // sample is the first 3 rounds of a pro de_nuke GOTV demo (docs/tour-sample-demo.md),
            // trimmer-verified and app-loadable. DemoTestHelper does not search assets/tour, so
            // without this entry the one fixture that most needs a two-floor map, and this parity
            // gate with it, would sit empty because no such demo is staged anywhere.
            "assets/tour/sample-de_nuke.dem"
        ]);

    // `prev2-`, not `fitmap-mirage-eco`: that name belongs to a hand-authored 640×360 fixture in the
    // manifest, and this harness writes a 900×900 capture of the PRE-V2 control. One name, one meaning.
    [Test]
    public async Task FitMap_Eco_MatchesGolden() =>
        await CaptureAndCompare("prev2-mirage-eco", ["003801777854962729156_0256036251.dem"]);

    /// <summary>
    ///     The corpus root, resolved by walking up to the directory holding
    ///     <c>DemoViewer.NET.slnx</c>. The captures write into the repo, not into the build output.
    /// </summary>
    internal static string CorpusRoot() =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "playback2d");

    /// <summary>The directory holding <c>DemoViewer.NET.slnx</c>, found by walking up from the binaries.</summary>
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root from " +
                                            AppContext.BaseDirectory);
    }

    /// <summary>
    ///     Resolves one demo candidate. A bare file name goes through the usual
    ///     <see cref="DemoTestHelper" /> search (<c>DEMO_PATH</c> → <c>TestData/</c> →
    ///     <c>demos/benchmarks/</c> → <c>demos/</c>); a candidate containing a separator is treated as
    ///     repo-relative, so a demo committed to the tree — rather than staged by a developer — is
    ///     reachable. Returns null when neither locates a file.
    /// </summary>
    /// <param name="candidate">A bare demo file name, or a repo-relative path.</param>
    private static string? ResolveDemo(string candidate)
    {
        if (candidate.Contains('/', StringComparison.Ordinal) ||
            candidate.Contains('\\', StringComparison.Ordinal))
        {
            string absolute = Path.Combine(RepoRoot(), candidate.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(absolute) ? absolute : null;
        }

        return DemoTestHelper.FindDemoPath(candidate);
    }

    private static async Task CaptureAndCompare(string name, IReadOnlyList<string> demoCandidates)
    {
        string? path = null;
        foreach (string candidate in demoCandidates)
        {
            path = ResolveDemo(candidate);
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
        bool updating = string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1",
            StringComparison.Ordinal);

        if (!File.Exists(goldenPath) || updating)
        {
            if (!updating)
            {
                throw new InvalidOperationException(
                    $"no golden at {goldenPath}. Regenerate deliberately with " +
                    "scripts/update-playback2d-goldens.sh (or " +
                    $"{UpdateEnvVar}=1 dotnet run --project src/App/DemoViewer.NET.App.Tests -c Release " +
                    "-- --treenode-filter \"/*/*/Playback2DGoldenCaptureTests/*\").");
            }

            // The fixture is written from the SAME push that produced the PNG, so the parity and level
            // suites can diff JSON against image knowing both came from one run — gated identically to
            // the PNG for the same reason described on the class.
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
                        "CameraMode.Fit. The v2 compositor must re-render this to match the paired golden."
            }, scenePath);

            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllBytesAsync(goldenPath, capture.Png);
            Console.WriteLine($"[golden] wrote {scenePath}");
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

        // Calibrate the game clock, exactly as the shell does on load. Without it CurtimeSeconds is the
        // naive tick/tickRate and every round/bomb timer in the captured fixture is offset by clockBase
        // — measured once as a 7:14 round clock on a 1:55 round. Nothing in the PICTURE depends on it,
        // but a fixture whose game info is wrong will mislead whoever reads it next.
        int firstFreezeEnd = FindFirstFreezeEndFrame(frames);
        (double clockBase, bool clockValid) = GameClock.ComputeClockBase(frames, firstFreezeEnd, 64);
        context.SetGameClock(clockBase);
        Console.WriteLine($"[capture] clockBase={clockBase:F3} valid={clockValid} " +
                          $"firstFreezeEnd={firstFreezeEnd}");

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

    /// <summary>The first <c>round_freeze_end</c> in the demo — the clock calibration point.</summary>
    /// <param name="frames">The demo's frames.</param>
    private static int FindFirstFreezeEndFrame(IReadOnlyList<DemoFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            bool freezeEnd = frames[i].InnerMessages.Any(m =>
                m is GameEventMessage gem &&
                gem.DecodedEvent.Name.Equals("round_freeze_end", StringComparison.OrdinalIgnoreCase));
            if (freezeEnd)
            {
                return i;
            }
        }

        return -1;
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
