#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.ViewModels.Settings;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Settings tuning section's VM (suggested-tags.md §3.7, step 6): hides itself with no harness
///     (the browser host, per §3.8), otherwise builds one parameter row per detector parameter and the
///     preview/save round trip.
/// </summary>
public class SuggestedTagsTuningViewModelTests
{
    [Test]
    public async Task WithNoHarness_TheSectionHidesItself()
    {
        SuggestedTagsTuningViewModel vm = new(null, null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.CanManageTuning).IsFalse();
            await Assert.That(vm.DetectorRows).IsEmpty();
            await Assert.That(vm.ParameterRows).IsEmpty();
        }
    }

    [Test]
    public async Task OnTheBrowserHost_TheSectionHidesItself_EvenWithAHarness()
    {
        using SuggestedTagsReviewHarness h = new();
        SuggestedTagsTuningService tuning = new(h.Cache, h.Service, h.Tags, h.Regions, _ => Parse());
        ProfileStore profiles = new(null);

        SuggestedTagsTuningViewModel vm = new(tuning, profiles, isBrowser: true);

        await Assert.That(vm.CanManageTuning).IsFalse();
    }

    [Test]
    public async Task WithAHarness_BuildsOneParameterRowPerDetectorParameter_AndTheStoredTable()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        await Assert.That(h.Service.Accept(DemoPath, h.Service.Load(DemoPath).Entries
            .Single(e => e.Proposal.Detector == "execute").Proposal.Id)).IsTrue();
        SuggestedTagsTuningService tuning = new(h.Cache, h.Service, h.Tags, h.Regions, _ => Parse());
        ProfileStore profiles = new(null);

        SuggestedTagsTuningViewModel vm = new(tuning, profiles);

        int expectedParameters = ProposalDetection.All.Sum(d => d.Parameters.Count);
        using (Assert.Multiple())
        {
            await Assert.That(vm.CanManageTuning).IsTrue();
            await Assert.That(vm.ParameterRows.Count).IsEqualTo(expectedParameters);
            await Assert.That(vm.DetectorRows.Select(r => r.Detector)).IsEquivalentTo(ProposalDetection.All.Select(d => d.Id));
            await Assert.That(vm.DetectorRows.Single(r => r.Detector == "execute").Made).IsEqualTo(1)
                .Because("the demo now has a verdict, so its entries count toward the history table");
            await Assert.That(vm.IsDirty).IsFalse();
        }
    }

    [Test]
    public async Task EditingAParameter_MarksDirty_AndSaveWritesThroughTheProfileStore()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        SuggestedTagsTuningService tuning = new(h.Cache, h.Service, h.Tags, h.Regions, _ => Parse());
        ProfileStore profiles = new(null);
        SuggestedTagsTuningViewModel vm = new(tuning, profiles);

        TuningParameterRow row = vm.ParameterRows.Single(r => r.Detector == "execute" && r.Name == "N");
        row.Value = row.ShippedDefault + 1;

        await Assert.That(vm.IsDirty).IsTrue();

        vm.SaveCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsDirty).IsFalse();
            await Assert.That(profiles.Current.Get("execute", "N")).IsEqualTo(row.ShippedDefault + 1);
        }
    }

    [Test]
    public async Task ResetToShipped_PutsEveryParameterBackToItsDefault_WithoutSaving()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        SuggestedTagsTuningService tuning = new(h.Cache, h.Service, h.Tags, h.Regions, _ => Parse());
        ProfileStore profiles = new(null);
        SuggestedTagsTuningViewModel vm = new(tuning, profiles);

        foreach (TuningParameterRow row in vm.ParameterRows)
        {
            row.Value += 7;
        }

        vm.ResetToShippedCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.ParameterRows.All(r => r.Value == r.ShippedDefault)).IsTrue();
            await Assert.That(profiles.Current.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson())
                .Because("reset only touches the edited rows, never the saved profile");
        }
    }

    [Test]
    public async Task Preview_UpdatesRecallAndPrecision_ButNeverTheVerdictCounts()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        ProposalSet set = h.Service.Load(DemoPath);
        TagProposal execute = set.Entries.Single(e => e.Proposal.Detector == "execute").Proposal;
        await Assert.That(h.Service.Accept(DemoPath, execute.Id)).IsTrue();
        h.Tags.Append(new DemoIdentity(Sha, "review.dem", 10),
            new ClockIdentity(ClockIdentity.DvFrameClock, 64, 2, 1, 20000),
            new TagInstance
            {
                Id = Guid.NewGuid(), Code = execute.Code, Round = execute.Round,
                FromTick = execute.FromTick, ToTick = execute.ToTick,
                Source = TagSources.Human, CreatedUtc = Now, ModifiedUtc = Now
            });

        SuggestedTagsTuningService tuning = new(h.Cache, h.Service, h.Tags, h.Regions, _ => Parse());
        ProfileStore profiles = new(null);
        SuggestedTagsTuningViewModel vm = new(tuning, profiles);

        await vm.PreviewCommand.ExecuteAsync(null);

        TuningDetectorRow row = vm.DetectorRows.Single(r => r.Detector == "execute");
        using (Assert.Multiple())
        {
            await Assert.That(row.RecallText).IsEqualTo("100%");
            await Assert.That(row.PrecisionText).IsEqualTo("100%");
            await Assert.That(row.Accepted).IsEqualTo(1);
            await Assert.That(vm.IsBusy).IsFalse();
        }
    }
}
