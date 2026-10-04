#region

using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A step's position entries as <c>placed</c> chips: one per entry that beats or stands in for
///     a field, the rest grouped as spots, seen and opponents; each chip's ✕ and "make it the …" are one undo entry; and
///     the checks warn on a departure that forces a leg faster than a run.
/// </summary>
[NotInParallel]
public class StratPlacedChipsTests
{
    private const double Floor = -512;

    private static StepPosition At(string slot, double x, double y, double? yaw = null, bool? observed = null, bool? carried = null) =>
        new() { Slot = slot, X = x, Y = y, LevelMinZ = Floor, YawDegrees = yaw, Observed = observed, Carried = carried };

    private static StratStep Step(double at, string actor, string verb, PlaceRef? to = null, params StepPosition[] positions) =>
        new() { Id = Guid.NewGuid(), AtSeconds = at, Actor = actor, Verb = verb, To = to, Positions = [.. positions] };

    // The spawn seed of a new strat: ten spots on a hold with no place.
    private static StratStep Seed() => Step(115, StratVocabulary.ActorAll, "hold", null,
        At("A", 0, 0), At("B", 60, 0), At("C", 120, 0), At("D", 180, 0), At("E", 240, 0),
        At("O1", 2000, 0), At("O2", 2060, 0, 135), At("O3", 2120, 0), At("O4", 2180, 0), At("O5", 2240, 0));

    private static (StratSession Session, StratEditorViewModel Editor) Open(params StratStep[] steps) => Open(null, steps);

