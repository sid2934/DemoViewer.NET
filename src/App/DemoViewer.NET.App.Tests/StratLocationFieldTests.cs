#region

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.StratBook;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratCanvasTestData;
using static DemoViewer.NET.AppTests.StratMapFirstTests;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Location fields: a <see cref="PlaceRef" /> holding a place, a point or both; a watch holding points beside its
///     places; a map pick aimed at any one field; every reader printing a point-only value; the projection facing a
///     point; and the <see cref="PlaceField" /> control's list and keys.
/// </summary>
[NotInParallel]
[Category("Render")]
public partial class StratLocationFieldTests
{
    private const string FixtureName = "schema-v1.locations.dvstrat.json";

    private static readonly Guid LocationsId = Guid.Parse("7e1f2a3b-4c5d-4e6f-8a7b-9c0d1e2f3a4b");

    private static bool Updating => Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1";

    private static readonly CalloutResolver Mirage = new(CanonicalPlaces.Embedded("de_mirage"), new CalloutTable
    {
        Map = "de_mirage",
        Aliases = [new CalloutAlias { Alias = "palace", Place = "PalaceInterior", Primary = true }, new CalloutAlias { Alias = "A ramp", Place = "TRamp" }]
    });

    private static PlaceRef Point(double x, double y, double levelMinZ = -256) => new() { X = x, Y = y, LevelMinZ = levelMinZ };

    /// <summary>The lines sample with a place and point, point-only froms, tos and a landing, and a watched point.</summary>
    internal static StratDocument LocationsSample()
    {
        StratDocument document = StratAssignmentsTests.WithLines();
        document.Id = LocationsId;
        document.Steps[0].To = new PlaceRef { Place = "BombsiteA", X = -300.5, Y = -2100, LevelMinZ = -256 };
        document.Steps[0].Utility = new UtilityRef { Kind = "smoke", Landing = new UtilityLanding { X = -1024.5, Y = -1616, LevelMinZ = -256 } };
        document.Steps[1].From = Point(1234.4, -560.6);
        StratStep lines = document.Steps[2];
        lines.Assignments![0].Watch!.Points = [Point(-900, -1500)];
        lines.Assignments[1].To = Point(-700, -1300);
        StratStep lurk = Step(4, 60, "E", "lurk");
        lurk.Lurk = new StepLurk
        {
            Areas = ["PalaceInterior"], AreaPoints = [Point(-500, -900)],
            Rotate = new LurkRotate { AtSeconds = 40, To = Point(-100, -200) }
        };
        document.Steps.Add(lurk);
        return document;
    }

    // ── Model ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task APlaceRef_WritesThePointAfterThePlace_AndOneWithoutAPoint_WritesNone()
    {
        string both = JsonSerializer.Serialize(new PlaceRef { Place = "Hut", X = 1.5, Y = -2, LevelMinZ = -512 }, StratJsonContext.Default.PlaceRef);
        string place = JsonSerializer.Serialize(new PlaceRef { Place = "Hut" }, StratJsonContext.Default.PlaceRef);
        string sample = StratStore.Serialize(SchemaSample());
        StratDocument reloaded = JsonSerializer.Deserialize(sample, StratJsonContext.Default.StratDocument)!;
        using (Assert.Multiple())
        {
            await Assert.That(both).IsEqualTo("{\n  \"place\": \"Hut\",\n  \"x\": 1.5,\n  \"y\": -2,\n  \"levelMinZ\": -512\n}");
            await Assert.That(place).IsEqualTo("{\n  \"place\": \"Hut\"\n}");
            await Assert.That(reloaded.Steps.All(s => s.From is { X: null, Y: null } or null && s.To is { X: null, Y: null } or null)).IsTrue();
            await Assert.That(StratStore.Serialize(reloaded)).IsEqualTo(sample);
        }
    }

