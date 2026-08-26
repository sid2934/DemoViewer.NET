#region

using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Pipeline;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     <b>D6 finding 28 — <c>RenderPurpose</c> is threaded through the whole pipeline and read by nothing.</b>
///     <para>
///         The value travels <c>SceneSubmission.Purpose</c> → <c>SceneCompositor</c> →
///         <c>SceneRenderContext.Purpose</c>, which every <c>ISceneLayer.Draw</c> receives — and the
///         compositor's copy is the only production read of it anywhere. <c>Export</c> and
///         <c>Interactive</c> render identically, <c>Thumbnail</c> is never submitted at all, and design
///         §5.1's "layers may trade quality for latency on it" described an intention that the enum's own
///         doc presented as a shipped contract.
///     </para>
///     <para>
///         Round 3A marked it <b>reserved</b> rather than inventing a quality difference: any real
///         Export-vs-Interactive divergence moves the golden corpus, which round 2A has just re-baselined,
///         and a golden moving now would be indistinguishable from that. <b>These tests are what keep the
///         word honest.</b> The doc case fails if the reservation is quietly dropped from the enum; the
///         pixel case fails the moment a layer actually branches on it — so the commit that implements the
///         contract is forced to rewrite the documentation and this suite in the same breath, instead of
///         leaving one of them describing the other's past.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class RenderPurposeTests
{
    private static readonly SKSizeI _size = new(320, 240);

    /// <summary>
    ///     The enum says what is true of it. Absence of a branch is not something a reader can see; a
    ///     doc that promises one they cannot find is worse than silence, and that promise is what shipped.
    /// </summary>
    [Test]
    public async Task TheEnumDeclaresItselfReserved_AndNamesThumbnailAsNeverProduced()
    {
        string path = Path.Combine(RepoRoot(), "src", "Playback2D",
            "DemoViewer.NET.Playback2D.Core", "RenderPurpose.cs");

        await Assert.That(File.Exists(path)).IsTrue()
            .Because($"RenderPurpose.cs moved; this suite is reading nothing (looked at '{path}')");

        string source = File.ReadAllText(path);
        Console.WriteLine($"[render-purpose] {path} — {source.Length} chars");

        await Assert.That(source).Contains("RESERVED")
            .Because("the members render identically and the type has to say so");
        await Assert.That(source).Contains("Never produced")
            .Because("Thumbnail has no producer anywhere, and the next reader must not go hunting for it");
        await Assert.That(source).Contains("RenderPurposeTests")
            .Because("the doc points at the guard that keeps it true, so neither can be edited alone");
    }

    /// <summary>
    ///     The mechanical half. All three purposes, one fixture, one <c>dt</c> — identical bytes. This is
    ///     the assertion the implementing commit has to come back and change.
    /// </summary>
    [Test]
    public async Task EveryPurpose_RendersTheSamePixels()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();

        string export = Hash(fixture, RenderPurpose.Export);
        string interactive = Hash(fixture, RenderPurpose.Interactive);
        string thumbnail = Hash(fixture, RenderPurpose.Thumbnail);

        Console.WriteLine($"[render-purpose] export={export[..16]} interactive={interactive[..16]} "
                          + $"thumbnail={thumbnail[..16]}");

        await Assert.That(interactive).IsEqualTo(export)
            .Because("no layer branches on the purpose — if one now does, say so in RenderPurpose's doc "
                     + "and re-baseline whatever golden it moves, deliberately");
        await Assert.That(thumbnail).IsEqualTo(export)
            .Because("Thumbnail is not merely unproduced, it is also unimplemented");
    }

    /// <summary>
    ///     The value still ARRIVES, which is why it is worth keeping: the seam a fidelity/latency split
    ///     would need is in place at the one place a layer could act on it. A reservation that had also
    ///     stopped being plumbed would be worth deleting instead.
    /// </summary>
    [Test]
    public async Task ThePurposeStillReachesTheLayers()
    {
        SceneFixture fixture = SyntheticScenes.FullSceneBudget();
        PurposeProbeLayer probe = new();

        using SceneStage stage = new(_size, extra: probe);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);
        stage.Renderer.Purpose = RenderPurpose.Thumbnail;

        SceneTime time = fixture.Time;
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.Render();

        Console.WriteLine($"[render-purpose] layer saw {probe.Seen.Count} context(s), "
                          + $"distinct purposes: {string.Join(", ", probe.Seen.Distinct())}");

        await Assert.That(probe.Seen).IsNotEmpty()
            .Because("a reserved value nothing even receives would be plumbing to delete, not to keep");
        await Assert.That(probe.Seen.All(p => p == RenderPurpose.Thumbnail)).IsTrue()
            .Because("the compositor copies the submission's purpose into every pane's context");
    }

    private static string Hash(SceneFixture fixture, RenderPurpose purpose)
    {
        using SceneStage stage = new(_size);
        stage.Renderer.Levels.SetAuthoritativeFloors(SyntheticScenes.BudgetFloors);
        stage.Renderer.Purpose = purpose;

        SceneTime time = fixture.Time;
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.SetAllCameras(fixture.Camera);
        stage.Renderer.Advance(fixture.Frame, in time);
        stage.Renderer.Render();

        return Convert.ToHexString(SHA256.HashData(stage.Renderer.SnapshotPng()));
    }

    private static string RepoRoot()
    {
        // The corpus root is <repo>/tests/fixtures/playback2d, resolved by walking up to the .slnx —
        // reusing it keeps one answer to "where is the repo" in this assembly.
        DirectoryInfo dir = new(FixtureCorpus.Root);
        return dir.Parent?.Parent?.Parent?.FullName ?? AppContext.BaseDirectory;
    }
}

/// <summary>
///     Records the <see cref="RenderPurpose" /> every context it is drawn with carried. Draws nothing —
///     an extra layer that painted would change the pixels the case above compares.
/// </summary>
internal sealed class PurposeProbeLayer : ISceneLayer
{
    public List<RenderPurpose> Seen { get; } = [];

    public string Id => "test.purposeprobe";

    public int Order => 9999;

    public LayerSlot Slot => LayerSlot.Overlay;

    // Dynamic, so the compositor never replays a cached picture instead of calling Render — a probe that
    // stops being asked would report an empty list and read as "the purpose no longer arrives".
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    public int ContentVersion => 0;

    public bool IsEnabled { get; set; } = true;

    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    public void Render(SKCanvas canvas, SceneRenderContext ctx) => Seen.Add(ctx.Purpose);

    public void Dispose()
    {
    }
}
