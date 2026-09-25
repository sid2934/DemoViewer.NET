#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A five-step strat for the Step Authoring canvas, a store and session over it, and a hand-cranked
///     transport clock, so a test drives playback without a dispatcher.
/// </summary>
internal static class StratCanvasTestData
{
    public static readonly Guid ArrowId = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001");
    public static readonly Guid TextId = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000003");
    public static readonly Guid BranchId = Guid.Parse("b1b1b1b1-0000-4000-8000-000000000001");

    /// <summary>
    ///     Five steps at 1:50, 1:40, 1:30, 1:20 and 1:10 on a 115 s clock (ticks 320, 960, 1600, 2240, 2880).
    ///     All five of A to E are placed at step 1; A walks east a step at a time; B moves only at step 3 and C
    ///     only at step 5, so the stationary rule is visible between; O1 stands still at step 1. An arrow on
    ///     step 1, a label on step 3, and a branch after step 2 that skips to step 4.
    /// </summary>
    public static StratDocument FiveSteps(Guid? id = null)
    {
        StratDocument document = StratDocument.Create(id ?? Guid.NewGuid(), Team, "de_mirage", "T", "execute", "A exec", Created);
        document.Canvas = new StratCanvas();

        StratStep one = Step(1, 110, "all", "move", "TRamp", "BombsiteA");
        one.Positions =
        [
            Position("A", 0, 0, 90), Position("B", 100, 0), Position("C", 200, 0), Position("D", 300, 0),
            Position("E", 400, 0), Position("O1", -1000, -1000)
        ];
        one.Strokes =
        [
            JsonNode.Parse($$"""{ "id": "{{ArrowId}}", "kind": "Arrow", "colorArgb": 4294951175, "widthWorld": 6, "opacity": 1, "revealOnFadeIn": false, "space": "world", "levelMinZ": 0, "points": [0, 0, 0.5, 600, 0, 0.5], "futureStroke": "kept" }""")!.AsObject()
        ];

        StratStep two = Step(2, 100, "A", "move");
        two.Positions = [Position("A", 600, 0)];

        StratStep three = Step(3, 90, "B", "peek");
        three.Positions = [Position("A", 1200, 0), Position("B", 100, 900)];
        three.Strokes =
        [
            JsonNode.Parse($$"""{ "id": "{{TextId}}", "kind": "Text", "colorArgb": 4294951175, "widthWorld": 6, "opacity": 1, "space": "world", "levelMinZ": 0, "points": [100, 900, 0.5], "text": "peek here" }""")!.AsObject()
        ];

        StratStep four = Step(4, 80, "A", "move");
        four.Positions = [Position("A", 1800, 0)];

        StratStep five = Step(5, 70, "all", "move");
        five.Positions = [Position("A", 2400, 0), Position("C", 200, -600)];

        document.Steps = [one, two, three, four, five];
        document.Branches =
        [
            new StratBranch
            {
                Id = BranchId,
                AfterStepId = two.Id,
                Condition = new BranchCondition { Text = "contact at connector" },
                Target = new BranchTarget { StratId = document.Id, StepId = four.Id }
            }
        ];
        return document;
    }

    public static StepPosition Position(string slot, double x, double y, double? yaw = null) =>
        new() { Slot = slot, X = x, Y = y, LevelMinZ = 0, YawDegrees = yaw };

    /// <summary>An in-memory store holding the strat, and a session with it open and its timers parked.</summary>
    public static (StratStore Store, StratSession Session) Opened(StratDocument document)
    {
        StratStore store = new(null, utcNow: SteppingClock(Created));
        StratSaveResult saved = store.Save(document, [], "created");
        if (!saved.Saved)
        {
            throw new InvalidOperationException(saved.Reason);
        }

        StratSession session = new(store, null, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1),
            IdleCommitDelay = TimeSpan.FromHours(1)
        };
        session.Open(document.Id);
        return (store, session);
    }

    /// <summary>A canvas over the session with no map bundle, a hand-cranked clock and the shipped keymap.</summary>
    public static StratCanvasViewModel Canvas(StratSession session, ManualTicker ticker, StratStore? store = null)
    {
        StratCanvasViewModel canvas = new(session, _ => null, ticker, id => store?.Load(id).Document, () => []);

        // Wide enough that no two steps' markers fold into one glyph, as they would on a zero-width bar.
        canvas.Timeline.PixelWidth = 6000;
        return canvas;
    }
}

/// <summary>A clock the test turns by hand: <see cref="Fire" /> is one display frame.</summary>
internal sealed class ManualTicker : IStratTicker
{
    private Action<double>? _onTick;

    public bool IsRunning => _onTick is not null;

    public IDisposable Start(Action<double> onTick)
    {
        _onTick = onTick;
        return new Stop(this);
    }

    /// <summary>One frame of <paramref name="seconds" /> wall time; false when nothing is running.</summary>
    public bool Fire(double seconds)
    {
        if (_onTick is not { } tick)
        {
            return false;
        }

        tick(seconds);
        return true;
    }

    private sealed class Stop(ManualTicker owner) : IDisposable
    {
        public void Dispose() => owner._onTick = null;
    }
}
