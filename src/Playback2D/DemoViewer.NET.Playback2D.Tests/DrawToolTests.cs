#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The draw tool: one gesture, one element, one undo entry, and the anchor chosen at press time and
///     never revisited.
/// </summary>
public class DrawToolTests
{
    [Test]
    public async Task Press_Move_Release_CommitsOneElement_OneUndoEntry()
    {
        Harness h = new();

        h.Press(0, 0);
        h.Move(60, 0);
        h.Move(120, 40);
        h.Release(120, 40);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(h.Document.UndoDepth).IsEqualTo(1);
        await Assert.That(h.Session.Wet.IsActive).IsFalse();
        await Assert.That(h.Document.IsGestureOpen).IsFalse();

        h.Document.Undo();
        await Assert.That(h.Document.Elements).IsEmpty();
    }

    [Test]
    public async Task IntermediatePoints_AreAppended_InOrder()
    {
        Harness h = new();
        h.Press(0, 0);

        InkPoint[] coalesced =
        [
            new(20, 0, 0.5f), new(40, 0, 0.5f), new(60, 0, 0.5f)
        ];
        h.Move(80, 0, coalesced);
        h.Release(80, 0);

        IReadOnlyList<InkPoint> points = h.Document.Elements[0].Points;
        await Assert.That(points.Count).IsEqualTo(5);
        await Assert.That(points[0].X).IsEqualTo(0f);
        await Assert.That(points[1].X).IsEqualTo(20f);
        await Assert.That(points[2].X).IsEqualTo(40f);
        await Assert.That(points[3].X).IsEqualTo(60f);
        await Assert.That(points[4].X).IsEqualTo(80f);
    }

    [Test]
    public async Task MinDistanceFilter_DecimatesJitter()
    {
        Harness h = new();
        h.Session.Style = AnnotationStyle.Default with
        {
            WidthWorld = 20f // spacing filter = 20 * 0.35 = 7 world units
        };

        h.Press(0, 0);
        for (int i = 0; i < 40; i++)
        {
            h.Move(i % 2 == 0 ? 0.5f : -0.5f, 0);
        }

        h.Release(0, 0);

        await Assert.That(h.Document.Elements[0].Points.Count).IsLessThanOrEqualTo(2)
            .Because("a stationary pointer that jitters by half a unit must not store 41 samples");
    }

    [Test]
    public async Task TapWithNoMove_ProducesDot()
    {
        Harness h = new();

        h.Press(10, 10);
        h.Release(10, 10);

        await Assert.That(h.Document.Elements.Count).IsEqualTo(1);
        IReadOnlyList<InkPoint> points = h.Document.Elements[0].Points;
        await Assert.That(points.Count).IsEqualTo(2);
        await Assert.That(points[0]).IsEqualTo(points[1])
            .Because("two coincident samples is what the outliner turns into a circular dot");
    }

    [Test]
    public async Task AnchorMode_On_And_MarkerNearby_ProducesEntitySpaceRef()
    {
        Harness h = new();
        h.Session.AnchorToEntities = true;
        h.Services.Markers.Add(AnnotationFakes.Marker(76561198000000042, 100, 100));

        h.Press(120, 90);
        h.Move(160, 60);
        h.Release(160, 60);

        SpaceRef space = h.Document.Elements[0].Space;
        await Assert.That(space).IsTypeOf<SpaceRef.Entity>();

        SpaceRef.Entity entity = (SpaceRef.Entity)space;
        await Assert.That(entity.SteamId).IsEqualTo(76561198000000042ul);
        await Assert.That(entity.Dx).IsEqualTo(20f);
        await Assert.That(entity.Dy).IsEqualTo(-10f);
    }

    [Test]
    public async Task AnchorMode_On_And_NoMarker_FallsBackToWorldSpaceRef()
    {
        Harness h = new(-384);
        h.Session.AnchorToEntities = true;

        h.Press(0, 0);
        h.Release(20, 0);

        SpaceRef space = h.Document.Elements[0].Space;
        await Assert.That(space).IsTypeOf<SpaceRef.World>();
        await Assert.That(((SpaceRef.World)space).LevelMinZ).IsEqualTo(-384d);
    }

