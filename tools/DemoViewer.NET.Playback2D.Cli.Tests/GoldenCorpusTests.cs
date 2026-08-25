#region

using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The manifest reader and its budget arithmetic. (The image comparator itself is B0's, and is
///     covered by <c>GoldenImageComparerTests</c> in the Playback2D suite — one comparator, one test
///     class.)
/// </summary>
public class GoldenCorpusTests
{
    [Test]
    public async Task Load_ReadsTheCommittedManifest()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);

        await Assert.That(corpus.SchemaVersion).IsEqualTo(GoldenCorpus.CurrentSchemaVersion);
        await Assert.That(corpus.Entries.Count).IsGreaterThanOrEqualTo(6);
        await Assert.That(corpus.Find("duel-mirage-b")).IsNotNull();
        await Assert.That(corpus.Find("no-such-entry")).IsNull();
    }

    [Test]
    public async Task EveryNonPendingEntry_HasASceneAndACpuGolden()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);

        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            if (entry.Pending)
            {
                continue;
            }

            await Assert.That(File.Exists(entry.ScenePath)).IsTrue();
            await Assert.That(File.Exists(entry.GoldenPath(RenderBackend.CpuRaster))).IsTrue();
        }
    }

    [Test]
    public async Task GoldenPath_FollowsTheCanonicalLayout()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);
        GoldenCorpusEntry entry = corpus.Find("duel-mirage-b")!;

        string cpu = entry.GoldenPath(RenderBackend.CpuRaster);
        string gpu = entry.GoldenPath(RenderBackend.OpenGl);

        await Assert.That(cpu).EndsWith(Path.Combine("goldens", "cpu", "duel-mirage-b@640x360.png"));
        await Assert.That(gpu).EndsWith(Path.Combine("goldens", "gpu", "duel-mirage-b@640x360.png"));
    }

    [Test]
    public async Task Budget_ScalesTimeButNeverAllocation()
    {
        GoldenBudget scaled = new GoldenBudget(8, 2, 0).Scaled(2.5);

        await Assert.That(scaled.RenderP99Ms).IsEqualTo(20.0);
        await Assert.That(scaled.AdvanceP99Ms).IsEqualTo(5.0);
        await Assert.That(scaled.BytesPerFrame).IsEqualTo(0);
    }

    [Test]
    public async Task Upsert_ReplacesByName_AndPreservesUnknownMembers()
    {
        using CorpusCopy copy = new();
        string manifestPath = Path.Combine(copy.Path, GoldenCorpus.ManifestFileName);
        string before = File.ReadAllText(manifestPath);
        int entriesBefore = GoldenCorpus.Load(copy.Path).Entries.Count;

        GoldenCorpusEntry existing = GoldenCorpus.Load(copy.Path).Find("duel-mirage-b")!;
        GoldenCorpus.Upsert(copy.Path, existing with { Pending = true });

        GoldenCorpus after = GoldenCorpus.Load(copy.Path);
        await Assert.That(after.Entries.Count).IsEqualTo(entriesBefore);
        await Assert.That(after.Find("duel-mirage-b")!.Pending).IsTrue();
        await Assert.That(File.ReadAllText(manifestPath)).IsNotEqualTo(before);
        await Assert.That(after.Find("synthetic-utility")!.Notes).IsNotNull();
    }

    [Test]
    public async Task FindDefaultCorpusDirectory_FindsTheCheckoutCorpus()
    {
        string? found = GoldenCorpus.FindDefaultCorpusDirectory();

        await Assert.That(found).IsNotNull();
        await Assert.That(Path.GetFullPath(found!)).IsEqualTo(Path.GetFullPath(Dv2d.CorpusDirectory));
    }
}
