#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Suggested Tags steps 4 and 5 below the UI (suggested-tags.md §3.4, §3.5, §7.4, §7.5): the evaluator
///     writing proposals and stamping the record, <c>Wants</c> following the fingerprint, the gate and the
///     opt-in, accept, edit and reject writing the Tag Store with provenance and the verdicts file, a tuning
///     pass that cannot bring a rejection back, the re-match after a re-parse, and the in-memory mode.
/// </summary>
public class SuggestedTagsReviewTests
{
    [Test]
    public async Task Evaluate_WritesTheProposals_AndStampsTheRecordLast()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        ProposalDocument? document = h.Proposals.TryRead(DemoPath);
        DemoCacheIndexEntry? row = h.Cache.TryGetIndex(DemoPath);
        await Assert.That(document).IsNotNull();
        using (Assert.Multiple())
        {
            await Assert.That(document!.OccupancySource).IsEqualTo(ProposalDocument.FromWalk);
            await Assert.That(document.Demo.Sha256).IsEqualTo(Sha);
            await Assert.That(document.Clock.FrameCount).IsEqualTo(2);
            await Assert.That(document.DetectorSet.Fingerprint).IsEqualTo(h.Service.FingerprintFor(SuggestedTagsTestData.Map));
            await Assert.That(document.DetectorSet.Regions).IsEqualTo("learned:1");
            await Assert.That(document.Proposals.Select(p => p.Id)).Contains(ExecuteId);
            await Assert.That(document.Proposals.Single(p => p.Id == ExecuteId).RoundStartTick).IsEqualTo(FreezeEnd);
            await Assert.That(row!.SuggestionsFingerprint).IsEqualTo(document.DetectorSet.Fingerprint);
            await Assert.That(row.SuggestionCount).IsEqualTo(document.Proposals.Count)
                .Because("nothing has a verdict yet, and the index mirrors the pending count");
            await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("the stamp is current");
        }
    }

    [Test]
    public async Task Wants_FollowsTheGate_TheOptIn_TheRows_AndAForcedRequest()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Seed();

        h.Background = false;
        await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("the library sweep is off");
        await Assert.That(h.Service.PendingPaths()).IsEmpty();

        h.Service.Request(DemoPath);
        await Assert.That(h.Service.Wants(DemoPath)).IsTrue().Because("a forced request runs whatever the opt-in");

        h.Enabled = false;
        await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("the gate off stops the evaluator");

        h.Enabled = true;
        h.Background = true;
        h.Cache.Upsert(RoundIndexTestData.ParsedRecord(DemoPath, SuggestedTagsTestData.Map, Sha));
        await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("without Round Facts rows nothing can be detected");
        await Assert.That(h.Service.CanDetect(DemoPath)).IsFalse();
    }

    [Test]
    public async Task TheOpenDemo_IsBuiltOnItsOwnParse_WithTheSweepOff()
    {
        using SuggestedTagsReviewHarness h = new();
        string? open = null;
        SuggestedTagsService service = new(h.Cache, h.Proposals, h.Tags, h.Regions, () => DetectorProfile.Default,
            () => true, () => false, openDemo: () => open, walk: _ => Walk(), utcNow: () => Now);
        h.Seed();

        service.OnParsedOpportunistically(DemoPath, Parse());
        await Assert.That(h.Proposals.TryRead(DemoPath)).IsNull().Because("another demo's tier-2 parse is not the open one");

        open = DemoPath;
        service.OnParsedOpportunistically(DemoPath, Parse());
        await Assert.That(h.Proposals.TryRead(DemoPath)).IsNotNull();
    }

    [Test]
    public async Task AChangedProfile_MarksTheProposalsStale()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        string before = h.Service.FingerprintFor(SuggestedTagsTestData.Map);

        h.Profile = DetectorProfile.Default.With("execute", "lag", 9);

        await Assert.That(h.Service.FingerprintFor(SuggestedTagsTestData.Map)).IsNotEqualTo(before);
        await Assert.That(h.Service.Wants(DemoPath)).IsTrue();
    }

    [Test]
    public async Task Accept_WritesAHumanNamespaceInstance_WithProvenance_ThenTheVerdict()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        int pendingBefore = h.Service.Load(DemoPath).Pending.Count;

        await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsTrue();

        TagDocument? tags = h.Tags.TryLoad(Sha);
        await Assert.That(tags).IsNotNull();
        TagInstance instance = tags!.Instances.Single();
        SuggestionVerdict verdict = h.Tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId];
        using (Assert.Multiple())
        {
            await Assert.That(instance.Code).IsEqualTo("execute");
            await Assert.That(instance.Source).IsEqualTo(TagSources.Suggested);
            await Assert.That(instance.Round).IsEqualTo(1);
            await Assert.That(instance.Labels.Any(l => l.Group == "side" && l.Value == "T")).IsTrue();
            await Assert.That(instance.Labels.Any(l => l.Group == "site" && l.Value == "BombsiteA")).IsTrue();
            await Assert.That((string?)instance.Provenance!["source"]).IsEqualTo(SuggestedTagsService.ProvenanceSource);
            await Assert.That((string?)instance.Provenance["detector"]).IsEqualTo("execute");
            await Assert.That((string?)instance.Provenance["proposalId"]).IsEqualTo(ExecuteId);
            await Assert.That((bool?)instance.Provenance["edited"]).IsEqualTo(false);
            await Assert.That((string?)instance.Provenance["detectorSetFingerprint"])
                .IsEqualTo(h.Service.FingerprintFor(SuggestedTagsTestData.Map));
            await Assert.That(instance.FactsStamp).IsNotNull().Because("the round's facts are stamped as it is made");
            await Assert.That(verdict.Verdict).IsEqualTo(SuggestionVerdicts.Accepted);
            await Assert.That(verdict.TagInstanceId).IsEqualTo(instance.Id);
            await Assert.That(verdict.FrameCount).IsEqualTo(2);
            await Assert.That(h.Service.Load(DemoPath).Pending.Any(e => e.Proposal.Id == ExecuteId)).IsFalse();
            await Assert.That(h.Cache.TryGetIndex(DemoPath)!.SuggestionCount).IsEqualTo(pendingBefore - 1);
            await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsFalse().Because("it is no longer pending");
        }
    }

    [Test]
    public async Task AnEdit_AcceptsWithItsWindowAndLabels_MarkedEdited()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        Dictionary<string, string> labels = new(StringComparer.Ordinal) { ["site"] = "BombsiteA", ["tempo"] = "slow" };

        await Assert.That(h.Service.Accept(DemoPath, ExecuteId, new TagInstanceEdit(1500, 2500, labels, "late"))).IsTrue();

        TagInstance instance = h.Tags.TryLoad(Sha)!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(instance.FromTick).IsEqualTo(1500);
            await Assert.That(instance.ToTick).IsEqualTo(2500);
            await Assert.That(instance.Note).IsEqualTo("late");
            await Assert.That(instance.Labels.Any(l => l.Group == "tempo" && l.Value == "slow")).IsTrue();
            await Assert.That(instance.Labels.Any(l => l.Group == "count")).IsFalse().Because("the edit's labels replace the proposal's");
            await Assert.That((bool?)instance.Provenance!["edited"]).IsEqualTo(true);
            await Assert.That(h.Tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId].Verdict).IsEqualTo(SuggestionVerdicts.Edited);
        }
    }

    [Test]
    public async Task ARejection_SurvivesATuningPass_AndAnAcceptedInstanceIsUntouched()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        const string other = DefaultId;
        await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();
        await Assert.That(h.Service.Accept(DemoPath, other)).IsTrue();
        TagInstance accepted = h.Tags.TryLoad(Sha)!.Instances.Single();

        h.Profile = DetectorProfile.Default.With("execute", "lag", 9);
        await Assert.That(h.Service.Wants(DemoPath)).IsTrue();
        h.Service.Evaluate(DemoPath, Parse());

        ProposalSet set = h.Service.Load(DemoPath);
        using (Assert.Multiple())
        {
            await Assert.That(set.Entries.Any(e => e.Proposal.Id == ExecuteId)).IsTrue().Because("the detector still finds it");
            await Assert.That(set.Pending.Any(e => e.Proposal.Id == ExecuteId)).IsFalse().Because("the rejection stands");
            await Assert.That(set.Pending.Any(e => e.Proposal.Id == other)).IsFalse();
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Single().Id).IsEqualTo(accepted.Id);
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Single().ToTick).IsEqualTo(accepted.ToTick);
        }
    }

    [Test]
    public async Task AReparseThatRenumbersRounds_ReMatchesTheVerdictByTriggerTick()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();

        // The same round, called round 2 by a parse with another frame count: the key changes, the
        // trigger does not.
        h.Build(round: 2, frames: 3);

        ProposalSet set = h.Service.Load(DemoPath);
        ProposalEntry execute = set.Entries.Single(e => e.Proposal.Detector == "execute");
        using (Assert.Multiple())
        {
            await Assert.That(execute.Proposal.Id).IsEqualTo("exec|r2|T|BombsiteA|s=10");
            await Assert.That(execute.IsPending).IsFalse();
            await Assert.That(execute.VerdictKey).IsEqualTo(ExecuteId);
        }
    }

    [Test]
    public async Task TheReMatch_TakesTheNearestWithinFiveSeconds_AndOnlyAcrossParses()
    {
        TagProposal near = Proposal("exec|r2|T|BombsiteA|s=10", "execute", 2, 0, 100, 1000, 0.8);
        TagProposal far = Proposal("exec|r3|T|BombsiteA|s=10", "execute", 3, 0, 100, 5000, 0.8);
        Dictionary<string, SuggestionVerdict> verdicts = new(StringComparer.Ordinal)
        {
            ["exec|r1|T|BombsiteA|s=10"] = new()
            {
                Verdict = SuggestionVerdicts.Rejected, Detector = "execute", Side = 2, TriggerTick = 1000 + 3 * 64,
                FrameCount = 2
            },
            ["exec|r3|T|BombsiteA|s=10"] = new()
            {
                Verdict = SuggestionVerdicts.Rejected, Detector = "execute", Side = 2, TriggerTick = 5000 + 6 * 64,
                FrameCount = 2
            }
        };

        IReadOnlyList<ProposalEntry> sameParse = ProposalVerdictMatch.Resolve([near, far], verdicts, 2, 64);
        IReadOnlyList<ProposalEntry> reparsed = ProposalVerdictMatch.Resolve([near, far], verdicts, 3, 64);

        using (Assert.Multiple())
        {
            await Assert.That(sameParse[0].IsPending).IsTrue().Because("same parse: keys only, and r2 has none");
            await Assert.That(sameParse[1].IsPending).IsFalse().Because("same parse, same key");
            await Assert.That(reparsed[0].VerdictKey).IsEqualTo("exec|r1|T|BombsiteA|s=10").Because("3 s away");
            await Assert.That(reparsed[1].IsPending).IsTrue().Because("6 s away is another proposal");
        }
    }

    [Test]
    public async Task WithNoRoots_ProposalsAndVerdicts_StayInMemory_AcrossAcceptAndReject()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        const string other = DefaultId;

        await Assert.That(h.Service.IsPersistent).IsFalse();
        await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsTrue();
        await Assert.That(h.Service.Reject(DemoPath, other)).IsTrue();

        ProposalSet set = h.Service.Load(DemoPath);
        using (Assert.Multiple())
        {
            await Assert.That(set.IsPersistent).IsFalse();
            await Assert.That(set.Entries.Single(e => e.Proposal.Id == ExecuteId).Verdict!.Verdict)
                .IsEqualTo(SuggestionVerdicts.Accepted);
            await Assert.That(set.Entries.Single(e => e.Proposal.Id == other).Verdict!.Verdict)
                .IsEqualTo(SuggestionVerdicts.Rejected);
            await Assert.That(h.Proposals.PathFor(DemoPath)).IsNull();
            await Assert.That(h.Tags.VerdictsPathFor(Sha)).IsNull();
        }
    }

    [Test]
    public async Task OnDisk_TheFilesLandBesideDemosAndUnderTags_AndLeaveWithTheDemo()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-st-{Guid.NewGuid():N}");
        try
        {
            using SuggestedTagsReviewHarness h = new(Path.Combine(root, "cache"), Path.Combine(root, "tags"));
            h.Build();
            await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();

            string proposals = Path.Combine(root, "cache", "suggestions", DemoCacheStore.StableKey(DemoPath) + ".json");
            string verdicts = Path.Combine(root, "tags", "verdicts", Sha + ".verdicts.json");
            await Assert.That(File.Exists(proposals)).IsTrue();
            await Assert.That(File.Exists(verdicts)).IsTrue();
            await Assert.That(ProposalDocument.TryDeserialize(await File.ReadAllTextAsync(proposals))!.Proposals.Count)
                .IsEqualTo(h.Service.Load(DemoPath).Entries.Count);

            h.Cache.Remove(DemoPath);
            await Assert.That(File.Exists(proposals)).IsFalse().Because("a derived file follows its demo out of the index");
            await Assert.That(File.Exists(verdicts)).IsTrue().Because("verdicts are user truth");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Test]
    public async Task AnUnreadableVerdictsFile_OffersNothing_AndIsNeverOverwritten()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-st-{Guid.NewGuid():N}");
        try
        {
            using SuggestedTagsReviewHarness h = new(tagsRoot: root);
            h.Build();
            string file = h.Tags.VerdictsPathFor(Sha)!;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{ not json");

            ProposalSet set = h.Service.Load(DemoPath);
            using (Assert.Multiple())
            {
                await Assert.That(set.VerdictsUnreadable).IsTrue();
                await Assert.That(set.Pending).IsEmpty();
                await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsFalse();
                await Assert.That(h.Tags.RecordVerdict(Sha, ExecuteId, new SuggestionVerdict())).IsFalse();
                await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("{ not json");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Test]
    public async Task AVerdictKey_KeepsItsFirstVerdict()
    {
        TagStore tags = new(null);

        await Assert.That(tags.RecordVerdict(Sha, ExecuteId, new SuggestionVerdict { Verdict = SuggestionVerdicts.Rejected })).IsTrue();
        await Assert.That(tags.RecordVerdict(Sha, ExecuteId, new SuggestionVerdict { Verdict = SuggestionVerdicts.Accepted })).IsFalse();
        await Assert.That(tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId].Verdict).IsEqualTo(SuggestionVerdicts.Rejected);
    }

    [Test]
    public async Task AFileBuiltForOtherBytes_IsNotOffered()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        h.Seed(sha: new string('b', 64));

        await Assert.That(h.Service.Load(DemoPath).Entries).IsEmpty();
    }
}