    /// <summary>
    ///     Plan correction 10: a world anchor stamps the QUANTIZED level ZMin, never the raw band edge.
    ///     Otherwise an anchor written before a floor-split rebuild can miss its own level.
    /// </summary>
    [Test]
    public async Task WorldAnchor_StampsTheQuantizedLevelZMin()
    {
        Harness h = new(-390);

        h.Press(0, 0);
        h.Release(20, 0);

        await Assert.That(((SpaceRef.World)h.Document.Elements[0].Space).LevelMinZ)
            .IsEqualTo(MapSpace.QuantizeZ(-390));
    }

    [Test]
    public async Task NewElement_UsesSessionStyleAndEnvelope()
    {
        Harness h = new();
        h.Session.Style = new AnnotationStyle(0xFF00FF00, 12f, 0.75f, true);
        h.Session.DefaultVisibility = EnvelopeMode.Fade;
        h.Session.HoldTicks = 100;
        h.Session.FadeInTicks = 4;
        h.Session.FadeOutTicks = 9;
        h.Services.CurrentTick = 1234;

        h.Press(0, 0);
        h.Release(30, 0);

        AnnotationElement element = h.Document.Elements[0];
        await Assert.That(element.Style.ColorArgb).IsEqualTo(0xFF00FF00u);
        await Assert.That(element.Style.WidthWorld).IsEqualTo(12f);
        await Assert.That(element.Style.Opacity).IsEqualTo(0.75f);
        await Assert.That(element.Style.RevealOnFadeIn).IsTrue();
        await Assert.That(element.Time.FromTick).IsEqualTo(1234);
        await Assert.That(element.Time.UntilTick).IsEqualTo(1334);
        await Assert.That(element.Time.FadeInTicks).IsEqualTo(4);
        await Assert.That(element.Time.FadeOutTicks).IsEqualTo(9);
    }

    /// <summary>
    ///     A cadence is what <see cref="EnvelopeMode.RealTime" /> means and nothing else has one.
    ///     Null here is load-bearing: the DTO writes <c>WhenWritingNull</c>, so an element without one
    ///     emits no field and the pinned v1 schema sample does not move.
    /// </summary>
    [Test]
    [Arguments(EnvelopeMode.Always)]
    [Arguments(EnvelopeMode.Fade)]
    [Arguments(EnvelopeMode.Custom)]
    public async Task EveryModeButRealTime_CommitsNoTiming(EnvelopeMode mode)
    {
        Harness h = new();
        h.Session.DefaultVisibility = mode;
        h.Session.SetCustomWindow(500, 800);

        h.Press(0, 0);
        h.Services.Advance(120);
        h.Move(60, 0);
        h.Services.Advance(120);
        h.Release(120, 40);

        await Assert.That(h.Document.Elements[0].Timing).IsNull()
            .Because("time really passed while this was drawn; only RealTime is asking about it");
    }

    /// <summary>
    ///     ...and RealTime commits one, through the same public entry points. The shape of it is
    ///     <c>StrokeCadenceTests</c>' business; that it arrives at all is this suite's.
    /// </summary>
    [Test]
    public async Task RealTime_CommitsTheCadenceItWasDrawnAt()
    {
        Harness h = new();
        h.Session.DefaultVisibility = EnvelopeMode.RealTime;

        h.Press(0, 0);
        h.Services.Advance(250);
        h.Move(60, 0);
        h.Services.Advance(250);
        h.Release(120, 40);

        StrokeTiming? timing = h.Document.Elements[0].Timing;
        await Assert.That(timing).IsNotNull();
        await Assert.That(timing!.DurationTicks).IsEqualTo(32)
            .Because("500 ms of authoring is 32 ticks at 64 tick");
    }

    /// <summary>
    ///     The cadence flag is captured at PRESS, with the ink and the anchor and for the same reason:
    ///     recording has to begin at the first sample, so a toolbar flip mid-drag cannot retroactively
    ///     assign this stroke a cadence that was never being recorded.
    /// </summary>
    [Test]
    public async Task VisibilityChosenAtPress_SurvivesAModeChangeMidGesture()
    {
        Harness h = new();
        h.Session.DefaultVisibility = EnvelopeMode.Always;

        h.Press(0, 0);
        h.Session.DefaultVisibility = EnvelopeMode.RealTime;
        h.Services.Advance(250);
        h.Move(60, 0);
        h.Release(120, 40);

        await Assert.That(h.Document.Elements[0].Timing).IsNull();
    }

