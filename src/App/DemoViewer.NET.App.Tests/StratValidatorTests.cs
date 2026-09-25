#region

using System.Text.Json;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The validator (strat-model.md §3.10 plus overview correction 17): one case per refuse row and per warn
///     row, the info rows, and a clean strat that raises nothing.
/// </summary>
public class StratValidatorTests
{
    private static readonly CalloutResolver Mirage = new(CanonicalPlaces.Embedded("de_mirage"));

    private static async Task Expect(StratDocument document, StratIssueSeverity severity, string field, CalloutResolver? places = null,
        IReadOnlyList<StratIndexEntry>? index = null)
    {
        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, places, index);
        await Assert.That(issues.Any(i => i.Severity == severity && i.Field == field)).IsTrue()
            .Because($"expected a {severity} at '{field}', got: {string.Join("; ", issues.Select(i => $"{i.Severity} {i.Field} {i.Message}"))}");
    }

    [Test]
    public async Task AValidStrat_RaisesNothing()
    {
        await Assert.That(StratValidator.Validate(Minimal(), Mirage, []).Count).IsEqualTo(0);
    }

    [Test]
    public async Task HeaderRefusals()
    {
        StratDocument schema = Minimal();
        schema.SchemaVersion = 0;
        await Expect(schema, StratIssueSeverity.Refusal, "/schemaVersion");

        StratDocument id = Minimal(Guid.Empty);
        await Expect(id, StratIssueSeverity.Refusal, "/id");

        StratDocument map = Minimal();
        map.Map = " ";
        await Expect(map, StratIssueSeverity.Refusal, "/map");

        StratDocument side = Minimal();
        side.Side = "t";
        await Expect(side, StratIssueSeverity.Refusal, "/side");

        StratDocument status = Minimal();
        status.Status = "Done";
        await Expect(status, StratIssueSeverity.Refusal, "/status");

        StratDocument numeric = Minimal();
        numeric.Status = "2";
        await Expect(numeric, StratIssueSeverity.Refusal, "/status");
    }

    [Test]
    public async Task SlotsOutOfOrderOrShort_AreRefused()
    {
        StratDocument order = Minimal();
        (order.Slots[0], order.Slots[1]) = (order.Slots[1], order.Slots[0]);
        await Expect(order, StratIssueSeverity.Refusal, "/slots");

        StratDocument four = Minimal();
        four.Slots.RemoveAt(4);
        await Expect(four, StratIssueSeverity.Refusal, "/slots");
    }

    [Test]
    public async Task StepRefusals()
    {
        StratDocument increasing = Minimal();
        increasing.Steps[1].AtSeconds = 95;
        await Expect(increasing, StratIssueSeverity.Refusal, "/steps/1/atSeconds");

        StratDocument actor = Minimal();
        actor.Steps[0].Actor = "F";
        await Expect(actor, StratIssueSeverity.Refusal, "/steps/0/actor");

        StratDocument verb = Minimal();
        verb.Steps[0].Verb = "teleport";
        await Expect(verb, StratIssueSeverity.Refusal, "/steps/0/verb");

        StratDocument duplicate = Minimal();
        duplicate.Steps[1].Id = duplicate.Steps[0].Id;
        await Expect(duplicate, StratIssueSeverity.Refusal, "/steps/1/id");

        StratDocument shared = Minimal();
        shared.Steps[1].AtSeconds = shared.Steps[0].AtSeconds;
        await Assert.That(StratValidator.Validate(shared).Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse()
            .Because("two steps may share a time");
    }

    [Test]
    public async Task BranchRefusals()
    {
        StratDocument after = Minimal();
        after.Branches.Add(new StratBranch { Id = Guid.NewGuid(), AfterStepId = Guid.NewGuid(), Target = new BranchTarget { StratId = Guid.NewGuid() } });
        await Expect(after, StratIssueSeverity.Refusal, "/branches/0/afterStepId");

        StratDocument missingStep = Minimal();
        missingStep.Branches.Add(new StratBranch
        {
            Id = Guid.NewGuid(), AfterStepId = StepId(1), Target = new BranchTarget { StratId = missingStep.Id, StepId = Guid.NewGuid() }
        });
        await Expect(missingStep, StratIssueSeverity.Refusal, "/branches/0/target/stepId");

        StratDocument backwards = Minimal();
        backwards.Branches.Add(new StratBranch
        {
            Id = Guid.NewGuid(), AfterStepId = StepId(2), Target = new BranchTarget { StratId = backwards.Id, StepId = StepId(1) }
        });
        await Expect(backwards, StratIssueSeverity.Refusal, "/branches/0/target/stepId");
    }

    [Test]
    public async Task CalloutRefusals_DuplicateAndEmptyPlace()
    {
        CalloutTable table = new()
        {
            Map = "de_mirage",
            Aliases =
            [
                new CalloutAlias { Alias = "A ramp", Place = "TRamp" },
                new CalloutAlias { Alias = "a-ramp", Place = "TRamp" },
                new CalloutAlias { Alias = "window", Place = "" }
            ]
        };

        IReadOnlyList<StratIssue> issues = StratValidator.ValidateCallouts(table, Mirage);
        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i is { Severity: StratIssueSeverity.Refusal, Field: "/aliases/1/alias" })).IsTrue()
                .Because("aliases fold before comparing, so 'a-ramp' is 'A ramp'");
            await Assert.That(issues.Any(i => i is { Severity: StratIssueSeverity.Refusal, Field: "/aliases/2/place" })).IsTrue();
        }
    }

    [Test]
    public async Task CalloutWarnings_UnknownPlaceAndVanishedName()
    {
        CalloutTable table = new()
        {
            Map = "de_mirage",
            Canonical = new CalloutCanonicalSnapshot { Source = "embedded", Names = ["TRamp", "Kitchen"] },
            Aliases = [new CalloutAlias { Alias = "kitchen", Place = "Kitchen" }]
        };

        IReadOnlyList<StratIssue> issues = StratValidator.ValidateCallouts(table, Mirage);
        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i is { Severity: StratIssueSeverity.Warning, Field: "/aliases/0/place" })).IsTrue();
            await Assert.That(issues.Any(i => i is { Severity: StratIssueSeverity.Warning, Field: "/canonical/names" })).IsTrue();
            await Assert.That(issues.Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
        }
    }

    [Test]
    public async Task TypeAndApplicabilityWarn_NeverRefuse()
    {
        StratDocument unknownType = Minimal();
        unknownType.Type = "blitz";
        await Expect(unknownType, StratIssueSeverity.Warning, "/type");

        StratDocument ctExecute = Minimal();
        ctExecute.Side = "CT";
        await Expect(ctExecute, StratIssueSeverity.Warning, "/side");

        StratDocument noSite = Minimal();
        noSite.TargetSite = null;
        await Expect(noSite, StratIssueSeverity.Warning, "/targetSite");

        StratDocument defaultWithSite = Minimal();
        defaultWithSite.Type = "default";
        await Expect(defaultWithSite, StratIssueSeverity.Warning, "/targetSite");

        StratDocument siteC = Minimal();
        siteC.TargetSite = "C";
        await Expect(siteC, StratIssueSeverity.Warning, "/targetSite");

        StratDocument setup = Minimal();
        setup.Side = "CT";
        setup.Type = "setup";
        await Assert.That(StratValidator.Validate(setup).Count).IsEqualTo(0).Because("a setup may favour a site");

        foreach (StratDocument document in new[] { unknownType, ctExecute, noSite, defaultWithSite, siteC })
        {
            await Assert.That(StratValidator.Validate(document).Any(i => i.Severity == StratIssueSeverity.Refusal)).IsFalse();
        }
    }

    [Test]
    public async Task EconomyOutsideRoundFactsVocabulary_Warns_AndSemiIsAdmitted()
    {
        StratDocument semi = Minimal();
        semi.Economy = "semi";
        await Assert.That(StratValidator.Validate(semi).Count).IsEqualTo(0).Because("overview correction 10 adopts Round Facts' five plus any");

        StratDocument bad = Minimal();
        bad.Economy = "half";
        await Expect(bad, StratIssueSeverity.Warning, "/economy");
    }

    [Test]
    public async Task APlaceNotOnTheMap_Warns()
    {
        StratDocument from = Minimal();
        from.Steps[0].From = new PlaceRef { Place = "Kitchen" };
        await Expect(from, StratIssueSeverity.Warning, "/steps/0/from/place", Mirage);

        StratDocument to = Minimal();
        to.Steps[0].To = new PlaceRef { Place = "Kitchen" };
        await Expect(to, StratIssueSeverity.Warning, "/steps/0/to/place", Mirage);

        StratDocument landing = Minimal();
        landing.Steps[0].Utility = new UtilityRef { Kind = "smoke", Landing = new UtilityLanding { Place = "Kitchen" } };
        await Expect(landing, StratIssueSeverity.Warning, "/steps/0/utility/landing/place", Mirage);
    }

    [Test]
    public async Task AtSecondsOutsideTheRoundClock_Warns()
    {
        StratDocument late = Minimal();
        late.Steps[0].AtSeconds = 116;
        late.Steps[1].AtSeconds = 100;
        await Expect(late, StratIssueSeverity.Warning, "/steps/0/atSeconds");

        StratDocument afterTimer = Minimal();
        afterTimer.Steps[1].AtSeconds = -61;
        await Expect(afterTimer, StratIssueSeverity.Warning, "/steps/1/atSeconds");
    }

    [Test]
    public async Task ABranchToAStratOutsideThisBookAndMap_Warns()
    {
        StratDocument document = Minimal();
        Guid elsewhere = Guid.NewGuid();
        document.Branches.Add(new StratBranch { Id = Guid.NewGuid(), AfterStepId = StepId(1), Target = new BranchTarget { StratId = elsewhere } });

        StratIndexEntry otherMap = new() { Id = elsewhere, Owner = Team, Map = "de_nuke" };
        await Expect(document, StratIssueSeverity.Warning, "/branches/0/target/stratId", index: [otherMap]);

        StratIndexEntry sameBook = new() { Id = elsewhere, Owner = Team, Map = "de_mirage" };
        await Assert.That(StratValidator.Validate(document, index: [sameBook]).Count).IsEqualTo(0);
    }

    [Test]
    public async Task CorrectionSeventeen_OpponentSlotsAdmitted_PathInterpolationWarns()
    {
        StratDocument document = Minimal();
        document.Steps[0].Positions = [new StepPosition { Slot = "O3", X = 1, Y = 2, LevelMinZ = 0 }];
        await Assert.That(StratValidator.Validate(document).Count).IsEqualTo(0);

        document.Steps[0].Positions.Add(new StepPosition { Slot = "Z9" });
        document.Steps[0].Interpolation = StratVocabulary.InterpolationPathReserved;
        await Expect(document, StratIssueSeverity.Warning, "/steps/0/positions/1/slot");
        await Expect(document, StratIssueSeverity.Warning, "/steps/0/interpolation");
    }

    [Test]
    public async Task InfoRows_LineupAndUnknownFields()
    {
        StratDocument document = Minimal();
        document.Steps[0].Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        document.Extra = new Dictionary<string, JsonElement> { ["zeta"] = JsonDocument.Parse("1").RootElement.Clone(), ["alpha"] = JsonDocument.Parse("2").RootElement.Clone() };
        document.Steps[1].Extra = new Dictionary<string, JsonElement> { ["speed"] = JsonDocument.Parse("3").RootElement.Clone() };

        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document);
        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i is { Severity: StratIssueSeverity.Info, Field: "/steps/0/utility/lineupId" })).IsTrue();
            await Assert.That(issues.Single(i => i.Field == "").Message).IsEqualTo("unknown fields kept: alpha, zeta");
            await Assert.That(issues.Single(i => i.Field == "/steps/1").Message).IsEqualTo("unknown fields kept: speed");
            await Assert.That(issues.All(i => i.Severity == StratIssueSeverity.Info)).IsTrue();
        }
    }

    [Test]
    public async Task AMoveWithNoDestination_Warns()
    {
        StratDocument document = Minimal();
        document.Steps[0].Verb = "move";
        document.Steps[0].To = new PlaceRef { Place = "" };
        await Expect(document, StratIssueSeverity.Warning, "/steps/0/to");
    }
}
