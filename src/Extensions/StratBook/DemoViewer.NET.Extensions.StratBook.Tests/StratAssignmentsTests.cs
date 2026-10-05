#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lines on a step (<c>assignments</c>): absent on every file written before them, round-tripped when present,
///     read through <see cref="StratStepLines" />, and checked by the validator with per-line pointers.
/// </summary>
public class StratAssignmentsTests
{
    private const string FixtureName = "schema-v1.assignments.dvstrat.json";

    internal static readonly Guid LinesId = Guid.Parse("5d0c7e1a-2b3c-4d5e-8f60-718293a4b5c6");

    private static readonly CalloutResolver Mirage = new(CanonicalPlaces.Embedded("de_mirage"));

    private static bool Updating => Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1";

    internal static PlaceRef Place(string place) => new() { Place = place };

    /// <summary>The minimal strat plus a 1:05 move that sends B, C and D to three places, each watching something.</summary>
    internal static StratDocument WithLines()
    {
        StratDocument document = Minimal(LinesId);
        StratStep step = Step(3, 65, StratVocabulary.ActorAll, "move", "TRamp");
        step.Assignments =
        [
            new StepAssignment { Slot = "B", To = Place("PalaceInterior"), Watch = new StepWatch { Places = ["BombsiteA", "CTSpawn"] } },
            new StepAssignment { Slot = "C", To = Place("Connector"), Watch = new StepWatch { Places = ["Stairs"] } },
            new StepAssignment
            {
                Slot = "D", To = Place("Stairs"), Watch = new StepWatch { Places = ["TRamp"], YawDegrees = 135 },
                Extra = new Dictionary<string, JsonElement> { ["futureLine"] = JsonDocument.Parse("1").RootElement.Clone() }
            }
        ];
        document.Steps.Add(step);
        return document;
    }

    private static string Issues(StratDocument document) =>
        string.Join("; ", StratValidator.Validate(document, Mirage).Select(i => $"{i.Severity} {i.Field}"));

