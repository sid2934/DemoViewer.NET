#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The review loop over a real Valve replay, read in place from the <c>DEMO_PATH</c> folder: Round Facts
///     from the engine's rows, the evaluator building the proposals, and one accept and one reject landing
///     in the Tag Store. Written against the record and skipped until CS2DemoKit #54 lets the engine write
///     rows; the synthetic suite covers the same path meanwhile. The tour sample is never used.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class SuggestedTagsReviewRealDemoTests
{
    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: the evaluator needs Round Facts rows, and the engine row source writes none yet";

    [Test]
    [Skip(WaitingOnEngine)]
    public async Task OnAMeasuredDemo_TheEvaluatorBuilds_AndAVerdictLandsInTheTagStore()
    {
        string? folder = Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar);
        string path = Path.Combine(folder ?? "", SuggestedTagsRealDemoTests.Pinned[1].Demo);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"{path} is not in the replays folder");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Sha256 = new string('c', 64),
            Map = parsed.MapName,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });
        new RoundFactsEvaluator(cache, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity())
            .OnParsedOpportunistically(path, parsed);

        using ProposalStore proposals = new(null, cache);
        TagStore tags = new(null);
        SuggestedTagsService service = new(cache, proposals, tags, new SiteRegionStore(null),
            () => DetectorProfile.Default, () => true, () => true);
        service.Evaluate(path, parsed);

        ProposalSet set = service.Load(path);
        await Assert.That(set.Pending.Count).IsGreaterThan(1);
        await Assert.That(service.Accept(path, set.Pending[0].Proposal.Id)).IsTrue();
        await Assert.That(service.Reject(path, set.Pending[1].Proposal.Id)).IsTrue();
        await Assert.That(tags.TryLoad(new string('c', 64))!.Instances.Single().Source).IsEqualTo(TagSources.Suggested);
        await Assert.That(cache.TryGetIndex(path)!.SuggestionCount).IsEqualTo(set.Pending.Count - 2);
    }
}