    [Test]
    public async Task Locations_RoundTripThroughTheStore_MatchingTheCheckedInFixture()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "tests", "fixtures", "strats", FixtureName);
        if (Updating)
        {
            File.WriteAllText(path, StratStore.Serialize(LocationsSample()) + "\n");
        }

        string original = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        StratDocument loaded = JsonSerializer.Deserialize(original, StratJsonContext.Default.StratDocument)!;
        using (Assert.Multiple())
        {
            await Assert.That(StratStore.Serialize(loaded) + "\n").IsEqualTo(original);
            await Assert.That(StratStore.Serialize(LocationsSample()) + "\n").IsEqualTo(original);
            await Assert.That(loaded.Steps[2].Assignments![1].To!.Place).IsNull();
            await Assert.That(loaded.Steps[2].Assignments![0].Watch!.Points!.Single().X).IsEqualTo(-900);
            await Assert.That(loaded.Steps[3].Lurk!.AreaPoints!.Single().X).IsEqualTo(-500);
            await Assert.That(StratStepPhrasing.LurkText(loaded.Steps[3].Lurk!, Mirage)).IsEqualTo("palace, (-500, -900); rotate to (-100, -200) at 0:40");
            await Assert.That(StratValidator.Validate(loaded, Mirage).Where(i => i.Severity == StratIssueSeverity.Refusal)).IsEmpty();
        }
    }

    // An older build's shapes: a place and a list of watched place names, with the extension bag every object has.
    private sealed class OldPlaceRef
    {
        public string? Place { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class OldLurk
    {
        public List<string> Areas { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class OldWatch
    {
        public List<string> Places { get; set; } = [];

        public double? YawDegrees { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private static readonly JsonSerializerOptions OldOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    [Test]
    public async Task AnOlderBuild_LoadsAPointAndAWatchedPoint_AndWritesThemBack()
    {
        PlaceRef to = new() { Place = "Hut", X = 10, Y = 20, LevelMinZ = -512 };
        StepWatch watch = new() { Places = ["Hut"], Points = [Point(5, 6)], YawDegrees = 90 };
        string toJson = JsonSerializer.Serialize(to, StratJsonContext.Default.PlaceRef);
        string watchJson = JsonSerializer.Serialize(watch, StratJsonContext.Default.StepWatch);

        OldPlaceRef oldTo = JsonSerializer.Deserialize<OldPlaceRef>(toJson, OldOptions)!;
        OldWatch oldWatch = JsonSerializer.Deserialize<OldWatch>(watchJson, OldOptions)!;
        PlaceRef back = JsonSerializer.Deserialize(JsonSerializer.Serialize(oldTo, OldOptions), StratJsonContext.Default.PlaceRef)!;
        StepWatch watchBack = JsonSerializer.Deserialize(JsonSerializer.Serialize(oldWatch, OldOptions), StratJsonContext.Default.StepWatch)!;
        StepLurk lurk = new() { Areas = ["Hut"], AreaPoints = [Point(7, 8)], Rotate = new LurkRotate { To = Point(1, 2) } };
        OldLurk oldLurk = JsonSerializer.Deserialize<OldLurk>(JsonSerializer.Serialize(lurk, StratJsonContext.Default.StepLurk), OldOptions)!;
        StepLurk lurkBack = JsonSerializer.Deserialize(JsonSerializer.Serialize(oldLurk, OldOptions), StratJsonContext.Default.StepLurk)!;
        using (Assert.Multiple())
        {
            await Assert.That(oldLurk.Areas).IsEquivalentTo(["Hut"]);
            await Assert.That(lurkBack.AreaPoints!.Single().X).IsEqualTo(7);
            await Assert.That(lurkBack.Rotate!.To!.Y).IsEqualTo(2);
            await Assert.That(oldTo.Place).IsEqualTo("Hut");
            await Assert.That(oldWatch.Places).IsEquivalentTo(["Hut"]);
            await Assert.That(StratLocations.Same(back, to)).IsTrue();
            await Assert.That(watchBack.Points!.Single().X).IsEqualTo(5);
            await Assert.That(watchBack.YawDegrees).IsEqualTo(90);
        }
    }

    [Test]
    public async Task APointOnlyDestination_SatisfiesAMove_AndNeitherStillWarns()
    {
        StratDocument document = Minimal();
        document.Steps.Add(Step(3, 70, "D", "move"));
        await Assert.That(StratValidator.Validate(document, Mirage).Any(i => i.Field == "/steps/2/to")).IsTrue();

        document.Steps[2].To = Point(100, 200);
        StratStep lines = Step(4, 60, "all", "move");
        lines.Assignments = [new StepAssignment { Slot = "B", To = Point(1, 2) }, new StepAssignment { Slot = "C" }];
        document.Steps.Add(lines);
        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, Mirage);
        using (Assert.Multiple())
        {
            await Assert.That(issues.Any(i => i.Field.StartsWith("/steps/2/to", StringComparison.Ordinal))).IsFalse();
            await Assert.That(issues.Any(i => i.Field == "/steps/3/assignments/0/to")).IsFalse();
            await Assert.That(issues.Any(i => i.Field == "/steps/3/assignments/1/to")).IsTrue();
        }
    }

    // ── Picks ────────────────────────────────────────────────────────────────────────────────────

    private static (StratSession Session, StratCanvasViewModel Canvas, StratStepSelection Selection) Rig(StratDocument document,
        Func<string, Task<IZonePlaceResolver?>>? places = null)
    {
        (StratStore _, StratSession session) = Opened(document);
        StratCanvasViewModel canvas = new(session, _ => null, new ManualTicker(), null, () => [],
            placesFor: places ?? (_ => Task.FromResult<IZonePlaceResolver?>(SyntheticZones())), post: a => a());
        canvas.Timeline.PixelWidth = 6000;
        StratEditorViewModel editor = new(session);
        editor.Project();
        return (session, canvas, new StratStepSelection(editor, canvas));
    }

    [Test]
    public async Task PickOnMap_ArmsTheExactField_SelectsItsStep_AndAClickWritesPlaceAndPoint_AsOneEntry()
    {
        StratDocument document = FiveSteps();
        (StratSession session, StratCanvasViewModel canvas, StratStepSelection selection) = Rig(document);
        using (canvas)
        using (selection)
        {
            StratLocationField from = new(document.Steps[1].Id, null, StratLocationKind.From);
            await Assert.That(selection.PickOnMap(from)).IsTrue();
            using (Assert.Multiple())
            {
                await Assert.That(canvas.ArmedField).IsEqualTo(from);
                await Assert.That(selection.IsPicking(from)).IsTrue();
                await Assert.That(selection.SelectedStepId).IsEqualTo(document.Steps[1].Id);
                await Assert.That(canvas.SetPlaceText).IsEqualTo("Set “from” on map");
            }

            // A note edit keeps the pick armed; the click then writes the one field.
            session.Apply(PatchOp.ReplaceOp("/steps/1/note", null, JsonValue.Create("go")));
            await Assert.That(canvas.ArmedField).IsEqualTo(from);
            int depth = session.UndoDepth;
            canvas.TryTagPositionAt(Upper, 150, 50);
            PlaceRef written = session.Document!.Steps[1].From!;
            using (Assert.Multiple())
            {
                await Assert.That(written.Place).IsEqualTo("Ramp");
                await Assert.That(written.X).IsEqualTo(150);
                await Assert.That(written.Y).IsEqualTo(50);
                await Assert.That(written.LevelMinZ).IsEqualTo(-512);
                await Assert.That(session.Document!.Steps[1].To).IsNull().Because("only the armed field is written");
                await Assert.That(session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(canvas.ArmedField).IsNull();
            }

            // Again on an armed field cancels it.
            selection.PickOnMap(from);
            await Assert.That(selection.PickOnMap(from)).IsFalse();
            await Assert.That(canvas.IsSettingPlace).IsFalse();
        }
    }

    [Test]
    public async Task AWatchPick_AddsThePlace_OrThePointOutsideEveryPlace_OneEntryEach()
    {
        StratDocument document = FiveSteps();
        (StratSession session, StratCanvasViewModel canvas, StratStepSelection selection) = Rig(document);
        using (canvas)
        using (selection)
        {
            StratLocationField watch = new(document.Steps[1].Id, "A", StratLocationKind.Watch);
            selection.PickOnMap(watch);
            canvas.TryTagPositionAt(Upper, 50, 50);
            selection.PickOnMap(watch);
            canvas.TryTagPositionAt(Upper, 900.4, -560);
            selection.PickOnMap(watch);
            canvas.TryTagPositionAt(Upper, 60, 60);

            StepWatch stored = session.Document!.Steps[1].Assignments!.Single().Watch!;
            using (Assert.Multiple())
            {
                await Assert.That(stored.Places).IsEquivalentTo(["Hut"]);
                await Assert.That(stored.Points!.Single().X).IsEqualTo(900.4);
                await Assert.That(session.UndoDepth).IsEqualTo(2).Because("a second click on a watched place adds nothing");
                await Assert.That(StratStepPhrasing.Watching(session.Document!.Steps[1].Assignments![0], null))
                    .IsEqualTo("watching Hut, (900, -560)");
            }

            // The point is what the token faces once no place comes first.
            session.Apply(PatchOp.ReplaceOp("/steps/1/assignments/0/watch/places", null, new JsonArray()));
            canvas.SelectStep(document.Steps[1].Id);
            StratSceneProjection projection = canvas.Projection!;
            TokenPlacement? a = StratSceneProjection.Placements(projection.Path, null, "A", null)[1];
            await Assert.That(a!.Value.YawDegrees).IsEqualTo((float)Math.Round(Math.Atan2(-560 - 0, 900.4 - 600) * 180 / Math.PI + 360, 2));
        }
    }

    [Test]
    public async Task ALinePick_WritesThatLinesTo_AndTheToolbarPickOnLines_StillNamesTheLine()
    {
        StratDocument document = FiveSteps();
        document.Steps[1].Actor = StratVocabulary.ActorAll;
        document.Steps[1].Assignments = [new StepAssignment { Slot = "B" }, new StepAssignment { Slot = "C" }];
        (StratSession session, StratCanvasViewModel canvas, StratStepSelection selection) = Rig(document);
        using (canvas)
        using (selection)
        {
            selection.PickOnMap(new StratLocationField(document.Steps[1].Id, "C", StratLocationKind.To));
            await Assert.That(canvas.SelectedLineSlot).IsEqualTo("C");
            await Assert.That(canvas.SetPlaceText).IsEqualTo("Set C's “to” on map");
            canvas.TryTagPositionAt(Upper, 5_000, 5_000);
            List<StepAssignment> lines = session.Document!.Steps[1].Assignments!;
            using (Assert.Multiple())
            {
                await Assert.That(lines[1].To!.Place).IsNull();
                await Assert.That(lines[1].To!.X).IsEqualTo(5_000);
                await Assert.That(lines[0].To).IsNull();
            }
        }
    }

    [Test]
    public async Task APickThatNoLongerApplies_Ends_AndReadOnlyArmsNothing()
    {
        StratDocument document = FiveSteps();
        (StratSession session, StratCanvasViewModel canvas, StratStepSelection selection) = Rig(document);
        using (canvas)
        using (selection)
        {
            StratLocationField from = new(document.Steps[1].Id, null, StratLocationKind.From);
            selection.PickOnMap(from);
            session.Apply(PatchOp.ReplaceOp("/steps/1/verb", null, JsonValue.Create("wait")));
            await Assert.That(canvas.ArmedField).IsNull().Because("a wait has no from");
            await Assert.That(canvas.BeginSetPlace(new StratLocationField(document.Steps[1].Id, null, StratLocationKind.Landing))).IsFalse();
            await Assert.That(canvas.BeginSetPlace(new StratLocationField(document.Steps[1].Id, null, StratLocationKind.Watch))).IsFalse();
        }

        (StratStore _, StratSession readOnly) = Opened(FiveSteps());
        using StratCanvasViewModel preview = new(readOnly, _ => null, new ManualTicker(), null, () => [], readOnly: true);
        await Assert.That(preview.BeginSetPlace(new StratLocationField(readOnly.Document!.Steps[1].Id, null, StratLocationKind.From))).IsFalse();
    }

    // ── Readers ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PointOnlyValues_PrintAsCoordinates_OnEverySurface()
    {
        StratDocument document = LocationsSample();
        StratStep throwStep = document.Steps[0], fromStep = document.Steps[1], lines = document.Steps[2];
        string callSheet = StratTextExporter.CallSheet(document, Mirage);
        StratStep pointMove = Step(9, 50, "C", "move");
        pointMove.To = Point(-700.4, -1299.6);
        IReadOnlyList<string> history = StratDiffPhrasing.Describe(
            JsonSerializer.SerializeToNode(document, StratJsonContext.Default.StratDocument),
            [
                PatchOp.ReplaceOp("/steps/1/to", JsonNode.Parse("""{ "place": "BombsiteA" }"""), JsonNode.Parse("""{ "x": 5, "y": 6, "levelMinZ": 0 }""")),
                PatchOp.AddOp("/steps/3", JsonSerializer.SerializeToNode(pointMove, StratJsonContext.Default.StratStep)),
                PatchOp.AddOp("/steps/2/assignments/0/watch/points/1", JsonNode.Parse("""{ "x": 1, "y": -1, "levelMinZ": 0 }"""))
            ], Mirage);

        using (Assert.Multiple())
        {
            await Assert.That(StratStepPhrasing.Phrase(throwStep, Mirage)).IsEqualTo("B throws smoke T Ramp → Bombsite A (-1025, -1616)");
            await Assert.That(StratStepPhrasing.Phrase(fromStep, Mirage)).IsEqualTo("C throws (1234, -561) → Bombsite A");
            await Assert.That(StratStepPhrasing.PhraseLine(lines.Assignments![1], "move", Mirage)).IsEqualTo("C → (-700, -1300), watching Stairs");
            await Assert.That(StratStepPhrasing.PhraseWithLines(lines, Mirage)).Contains("B → palace, watching Bombsite A, CT Spawn, (-900, -1500)");
            await Assert.That(callSheet).Contains("  - C → (-700, -1300), watching Stairs");
            await Assert.That(history[0]).IsEqualTo("C's throw: to Bombsite A → (5, 6)");
            await Assert.That(history[1]).StartsWith("step added: C moves to (-700, -1300)");
            await Assert.That(history[2]).IsEqualTo("B, C, D's move: B watching added: (1, -1)");
        }
    }

    [Test]
    public async Task ThePointWins_OverThePlaceCentre_WhereALocationIs()
    {
        PlaceCentreResolver centres = (place, _) => place == "Hut" ? (50, 50) : null;
        PlaceRef both = new() { Place = "Hut", X = 400, Y = 0, LevelMinZ = 0 };
        StepWatch watchesPoint = new() { Points = [Point(0, 500, 0)] };
        using (Assert.Multiple())
        {
            await Assert.That(StratSceneProjection.Where(both, 0, centres)).IsEqualTo((400d, 0d));
            await Assert.That(StratSceneProjection.Where(new PlaceRef { Place = "Hut" }, 0, centres)).IsEqualTo((50d, 50d));
            await Assert.That(StratSceneProjection.FacingOf(watchesPoint, new TokenPlacement(0, 0, 0, null), null)).IsEqualTo(90f);
        }
    }

    // ── The control's logic ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheList_IsTheMapsCallouts_ByTheOwnersWord_Cached_AndFiltersOnAliases()
    {
        PlaceFieldOptions options = PlaceFieldOptions.For(Mirage);
        using (Assert.Multiple())
        {
            await Assert.That(PlaceFieldOptions.For(Mirage)).IsSameReferenceAs(options);
            await Assert.That(options.All.Select(o => o.Place).Order(StringComparer.Ordinal)).IsEquivalentTo(Mirage.CanonicalNames.Order(StringComparer.Ordinal));
            await Assert.That(options.All.Single(o => o.Place == "PalaceInterior").Display).IsEqualTo("palace");
            await Assert.That(options.Filter("a ramp")[0].Place).IsEqualTo("TRamp").Because("an alias finds its place");
            await Assert.That(options.Filter("bombsite").Select(o => o.Place)).IsEquivalentTo(["BombsiteA", "BombsiteB"]);
            await Assert.That(options.Filter("site").Select(o => o.Place).Take(2)).IsEquivalentTo(["BombsiteA", "BombsiteB"])
                .Because("a name containing the text still lists");
            await Assert.That(options.Filter("").Count).IsEqualTo(options.All.Count);
        }
    }

    [Test]
    public async Task TheModel_MovesAcceptsAndDismisses_AndKeepsAStoredPointWhileItsTextStays()
    {
        PlaceFieldModel single = new() { Options = PlaceFieldOptions.For(Mirage) };
        single.Load([new PlaceRef { Place = "BombsiteA", X = 5, Y = 6, LevelMinZ = 0 }]);
        single.Open();
        await Assert.That(single.Items[single.Highlight].Place).IsEqualTo("BombsiteA").Because("the stored callout is highlighted");
        await Assert.That(single.Commit()).IsNull().Because("the text still reads as the stored value, point and all");

        single.Type("pal");
        using (Assert.Multiple())
        {
            await Assert.That(single.IsOpen).IsTrue();
            await Assert.That(single.Items[0].Place).IsEqualTo("PalaceInterior");
            await Assert.That(single.Highlight).IsEqualTo(0);
        }

        single.Move(1);
        single.Move(-1);
        IReadOnlyList<PlaceRef>? picked = single.Accept();
        using (Assert.Multiple())
        {
            await Assert.That(picked!.Single().Place).IsEqualTo("PalaceInterior");
            await Assert.That(picked!.Single().X).IsNull().Because("another place drops the old point");
            await Assert.That(single.Text).IsEqualTo("palace");
            await Assert.That(single.IsOpen).IsFalse();
            await Assert.That(single.Dismiss()).IsFalse().Because("a closed list does not take Esc");
        }

        PlaceFieldModel point = new() { Options = PlaceFieldOptions.For(Mirage) };
        point.Load([Point(1234.4, -560.6)]);
        using (Assert.Multiple())
        {
            await Assert.That(point.Text).IsEqualTo("(1234, -561)");
            await Assert.That(point.HasPointOnly).IsTrue();
            await Assert.That(point.Commit()).IsNull();
            await Assert.That(point.Clear()).IsEmpty();
        }

        PlaceFieldModel multi = new(true) { Options = PlaceFieldOptions.For(Mirage) };
        multi.Load([new PlaceRef { Place = "Stairs" }, Point(-900, -1500)]);
        multi.Type(multi.Text + ", jun");
        await Assert.That(multi.Items[0].Place).IsEqualTo("Jungle");
        IReadOnlyList<PlaceRef> watched = multi.Accept()!;
        using (Assert.Multiple())
        {
            await Assert.That(multi.Text).IsEqualTo("Stairs, (-900, -1500), Jungle");
            await Assert.That(watched.Select(w => w.Place)).IsEquivalentTo(["Stairs", null, "Jungle"]);
            await Assert.That(watched[1].X).IsEqualTo(-900).Because("the coordinate's text keeps its point");
            await Assert.That(PlaceFieldModel.Split("a, (1, 2), b")).IsEquivalentTo(["a", "(1, 2)", "b"]);
            await Assert.That(PlaceField.ChooseUp(500, 60, 200)).IsTrue();
            await Assert.That(PlaceField.ChooseUp(50, 60, 200)).IsFalse();
            await Assert.That(PlaceField.ChooseUp(500, 300, 200)).IsFalse();
        }
    }

    // ── The control in a window ──────────────────────────────────────────────────────────────────

    // A panel whose tunnel handler does what a step row does with Enter and Esc from a text field.
    private sealed class RowLike : StackPanel
    {
        public int Enters { get; private set; }

        public int Escapes { get; private set; }

        public RowLike() => AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Source is not TextBox)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                Enters++;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Escapes++;
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private static (Window Window, RowLike Row, PlaceField Field) ShowField(VerticalAlignment where, bool multi = false)
    {
        PlaceField field = new() { Callouts = Mirage, IsMulti = multi, Width = 120 };
        RowLike row = new() { VerticalAlignment = where };
        row.Children.Add(field);
        Window window = new() { Width = 315, Height = 600, Content = row };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, row, field);
    }

    private static TextBox BoxOf(PlaceField field) => field.GetVisualDescendants().OfType<TextBox>().First();

    [Test]
    public async Task FocusOpensTheList_TypingFilters_AndEnterAndEscStayWithTheList() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, RowLike row, PlaceField field) = ShowField(VerticalAlignment.Top);
            List<IReadOnlyList<PlaceRef>?> stored = [];
            field.PropertyChanged += (_, e) =>
            {
                if (e.Property == PlaceField.ValueProperty)
                {
                    stored.Add(field.Value);
                }
            };

            BoxOf(field).Focus();
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(field.IsDropDownOpen).IsTrue().Because("a focused location field lists the map's callouts");
                await Assert.That(field.Model.Items.Count).IsEqualTo(Mirage.CanonicalNames.Count);
                await Assert.That(field.OpensUp).IsFalse();
            }

            window.KeyTextInput("pal");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(field.Model.Items[0].Place).IsEqualTo("PalaceInterior");

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(row.Enters).IsEqualTo(0).Because("Enter on the open list picks, it never reaches the row");
                await Assert.That(field.Value!.Single().Place).IsEqualTo("PalaceInterior");
                await Assert.That(BoxOf(field).Text).IsEqualTo("palace");
                await Assert.That(field.IsDropDownOpen).IsFalse();
                await Assert.That(stored.Count).IsEqualTo(1);
            }

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(field.IsDropDownOpen).IsTrue().Because("Down reopens a closed list");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(field.IsDropDownOpen).IsFalse();
                await Assert.That(row.Escapes).IsEqualTo(0).Because("Esc closes the list first");
            }

            // With the list closed the row has its keys back.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.Enters).IsEqualTo(1);
            window.Close();
        });

    private sealed partial class LineLike : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private IReadOnlyList<PlaceRef>? _to;
    }

    [Test]
    public async Task ABoundValue_IsWrittenBackOnAPick_AndEnterWithNoCalloutListed_GoesToTheRow() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, RowLike row, PlaceField field) = ShowField(VerticalAlignment.Top);
            LineLike line = new() { To = [Point(1234.4, -560.6)] };
            int writes = 0;
            line.PropertyChanged += (_, _) => writes++;
            field.DataContext = line;
            field.Bind(PlaceField.ValueProperty, new Avalonia.Data.Binding(nameof(LineLike.To)) { Mode = Avalonia.Data.BindingMode.TwoWay });
            Dispatcher.UIThread.RunJobs();
            await Assert.That(BoxOf(field).Text).IsEqualTo("(1234, -561)");

            BoxOf(field).Focus();
            BoxOf(field).SelectAll();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("jung");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(line.To!.Single().Place).IsEqualTo("Jungle");
                await Assert.That(line.To!.Single().X).IsNull();
                await Assert.That(writes).IsEqualTo(1);
                await Assert.That(row.Enters).IsEqualTo(0);
            }

            // Picking the stored callout again writes nothing.
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(writes).IsEqualTo(1);

            // Text no callout matches shows no list, so Enter is the row's again.
            BoxOf(field).Text = "";
            window.KeyTextInput("zzz");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(field.IsDropDownOpen).IsFalse();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.Enters).IsEqualTo(1);
            window.Close();
        });

    [Test]
    public async Task AtTheRowsFieldWidth_TheTextKeepsMostOfTheField() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, RowLike _, PlaceField field) = ShowField(VerticalAlignment.Top);
            field.Width = 95;
            field.PickCommand = new RelayCommand(() => { });
            field.Value = [Point(1234, -560)];
            Dispatcher.UIThread.RunJobs();
            double text = field.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Single().Bounds.Width;
            await Assert.That(text).IsGreaterThanOrEqualTo(45).Because("both buttons showing still leave room for a callout");
            window.Close();
        });

    [Test]
    public async Task InTheStepRow_AFocusedLocationListsCallouts_PicksByKeyboard_AndRotateToPicksOnTheMap() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = new(new StratStore(null), null, null, false,
                canvasPlaces: _ => Task.FromResult<IZonePlaceResolver?>(new EverywhereIs("BombsiteB")));
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_mirage";
            vm.NewStratCommand.Execute(null);
            StratStep lurk = StratStepEditingTests.Step(100, "E", "lurk");
            lurk.Lurk = new StepLurk { Rotate = new LurkRotate { AtSeconds = 60 } };
            StratStepEditingTests.Seed(vm, lurk);
            Views.StratBook.StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            int index = vm.Editor.Steps.Count - 1;
            int steps = vm.Session.Document!.Steps.Count;
            int depth = vm.Session.UndoDepth;
            Control row = (Control)view.FindControl<ItemsControl>("StepRows")!.ContainerFromIndex(index)!;
            PlaceField areas = row.GetVisualDescendants().OfType<PlaceField>().Single(f => f.Name == "LurkAreasField");
            BoxOf(areas).Focus();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(areas.IsDropDownOpen).IsTrue().Because("a focused location field lists the map's callouts");

            window.KeyTextInput("palace i");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Steps[index].Lurk!.Areas).IsEquivalentTo(["PalaceInterior"]);
                await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(steps).Because("Enter picked from the list");
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            }

            PlaceField rotateTo = row.GetVisualDescendants().OfType<PlaceField>().Single(f => f.Name == "RotateToField");
            rotateTo.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PickButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Canvas.ArmedField).IsEqualTo(vm.Editor.Steps[index].RotateToTarget);
                await Assert.That(rotateTo.IsPicking).IsTrue().Because("the field shows it is the map's target");
            }

            vm.Canvas.TryTagPositionAt(Upper, 700, 800);
            Dispatcher.UIThread.RunJobs();
            PlaceRef to = vm.Session.Document!.Steps[index].Lurk!.Rotate!.To!;
            using (Assert.Multiple())
            {
                await Assert.That(to.Place).IsEqualTo("BombsiteB");
                await Assert.That(to.X).IsEqualTo(700);
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 2);
                await Assert.That(rotateTo.IsPicking).IsFalse();
                await Assert.That(BoxOf(rotateTo).Text).IsEqualTo("Bombsite B");
            }

            window.Close();
        });

    [Test]
    public async Task InTheStepRow_APointOnlyValueReadsAsItsCoordinate_AndTypingAnotherPlaceDropsIt()
    {
        using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
        StratStep move = StratStepEditingTests.Step(100, "all", "move");
        move.To = Point(1234.4, -560.6);
        move.From = new PlaceRef { Place = "TRamp", X = 5, Y = 6, LevelMinZ = 0 };
        move.Assignments = null;
        StratStep hold = StratStepEditingTests.Step(90, "all", "hold");
        hold.Assignments =
        [
            new StepAssignment { Slot = "A", Watch = new StepWatch { Places = ["Stairs"], Points = [Point(-900, -1500)] } },
            new StepAssignment { Slot = "B", To = Point(1, 2) }
        ];
        hold.Actor = StratVocabulary.ActorAll;
        StratStepEditingTests.Seed(vm, move, hold);
        int m = vm.Editor.Steps.Count - 2, h = m + 1;
        StratStepRow moveRow = vm.Editor.Steps[m];
        StratLineRow a = vm.Editor.Steps[h].Lines.Single(l => l.Slot == "A");
        StratLineRow b = vm.Editor.Steps[h].Lines.Single(l => l.Slot == "B");
        using (Assert.Multiple())
        {
            await Assert.That(moveRow.GroupPlaceText).IsEqualTo("(1234, -561)");
            await Assert.That(moveRow.ShowGroupPlace).IsTrue();
            await Assert.That(a.WatchText).IsEqualTo("Stairs, (-900, -1500)");
            await Assert.That(b.PlaceText).IsEqualTo("(1, 2)");
            await Assert.That(b.ShowPlace).IsTrue();
        }

        moveRow.FromText = "Connector";
        a.WatchText = "Stairs, (-900, -1500), Jungle";
        StratDocument document = vm.Session.Document!;
        StepWatch watch = document.Steps[h].Assignments!.Single(l => l.Slot == "A").Watch!;
        using (Assert.Multiple())
        {
            await Assert.That(document.Steps[m].From!.Place).IsEqualTo("Connector");
            await Assert.That(document.Steps[m].From!.X).IsNull().Because("another place drops the point");
            await Assert.That(watch.Places).IsEquivalentTo(["Stairs", "Jungle"]);
            await Assert.That(watch.Points!.Single().X).IsEqualTo(-900).Because("editing the watching keeps its points");
        }

        a.WatchText = "Jungle";
        watch = vm.Session.Document!.Steps[h].Assignments!.Single(l => l.Slot == "A").Watch!;
        await Assert.That(watch.Points).IsNull().Because("a point whose text is removed goes");
    }

    [Test]
    public async Task NearTheBottom_TheListOpensUp() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, RowLike _, PlaceField field) = ShowField(VerticalAlignment.Bottom);
            BoxOf(field).Focus();
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(field.IsDropDownOpen).IsTrue();
                await Assert.That(field.OpensUp).IsTrue();
                await Assert.That(field.GetVisualDescendants().OfType<Popup>().Single().Placement).IsEqualTo(PlacementMode.TopEdgeAlignedLeft);
            }

            window.Close();
        });

    [Test]
    public async Task ThePickButton_ArmsTheCanvasForItsField_AndEscInTheFieldCancels() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            StratDocument document = FiveSteps();
            (StratSession _, StratCanvasViewModel canvas, StratStepSelection selection) = Rig(document);
            using (canvas)
            using (selection)
            {
                StratLocationField target = new(document.Steps[2].Id, "B", StratLocationKind.Watch);
                (Window window, RowLike row, PlaceField field) = ShowField(VerticalAlignment.Top, true);
                field.PickCommand = new RelayCommand<StratLocationField>(f => selection.PickOnMap(f!));
                field.PickCommandParameter = target;
                Dispatcher.UIThread.RunJobs();

                Button pick = field.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PickButton");
                await Assert.That(pick.IsVisible).IsTrue();
                pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                field.IsPicking = selection.IsPicking(target);
                using (Assert.Multiple())
                {
                    await Assert.That(canvas.ArmedField).IsEqualTo(target);
                    await Assert.That(canvas.SelectedLineSlot).IsEqualTo("B");
                    await Assert.That(field.Classes.Contains(":picking")).IsTrue();
                }

                BoxOf(field).Focus();
                Dispatcher.UIThread.RunJobs();
                window.KeyTextInput("b");
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(canvas.ArmedField).IsEqualTo(target).Because("after typing, the first Esc closes the list");
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                using (Assert.Multiple())
                {
                    await Assert.That(canvas.ArmedField).IsNull().Because("Esc in the field cancels its pick");
                    await Assert.That(row.Escapes).IsEqualTo(1);
                }

                // A list that only opened on focus does not hold Esc: one press leaves the field and cancels the pick.
                pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                row.Focusable = true;
                row.Focus();
                BoxOf(field).Focus();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(field.IsDropDownOpen).IsTrue();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                using (Assert.Multiple())
                {
                    await Assert.That(canvas.ArmedField).IsNull();
                    await Assert.That(row.Escapes).IsEqualTo(2);
                }

                window.Close();
            }
        });
}
