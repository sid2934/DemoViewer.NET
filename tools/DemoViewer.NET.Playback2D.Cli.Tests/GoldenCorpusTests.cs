#region

using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The manifest reader and its budget arithmetic. (The image comparator itself is covered
///     separately, by <c>GoldenImageComparerTests</c> in the Playback2D suite — one comparator, one test
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

    /// <summary>
    ///     <b>The corpus README's "which fixtures exist has exactly one answer", enforced.</b>
    ///     Every committed CPU golden must be named by an entry, at the size that entry declares.
    ///     <para>
    ///         Three things were wrong at once and none of them could be seen from either side alone.
    ///         <c>nuke-multilevel-noradar@900x900.png</c> was committed with no entry at all.
    ///         <c>nuke-single-upper</c> had goldens at 640×360 AND 900×900 with different
    ///         meanings, so one name described two pictures and the manifest could only ever describe
    ///         one of them. And <c>Playback2DGoldenCaptureTests</c> wrote 900×900 captures under
    ///         <c>duel-mirage-b</c> / <c>fitmap-mirage-eco</c> — names the manifest declares at 640×360 —
    ///         so its output landed at a path nothing reads, on top of two hand-authored fixtures.
    ///     </para>
    ///     <para>
    ///         The <c>prev2-</c> prefix is exempt and stays exempt: those are the pre-v2 control's own
    ///         captures, they only exist on a machine with the relevant demo staged, and they are gated
    ///         by <c>GoldenParityTests</c> rather than by <c>golden verify</c>. Exempting by prefix
    ///         rather than by name means a fourth capture cannot quietly reintroduce the collision.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EveryCommittedGolden_IsNamedByAnEntry_AtTheDeclaredSize()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);
        string cpuDir = Path.Combine(corpus.Directory, "goldens", "cpu");

        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach (GoldenCorpusEntry entry in corpus.Entries)
        {
            declared.Add(Path.GetFileName(entry.GoldenPath(RenderBackend.CpuRaster)));
        }

        List<string> orphans = [];
        foreach (string file in Directory.GetFiles(cpuDir, "*.png"))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith("prev2-", StringComparison.Ordinal) && !declared.Contains(name))
            {
                orphans.Add(name);
            }
        }

        Console.WriteLine($"[corpus] {declared.Count} declared goldens, " +
                          $"{Directory.GetFiles(cpuDir, "*.png").Length} committed, " +
                          $"{orphans.Count} orphaned");

        await Assert.That(orphans)
            .IsEmpty()
            .Because("a committed golden nothing names is a picture no gate reads");
    }

    /// <summary>
    ///     The other direction, and the reason <c>annotated-mirage-b</c> could be <c>pending</c> for
    ///     three phases with no scene file: a name in the manifest that resolves to nothing on disk is
    ///     invisible to <c>EveryNonPendingEntry_HasASceneAndACpuGolden</c>, which skips pending entries.
    ///     A pending entry may legitimately have no files — that is what pending is for — but its NOTE
    ///     must say so, so a reader of the manifest can tell "waiting on an asset" from "waiting on a
    ///     decision" without opening the directory.
    /// </summary>
    [Test]
    public async Task EveryPendingEntry_ExplainsItselfInItsNote()
    {
        GoldenCorpus corpus = GoldenCorpus.Load(Dv2d.CorpusDirectory);

        foreach (GoldenCorpusEntry entry in corpus.Entries.Where(e => e.Pending))
        {
            Console.WriteLine($"[corpus] pending {entry.Name}: {entry.Notes}");
            await Assert.That(entry.Notes).IsNotNull();
            await Assert.That(entry.Notes!).Contains("PENDING")
                .Because("a pending flag whose note does not say why is how three stale ones survived");
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
