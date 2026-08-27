#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The CI pixel gate. Every case works on a temp COPY of the corpus, so a failing assertion can
///     never rewrite a committed golden.
/// </summary>
[NotInParallel]
[Category("Render")]
public class GoldenCommandTests
{
    [Test]
    public async Task Verify_OnThePristineCorpus_ExitsZero()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        JsonObject counts = (JsonObject)run.Json()["counts"]!;
        await Assert.That(counts["mismatched"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(counts["missing"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(counts["matched"]!.GetValue<int>()).IsGreaterThan(0);
    }

    [Test]
    public async Task Verify_PendingEntries_AreSkippedNotFailed()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        JsonObject payload = run.Json();
        await Assert.That(((JsonObject)payload["counts"]!)["skipped"]!.GetValue<int>()).IsGreaterThan(0);

        foreach (JsonNode? result in (JsonArray)payload["results"]!)
        {
            string status = result!["status"]!.GetValue<string>();
            await Assert.That(status is "match" or "skipped").IsTrue();
        }
    }

    /// <summary>
    ///     The gate's teeth: one corrupted golden, one mismatch, exit 4, evidence on disk.
    ///     <para>
    ///         <b>At the shipping tolerance, deliberately.</b> Skia's glyph rasteriser differs across
    ///         operating systems, so a whole-corpus <c>--tolerance byte-exact</c> run fails all eight
    ///         text-bearing entries on Linux, not just the one this test corrupts. Run at the per-entry
    ///         tolerance the CI lane uses, the budget that forgives cross-OS glyph edges does
    ///         <b>not</b> forgive a real regression, and the other entries verify clean around it.
    ///     </para>
    ///     <para>
    ///         The corruption lands at (1,1) — the frame's top-left corner, nowhere near a glyph — and
    ///         inverts the red channel, a delta of 213 against a dark background. That is over the glyph
    ///         tier's own 96 ceiling twice over, so it fails on <c>max channel delta</c>: the first rule,
    ///         and the one no budget can spend its way past.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Verify_CorruptedGolden_ExitsFour_AndWritesADiff()
    {
        using CorpusCopy copy = new();
        copy.CorruptGolden("synthetic-tenplayers");
        using TempDirectory diffs = new();

        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--diff-dir", diffs.Path,
            "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        JsonObject payload = run.Json();
        JsonObject counts = (JsonObject)payload["counts"]!;
        await Assert.That(counts["mismatched"]!.GetValue<int>()).IsEqualTo(1);

        // "For the right reason": the one mismatch is the entry that was corrupted, everything else
        // matched, and the failure names the ceiling rather than a budget that merely ran out.
        JsonNode row = ((JsonArray)payload["results"]!)
            .Single(r => r!["status"]!.GetValue<string>() == "mismatch")!;
        await Assert.That(row["name"]!.GetValue<string>()).IsEqualTo("synthetic-tenplayers");
        await Assert.That(row["reason"]!.GetValue<string>()).Contains("max channel delta");
        await Assert.That(counts["matched"]!.GetValue<int>()).IsEqualTo(
            counts["total"]!.GetValue<int>() - counts["skipped"]!.GetValue<int>() - 1);

        await Assert.That(File.Exists(Path.Combine(diffs.Path, "synthetic-tenplayers.actual.png"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(diffs.Path, "synthetic-tenplayers.diff.png"))).IsTrue();
    }

    /// <summary>
    ///     <c>--tolerance byte-exact</c> still overrides the manifest's per-entry mode, and is still
    ///     strictly stricter than what that mode resolves to.
    ///     <para>
    ///         Both halves matter and neither alone would do. A one-step nudge to a golden is inside
    ///         every perceptual budget — the ±8 band, both SSIM floors — so the default verify stays
    ///         green; byte-exact refuses the same corpus. Scoped to one entry with <c>--name</c> because
    ///         a whole-corpus byte-exact verify is only green on the platform that authored the PNGs,
    ///         which is the property this flag exists to let a maintainer check locally, not an
    ///         invariant CI can assert.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ByteExactOverride_RefusesWhatThePerEntryToleranceForgives()
    {
        using CorpusCopy copy = new();
        copy.NudgeGolden("synthetic-tenplayers");

        CliRun forgiving = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--name",
            "synthetic-tenplayers", "--json");
        await Assert.That(forgiving.ExitCode).IsEqualTo(0);
        await Assert.That(((JsonObject)forgiving.Json()["tolerance"]!)["mode"]!.GetValue<string>())
            .IsEqualTo("per-entry");

        CliRun exact = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--name",
            "synthetic-tenplayers", "--tolerance", "byte-exact", "--json");
        await Assert.That(exact.ExitCode).IsEqualTo(4);
        JsonObject payload = exact.Json();
        await Assert.That(((JsonObject)payload["tolerance"]!)["mode"]!.GetValue<string>())
            .IsEqualTo("byte-exact");
        await Assert.That(((JsonArray)payload["results"]!)[0]!["tolerance"]!.GetValue<string>())
            .IsEqualTo("byte-exact");
    }

    /// <summary>
    ///     The glyph budget is reported, and its denominator with it: a payload that omits the label
    ///     count cannot be checked against the manifest a reader has open, and
    ///     <c>above_ceiling_fraction</c> is the quantity that denominator is spent on.
    /// </summary>
    [Test]
    public async Task Verify_ReportsTheGlyphBudgetAndWhatSpentIt()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--json");

        JsonNode row = ((JsonArray)run.Json()["results"]!)
            .Single(r => r!["name"]!.GetValue<string>() == "synthetic-tenplayers")!;

        // Ten labelled markers in the capture; the count is read off the scene, so it cannot be edited
        // upward without adding a player and re-baselining the golden a reviewer then looks at.
        await Assert.That(row["labels"]!.GetValue<int>()).IsEqualTo(10);
        await Assert.That(row["above_ceiling_fraction"]).IsNotNull();
        await Assert.That(row["min_window_ssim"]).IsNotNull();

        // 6 px per label over the frame's area off the authoring platform, and a closed tier on it.
        double budget = row["glyph_budget"]!.GetValue<double>();
        double expected = GoldenTolerance.GlyphsMatchTheCorpus ? 0 : 6.0 * 10 / (640.0 * 360.0);
        await Assert.That(budget).IsEqualTo(expected).Within(1e-12);
    }

