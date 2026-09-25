#region

using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The §7.2 snapshot (suggested-tags.md): every committed real-round fixture under
///     <c>tests/fixtures/suggested-tags/</c> run through the shipped profile must propose exactly what
///     the file pins. No demo needed; the fold in the file is the input. A detector change that moves a
///     proposal fails here, and recapturing is a deliberate act with a diff
///     (<c>ST_GOLDEN_UPDATE=1</c> on the real-demo capture, which rewrites the proposals too).
/// </summary>
public class SuggestedTagsGoldenTests
{
    [Test]
    public async Task EveryCommittedRound_ProposesWhatItPins()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string[] files = Directory.GetFiles(SuggestedTagsGolden.FixtureDirectory(repo), "*.json");
        await Assert.That(files.Length).IsEqualTo(SuggestedTagsRealDemoTests.Pinned.Length)
            .Because("one pinned round per measured demo");

        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            string original = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            SuggestedTagsGolden golden = SuggestedTagsGolden.Load(file);
            golden.Proposals = golden.Detect();

            await Assert.That(golden.Serialize()).IsEqualTo(original).Because(Path.GetFileName(file));
        }
    }

    [Test]
    public async Task ThePinnedRounds_ExerciseEveryDetector()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        HashSet<string> detectors =
        [
            .. Directory.GetFiles(SuggestedTagsGolden.FixtureDirectory(repo), "*.json")
                .SelectMany(f => SuggestedTagsGolden.Load(f).Proposals.Select(p => p.Detector))
        ];

        await Assert.That(detectors).IsEquivalentTo(Modules.SuggestedTags.ProposalDetection.All.Select(d => d.Id));
    }
}