    [Test]
    public async Task Cancel_LeavesDocumentUnchanged()
    {
        Harness h = new();
        h.Press(0, 0);
        h.Move(50, 20);

        h.Tool.OnCancelled(h.Services);

        await Assert.That(h.Document.Elements).IsEmpty();
        await Assert.That(h.Document.UndoDepth).IsEqualTo(0);
        await Assert.That(h.Document.Version).IsEqualTo(0);
        await Assert.That(h.Session.Wet.IsActive).IsFalse();
    }

    [Test]
    public async Task Press_OffEveryPane_TakesNoGesture()
    {
        Harness h = new();

        ToolPointerEvent e = new()
        {
            Pane = null,
            Screen = new SKPoint(-50, -50),
            World = new SKPoint(0, 0),
            Pressure = 0.5f,
            Button = ToolPointerButton.Left
        };

        await Assert.That(h.Tool.OnPressed(in e, h.Services)).IsFalse();
        await Assert.That(h.Document.IsGestureOpen).IsFalse();
    }

    /// <summary>
    ///     <c>ToolPointerEvent.Button</c> reached the tools from day one and nothing read it, so
    ///     a right-drag drew ink identical to a left-drag. The button picks the pen.
    /// </summary>
    [Test]
    [Arguments(ToolPointerButton.Left, 0xFFFFC107u)]
    [Arguments(ToolPointerButton.Right, 0xFF29B6F6u)]
    [Arguments(ToolPointerButton.Middle, 0xFFFFC107u)]
    public async Task TheButtonPicksTheInk(ToolPointerButton button, uint expected)
    {
        Harness h = new();
        h.Session.Style = new AnnotationStyle(0xFFFFC107, 6f, 1f);
        h.Session.SecondaryStyle = new AnnotationStyle(0xFF29B6F6, 6f, 1f);

        h.Press(0, 0, button);
        h.Move(60, 0, button: button);
        h.Release(60, 0, button);

        await Assert.That(h.Document.Elements[0].Style.ColorArgb).IsEqualTo(expected);
    }

    /// <summary>
    ///     The wet stroke carries the pen it started with. A toolbar click mid-drag must not repaint the
    ///     half of the stroke that is already down and, on release, the whole of it.
    /// </summary>
    [Test]
    public async Task InkChosenAtPress_SurvivesAStyleChangeMidGesture()
    {
        Harness h = new();
        h.Session.SecondaryStyle = new AnnotationStyle(0xFF29B6F6, 6f, 1f);

        h.Press(0, 0, ToolPointerButton.Right);
        h.Session.SecondaryStyle = new AnnotationStyle(0xFF00FF00, 6f, 1f);
        h.Move(60, 0, button: ToolPointerButton.Right);
        h.Release(60, 0, ToolPointerButton.Right);

        await Assert.That(h.Document.Elements[0].Style.ColorArgb).IsEqualTo(0xFF29B6F6u);
    }

    private sealed class Harness
    {
        public Harness(double zMin = 0)
        {
            Pane = AnnotationFakes.Pane(600, 400, zMin, zMin + 64);
            Document = new AnnotationDocument();
            Session = new AnnotationSession(Document);
            Services = new FakeToolServices(Session, Pane);
            Tool = new DrawTool();
        }

        public LevelPane Pane { get; }

        public AnnotationDocument Document { get; }

        public AnnotationSession Session { get; }

        public FakeToolServices Services { get; }

        public DrawTool Tool { get; }

        public void Press(float x, float y, ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnPressed(Event(x, y, default, button), Services);

        public void Move(float x, float y, ReadOnlySpan<InkPoint> coalesced = default,
            ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnMoved(Event(x, y, coalesced, button), Services);

        public void Release(float x, float y, ToolPointerButton button = ToolPointerButton.Left) =>
            Tool.OnReleased(Event(x, y, default, button), Services);

        private ToolPointerEvent Event(float x, float y, ReadOnlySpan<InkPoint> coalesced,
            ToolPointerButton button) =>
            new()
            {
                Pane = Pane,
                Screen = Services.WorldToScreen(Pane, new SKPoint(x, y)),
                World = new SKPoint(x, y),
                Pressure = 0.5f,
                Button = button,
                Intermediate = coalesced
            };
    }
}