    private static (StratSession Session, StratEditorViewModel Editor) Open(StratOrigin? origin, params StratStep[] steps)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), Team, "de_synthetic", "T", "execute", "chips", Created);
        document.Steps = [.. steps];
        document.Origin = origin;
        (StratStore _, StratSession session) = StratCanvasTestData.Opened(document);
        session.GeometryChecks = StratDepartureCheck.Check;
        StratEditorViewModel editor = new(session);
        editor.Project();
        return (session, editor);
    }

    private static string Json(StratSession session) => StratHistory.ToNode(session.Document!).ToJsonString();

    private static string Texts(StratStepRow row) => string.Join(" | ", row.Placed.Select(c => c.Text));

    [Test]
    public async Task TenSpotsOnAHold_ReadAsSpotsAndOpponents_AndAGroupsClear_IsOneEntry_WhileAnOlderSeedShowsNone()
    {
        StratStep spots = Seed();
        spots.AtSeconds = 110;
        (StratSession session, StratEditorViewModel editor) = Open(Seed(), spots);
        StratStepRow row = editor.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(Texts(editor.Steps[0])).IsEqualTo("").Because("an older file's seed is its start, shown by the Start row");
            await Assert.That(editor.Start.Summary).IsEqualTo("from step 1");
            await Assert.That(Texts(row)).IsEqualTo("spots (5) | opponents (5)");
            await Assert.That(row.HasPlaced).IsTrue();
            await Assert.That(row.Placed[1].Entries.Select(e => e.Text)).Contains("O2 at (2060, 0) 135°")
                .Because("an opponent's angle shows on its entry");
            await Assert.That(row.Placed[0].Entries[0].ConvertLabel).IsEqualTo("Make it the at");
        }

        string before = Json(session);
        row.Placed[1].ClearCommand.Execute(null);
        editor.Project();
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.Document!.Steps[1].Positions.Select(p => p.Slot)).IsEquivalentTo(["A", "B", "C", "D", "E"]);
            await Assert.That(Texts(editor.Steps[1])).IsEqualTo("spots (5)");
        }

        session.Undo();
        editor.Project();
        await Assert.That(Json(session)).IsEqualTo(before);

        // One entry of a group: its own ✕.
        editor.Steps[1].Placed[0].Entries[2].ClearCommand.Execute(null);
        await Assert.That(session.Document!.Steps[1].Positions.Select(p => p.Slot)).DoesNotContain("C");
    }

    [Test]
    public async Task ADepartureOnATravelStep_IsItsOwnCautionChip_AndMakeItLurkAreaOne_MovesThePoint()
    {
        StratStep lurk = Step(108, "E", "lurk", null, At("E", 1374, 412));
        lurk.Lurk = new StepLurk { Areas = ["Ramp"] };
        (StratSession session, StratEditorViewModel editor) = Open(Seed(), lurk);
        StratPlacedChip chip = editor.Steps[1].Placed.Single();
        using (Assert.Multiple())
        {
            await Assert.That(chip.Text).IsEqualTo("E leaves from (1374, 412)");
            await Assert.That(chip.IsCaution).IsTrue();
            await Assert.That(chip.ConvertLabel).IsEqualTo("Make it lurk area 1");
        }

        string before = Json(session);
        chip.ConvertCommand.Execute(null);
        editor.Project();
        StratStep converted = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(converted.Positions).IsEmpty();
            await Assert.That(converted.Lurk!.Areas).IsEquivalentTo(["Ramp"]);
            await Assert.That(converted.Lurk!.AreaPoints!.Single().X).IsEqualTo(1374)
                .Because("with the places unknown the point is stored; points read after places");
            await Assert.That(editor.Steps[1].HasPlaced).IsFalse();
        }

        session.Undo();
        await Assert.That(Json(session)).IsEqualTo(before);
    }

    [Test]
    public async Task APinBesideAPositionVerbsPlace_IsItsOwnChip_AndMakeItTheAt_UsesThePlaceUnderIt()
    {
        StratStep hold = Step(100, "A", "hold", new PlaceRef { Place = "Ramp" }, At("A", 50, 50));
        (StratSession session, StratEditorViewModel editor) = Open(Seed(), hold);
        editor.PlaceAt = (x, y, level) => StratMapFirstTests.SyntheticZones().ResolveOnFloor(x, y, level);
        editor.PlacesKnown = () => true;
        editor.Project();

        StratPlacedChip chip = editor.Steps[1].Placed.Single();
        using (Assert.Multiple())
        {
            await Assert.That(chip.Text).IsEqualTo("A pinned at (50, 50)");
            await Assert.That(chip.IsCaution).IsFalse();
            await Assert.That(chip.ConvertLabel).IsEqualTo("Make it the at");
        }

        string before = Json(session);
        chip.ConvertCommand.Execute(null);
        StratStep converted = session.Document!.Steps[1];
        using (Assert.Multiple())
        {
            await Assert.That(converted.To!.Place).IsEqualTo("Hut");
            await Assert.That(converted.To!.X).IsEqualTo(50);
            await Assert.That(converted.Positions).IsEmpty();
            await Assert.That(session.UndoDepth).IsEqualTo(1);
        }

        session.Undo();
        await Assert.That(Json(session)).IsEqualTo(before);
        editor.Project();

        // Clear instead: the entry goes and the place is used again.
        editor.Steps[1].Placed.Single().ClearCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(session.Document!.Steps[1].Positions).IsEmpty();
            await Assert.That(session.Document!.Steps[1].To!.Place).IsEqualTo("Ramp");
        }
    }

    [Test]
    public async Task CaptureSpots_AreGroupedAsSeen_UnlessTheyDisagreeWithTheStepsPlace_AndCarriedNeverShows()
    {
        StratStep captured = Step(100, StratVocabulary.ActorAll, "hold", null,
            At("A", 10, 10, observed: true), At("B", 20, 10, observed: true), At("C", 30, 10, carried: true));
        StratStep pointed = Step(90, "A", "hold", new PlaceRef { X = 900, Y = 900, LevelMinZ = Floor }, At("A", 10, 10, observed: true));
        (StratSession _, StratEditorViewModel editor) = Open(Seed(), captured, pointed);
        using (Assert.Multiple())
        {
            await Assert.That(Texts(editor.Steps[1])).IsEqualTo("seen (2)");
            await Assert.That(Texts(editor.Steps[2])).IsEqualTo("A seen at (10, 10)");
        }
    }

    [Test]
    public async Task ALegacyCapture_ReadsEveryEntryAsSeen_AndIsNotZipChecked()
    {
        // A capture written before the observed mark: an origin, and no observed key anywhere.
        StratStep lurk = Step(114, "E", "lurk", null, At("E", 2140, 0));
        lurk.Lurk = new StepLurk { Areas = ["Middle"] };
        (StratSession session, StratEditorViewModel editor) = Open(new StratOrigin { DemoSha256 = "ab", Round = 3 }, Seed(), lurk);
        using (Assert.Multiple())
        {
            await Assert.That(Texts(editor.Steps[0])).IsEqualTo("seen (5) | opponents (5)");
            await Assert.That(Texts(editor.Steps[1])).IsEqualTo("E seen at (2140, 0)");
            await Assert.That(session.Issues.Any(i => StratDepartureCheck.PositionOf(i.Field) is not null)).IsFalse();
        }
    }

    [Test]
    public async Task ADepartureThatForcesALegFasterThanARun_Warns_TheExecuteBZip()
    {
        // Execute B's shape: everyone at spawn on the seed hold at 1:55, E's lurk a second later with a dragged departure
        // across the map.
        StratStep lurk = Step(114, "E", "lurk", null, At("E", 2140, 0));
        lurk.Lurk = new StepLurk { Areas = ["Middle"], Rotate = new LurkRotate { AtSeconds = 70, To = new PlaceRef { Place = "BombsiteB" } } };
        StratStep slow = Step(100, "A", "move", new PlaceRef { Place = "Ramp" }, At("A", 300, 0));
        (StratSession session, StratEditorViewModel editor) = Open(Seed(), lurk, slow);

        IReadOnlyList<StratIssue> issues = session.Issues;
        StratIssue zip = issues.Single(i => i.Field.StartsWith("/steps/1/positions/", StringComparison.Ordinal));
        using (Assert.Multiple())
        {
            await Assert.That(zip.Severity).IsEqualTo(StratIssueSeverity.Warning);
            await Assert.That(zip.Field).IsEqualTo("/steps/1/positions/0");
            await Assert.That(zip.Message).StartsWith("E would cross 1900 u in 1.0 s");
            await Assert.That(issues.Any(i => i.Field.StartsWith("/steps/2/positions/", StringComparison.Ordinal))).IsFalse()
                .Because("300 u in 15 s is a walk");
            await Assert.That(editor.Steps[1].Placed.Single().ToolTip).StartsWith("E would cross 1900 u in 1.0 s");
        }
    }
}