    private static async Task Expect(StratDocument document, StratIssueSeverity severity, string field)
    {
        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, Mirage);
        await Assert.That(issues.Any(i => i.Severity == severity && i.Field == field)).IsTrue()
            .Because($"expected a {severity} at '{field}', got: {Issues(document)}");
    }

    [Test]
    public async Task AFileWithoutLines_NeverWritesTheField()
    {
        string json = StratStore.Serialize(SchemaSample());
        StratDocument loaded = JsonSerializer.Deserialize(json, StratJsonContext.Default.StratDocument)!;
        using (Assert.Multiple())
        {
            await Assert.That(json.Contains("assignments", StringComparison.Ordinal)).IsFalse();
            await Assert.That(loaded.Steps.All(s => s.Assignments is null)).IsTrue();
            await Assert.That(StratStore.Serialize(loaded)).IsEqualTo(json);
        }
    }

    [Test]
    public async Task Lines_RoundTripThroughTheStore_MatchingTheCheckedInFixture()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "tests", "fixtures", "strats", FixtureName);
        if (Updating)
        {
            File.WriteAllText(path, StratStore.Serialize(WithLines()) + "\n");
        }

        string original = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        string root = TempRoot();
        try
        {
            string file = Path.Combine(StratStore.FolderFor(root, Team, "de_mirage"), LinesId + StratStore.StratExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, original);

            StratLoadResult loaded = new StratStore(root).Load(LinesId);
            StratStep step = loaded.Document!.Steps[2];
            using (Assert.Multiple())
            {
                await Assert.That(StratStore.Serialize(WithLines()) + "\n").IsEqualTo(original).Because("this build writes the committed shape");
                await Assert.That(StratStore.Serialize(loaded.Document) + "\n").IsEqualTo(original).Because("load and save is field-identical");
                await Assert.That(step.Assignments!.Select(a => a.Slot)).IsEquivalentTo(["B", "C", "D"]);
                await Assert.That(step.Assignments![0].Watch!.Places).IsEquivalentTo(["BombsiteA", "CTSpawn"]);
                await Assert.That(step.Assignments![2].Watch!.YawDegrees).IsEqualTo(135);
                await Assert.That(step.Assignments![2].Extra!.ContainsKey("futureLine")).IsTrue();
                await Assert.That(loaded.Issues.Where(i => i.Severity != StratIssueSeverity.Info)).IsEmpty();
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task TheReader_TreatsAPlainStepAsItAlwaysWas_AndAStepWithLinesByItsLines()
    {
        StratDocument document = WithLines();
        StratStep plain = document.Steps[0];
        StratStep all = Step(9, 60, StratVocabulary.ActorAll, "hold", to: "BombsiteA");
        StratStep lines = document.Steps[2];
        using (Assert.Multiple())
        {
            await Assert.That(StratStepLines.Of(plain).Select(l => l.Slot)).IsEquivalentTo(["B"]);
            await Assert.That(StratStepLines.ToFor(plain, "B")).IsEqualTo("BombsiteA");
            await Assert.That(StratStepLines.Involves(plain, "C")).IsFalse();
            await Assert.That(StratStepLines.Of(all)).IsEmpty();
            await Assert.That(StratStepLines.Involves(all, "E")).IsTrue();
            await Assert.That(StratStepLines.ToFor(all, "E")).IsEqualTo("BombsiteA");

            await Assert.That(StratStepLines.Involves(lines, "C")).IsTrue();
            await Assert.That(StratStepLines.Involves(lines, "A")).IsFalse().Because("actor 'all' is only a summary");
            await Assert.That(StratStepLines.ToFor(lines, "B")).IsEqualTo("PalaceInterior");
            await Assert.That(StratStepLines.ToFor(lines, "A")).IsNull();
            await Assert.That(StratStepLines.ActorOf(lines)).IsEqualTo(StratVocabulary.ActorAll);
            await Assert.That(StratStepLines.ActorFor([lines.Assignments![1]])).IsEqualTo("C");
        }
    }

    [Test]
    public async Task AStepWithLines_ValidatesClean()
    {
        await Assert.That(Issues(WithLines())).IsEqualTo("");
    }

    [Test]
    public async Task UnknownOrRepeatedSlots_AreRefused()
    {
        StratDocument unknown = WithLines();
        unknown.Steps[2].Assignments![0].Slot = "O1";
        await Expect(unknown, StratIssueSeverity.Refusal, "/steps/2/assignments/0/slot");

        StratDocument repeated = WithLines();
        repeated.Steps[2].Assignments![2].Slot = "B";
        await Expect(repeated, StratIssueSeverity.Refusal, "/steps/2/assignments/2/slot");
    }

    [Test]
    public async Task UnknownPlaces_InALinesToOrWatch_Warn_AtTheLinesPointer()
    {
        StratDocument to = WithLines();
        to.Steps[2].Assignments![1].To = Place("Nowhere");
        await Expect(to, StratIssueSeverity.Warning, "/steps/2/assignments/1/to/place");

        StratDocument watch = WithLines();
        watch.Steps[2].Assignments![0].Watch!.Places[1] = "Nowhere";
        await Expect(watch, StratIssueSeverity.Warning, "/steps/2/assignments/0/watch/places/1");
    }

    [Test]
    public async Task AMoveLineWithoutAPlace_Warns_AndTheStepLevelMoveCheckStandsDown()
    {
        StratDocument document = WithLines();
        document.Steps[2].Assignments![1].To = null;
        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, Mirage);
        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i is { Severity: StratIssueSeverity.Warning, Field: "/steps/2/assignments/1/to" })).IsTrue();
            await Assert.That(issues.Any(i => i.Field == "/steps/2/to")).IsFalse().Because("a step with lines has no step-level to");
        }

        StratDocument hold = WithLines();
        hold.Steps[2].Verb = "hold";
        hold.Steps[2].Assignments![1].To = null;
        await Assert.That(StratValidator.Validate(hold, Mirage).Any(i => i.Field.StartsWith("/steps/2/assignments/1/to", StringComparison.Ordinal)))
            .IsFalse().Because("only a move needs a place");
    }

    [Test]
    public async Task TheSummaryActor_AndAStrayStepTo_Warn()
    {
        StratDocument actor = WithLines();
        actor.Steps[2].Actor = "B";
        await Expect(actor, StratIssueSeverity.Warning, "/steps/2/actor");

        StratDocument single = WithLines();
        single.Steps[2].Assignments!.RemoveRange(1, 2);
        await Expect(single, StratIssueSeverity.Warning, "/steps/2/actor");
        single.Steps[2].Actor = "B";
        await Assert.That(StratValidator.Validate(single, Mirage).Any(i => i.Field == "/steps/2/actor")).IsFalse();

        StratDocument to = WithLines();
        to.Steps[2].To = Place("BombsiteA");
        await Expect(to, StratIssueSeverity.Warning, "/steps/2/to");
    }

    [Test]
    public async Task ALineupThrowByOneLine_NamesItsThrower_AndBySeveral_Warns()
    {
        StratDocument document = WithLines();
        StratStep step = document.Steps[2];
        step.Verb = "throw";
        step.Utility = new UtilityRef { Kind = "smoke", LineupId = Guid.NewGuid() };
        IReadOnlyList<StratIssue> several = StratValidator.Validate(document, Mirage, lineupExists: (_, _) => true);
        await Assert.That(several.Any(i => i.Field == "/steps/2/actor" && i.Message.Contains("thrower", StringComparison.Ordinal))).IsTrue();

        step.Assignments!.RemoveRange(1, 2);
        step.Actor = "B";
        IReadOnlyList<StratIssue> one = StratValidator.Validate(document, Mirage, lineupExists: (_, _) => true);
        await Assert.That(one.Any(i => i.Field == "/steps/2/actor")).IsFalse();
    }
}