    [Test]
    public async Task Update_ThenVerify_IsGreen()
    {
        using CorpusCopy copy = new();
        copy.CorruptGolden("synthetic-tenplayers");

        CliRun update = Dv2d.InProcess("golden", "update", "--corpus", copy.Path, "--name",
            "synthetic-tenplayers", "--quiet");
        await Assert.That(update.ExitCode).IsEqualTo(0);

        CliRun verify = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--quiet");
        await Assert.That(verify.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Verify_MissingGolden_ExitsFour()
    {
        using CorpusCopy copy = new();

        // Not synthetic-empty: that entry is `pending` (see its manifest note), and a pending entry is
        // skipped before its golden is ever looked for, which would make this case pass for the wrong
        // reason.
        File.Delete(Path.Combine(copy.Path, "goldens", "cpu", "synthetic-tenplayers@640x360.png"));
        using TempDirectory diffs = new();

        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--diff-dir", diffs.Path,
            "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        await Assert.That(((JsonObject)run.Json()["counts"]!)["missing"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Verify_StaleMapVersion_IsRefused_RatherThanDiffed()
    {
        using CorpusCopy copy = new();
        copy.SetMapVersion("fitmap-mirage-eco", "deadbeef");

        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", copy.Path, "--assets",
            Dv2d.AssetsDirectory, "--json");

        await Assert.That(run.ExitCode).IsEqualTo(4);
        JsonArray results = (JsonArray)run.Json()["results"]!;
        bool sawStale = results.Any(r => r!["status"]!.GetValue<string>() == "stale-assets");
        await Assert.That(sawStale).IsTrue();
    }

    [Test]
    public async Task UnknownName_ExitsOne()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--name",
            "no-such-fixture");

        await Assert.That(run.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task SizeOverride_IsRefused_BecauseAGoldenIsNamedForItsSize()
    {
        CliRun run = Dv2d.InProcess("golden", "verify", "--corpus", Dv2d.CorpusDirectory, "--size",
            "800x600");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("unknown option");
    }
}

/// <summary>A writable copy of the committed corpus, for cases that must mutate it.</summary>
internal sealed class CorpusCopy : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>Copies the corpus.</summary>
    public CorpusCopy()
    {
        Path = System.IO.Path.Combine(_temp.Path, "playback2d");
        CopyDirectory(Dv2d.CorpusDirectory, Path);
    }

    /// <summary>The copy's root.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>
    ///     Inverts the red channel of one pixel in an entry's golden — a delta of 213 against the dark
    ///     palette background, at (1,1) where no glyph reaches. Well over the glyph tier's 96 ceiling, so
    ///     it is caught by the same perceptual budget CI runs at, not only by byte-exact.
    /// </summary>
    /// <param name="name">The entry name.</param>
    public void CorruptGolden(string name) =>
        Rewrite(name, static p => new SKColor((byte)(p.Red ^ 0xFF), p.Green, p.Blue, p.Alpha));

    /// <summary>
    ///     Moves one pixel of an entry's golden by a single step — the smallest possible change, and one
    ///     that every perceptual budget forgives (it is inside the ±8 band, so it spends neither the
    ///     0.5 % coverage budget nor the glyph tier). What byte-exact must still refuse.
    /// </summary>
    /// <param name="name">The entry name.</param>
    public void NudgeGolden(string name) =>
        Rewrite(name, static p =>
            new SKColor((byte)(p.Red == 255 ? 254 : p.Red + 1), p.Green, p.Blue, p.Alpha));

    private void Rewrite(string name, Func<SKColor, SKColor> change)
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Path);
        GoldenCorpusEntry entry = corpus.Find(name)!;
        string goldenPath = entry.GoldenPath(Playback2D.Core.Rendering.RenderBackend.CpuRaster);

        using SKBitmap bitmap = SKBitmap.Decode(goldenPath);
        bitmap.SetPixel(1, 1, change(bitmap.GetPixel(1, 1)));

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(goldenPath, data.ToArray());
    }

    /// <summary>Rewrites one entry's <c>map_version</c>, to simulate a re-baked radar.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="version">The version to claim.</param>
    public void SetMapVersion(string name, string version)
    {
        string manifestPath = System.IO.Path.Combine(Path, GoldenCorpus.ManifestFileName);
        JsonObject manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath))!;
        foreach (JsonNode? entry in (JsonArray)manifest["entries"]!)
        {
            if (entry!["name"]!.GetValue<string>() == name)
            {
                entry["map_version"] = version;
            }
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory,
                System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)));
        }
    }
}
