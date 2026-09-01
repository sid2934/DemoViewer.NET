#region

using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <c>EnvelopeMode.Round</c>: the annotation lasts the round it was drawn in.
///     <para>
///         The bounds are a DEMO fact and the envelope is a Core type, so they meet at a resolver seam the
///         tab supplies. These tests drive the tab's real resolver over real
///         <c>round_freeze_end</c> events, because the interesting cases are all in the walk: the first
///         round, the last one (which has no following freeze-end), warmup before the first, and a demo
///         that carries no rounds at all.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class RoundEnvelopeTests
{
    // Three rounds, back to back. FrameIndexAtTick on the fake is tick/2 and drops anything past
    // TotalFrames, so every tick here stays inside a 1000-frame demo.
    private const int Round1 = 100;
    private const int Round2 = 700;
    private const int Round3 = 1300;

    private static readonly string[] EnvelopeBoxes =
        ["FadeInBox", "FadeOutBox", "HoldBox", "CustomFromBox", "CustomUntilBox"];

    /// <summary>
    ///     A playhead inside each of the three rounds gets that round's window, in TICKS, the axis a
    ///     <c>TimeEnvelope</c> is expressed on, which <c>EventsOfType</c> hands over directly.
    ///     <para>
    ///         The LAST round has no following freeze-end, and the choice made is an OPEN upper bound: it
    ///         runs to the end of the demo, which is what a null <c>UntilTick</c> already means and what
    ///         <c>RoundTrack</c>'s last band already does. Closing it would need a last-tick the timeline
    ///         contract does not expose, invented locally, and wrong the moment it disagreed with the band.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EachRound_GetsItsOwnWindow_AndTheLastOneIsOpenEnded()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            AnnotationSession session = RoundedTab().Annotations.Session;
            session.DefaultVisibility = EnvelopeMode.Round;
            session.FadeInTicks = 6;
            session.FadeOutTicks = 18;

            TimeEnvelope first = session.EnvelopeForNewElement(Round1 + 50);
            await Assert.That(first.FromTick).IsEqualTo(Round1);
            await Assert.That(first.UntilTick).IsEqualTo(Round2 - 1)
                .Because("freeze-ends are back to back, so a round closes the tick before the next opens "
                         + "— an inclusive bound of 700 would put one tick in two rounds");

            TimeEnvelope middle = session.EnvelopeForNewElement(Round2 + 300);
            await Assert.That(middle.FromTick).IsEqualTo(Round2);
            await Assert.That(middle.UntilTick).IsEqualTo(Round3 - 1);

            TimeEnvelope last = session.EnvelopeForNewElement(Round3 + 5);
            await Assert.That(last.FromTick).IsEqualTo(Round3);
            await Assert.That(last.UntilTick).IsNull()
                .Because("the last round runs to the end of the demo, and so does its annotation");

            // The ramps survive into all three: they are the two controls Round still offers.
            await Assert.That(first.FadeInTicks).IsEqualTo(6);
            await Assert.That(last.FadeOutTicks).IsEqualTo(18);

            // ...and the window really is the round, all the way across it.
            await Assert.That(middle.OpacityAt(Round2)).IsEqualTo(1.0);
            await Assert.That(middle.OpacityAt(Round3 - 1)).IsEqualTo(1.0);
            await Assert.That(middle.OpacityAt(Round3 + 18)).IsEqualTo(0.0);
        });
    }

    /// <summary>
    ///     A stroke drawn BEFORE the first freeze-end is in warmup, which is not a round. <c>RoundTrack</c>
    ///     bands it as <c>wu</c> for the same reason. It falls back rather than being pinned into round 1,
    ///     where the user was not looking.
    /// </summary>
    [Test]
    public async Task Warmup_IsNotARound_AndFallsBack()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            AnnotationSession session = RoundedTab().Annotations.Session;
            session.DefaultVisibility = EnvelopeMode.Round;
            session.HoldTicks = 320;

            TimeEnvelope envelope = session.EnvelopeForNewElement(20);

            await Assert.That(envelope.FromTick).IsEqualTo(20)
                .Because("the fallback opens where the user drew, not at round 1");
            await Assert.That(envelope.UntilTick).IsEqualTo(340);
        });
    }

    /// <summary>
    ///     <b>The fallback that matters.</b> A demo with no <c>round_freeze_end</c>, whether a warmup clip, a
    ///     partial parse, or an unsupported source, produces Fade's pinned trapezoid, never an empty or
    ///     inverted window. A mode that drew nothing there would be worse than one that drew the wrong
    ///     thing.
    /// </summary>
    [Test]
    public async Task ADemoWithNoRounds_FallsBackToThePinnedTrapezoid()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DFakeContext ctx = new()
            {
                Gate = new FakeModuleFeatureGate()
            };

            Playback2DTabViewModel vm = new();
            vm.OnActivated(ctx);

            AnnotationSession session = vm.Annotations.Session;
            session.DefaultVisibility = EnvelopeMode.Round;
            session.HoldTicks = 100;
            session.FadeInTicks = 8;
            session.FadeOutTicks = 16;

            TimeEnvelope round = session.EnvelopeForNewElement(9000);

            session.DefaultVisibility = EnvelopeMode.Fade;
            await Assert.That(round).IsEqualTo(session.EnvelopeForNewElement(9000));
            await Assert.That(round.FromTick).IsEqualTo(9000);
            await Assert.That(round.UntilTick).IsEqualTo(9100)
                .Because("an inverted or zero-length window would be a mode that silently draws nothing");
        });
    }

    /// <summary>
    ///     <c>HasEvent</c> can say yes where <c>EventsOfType</c> has nothing to hand back: a demo whose
    ///     event NAMES were indexed but whose payloads were not decoded, which is what a truncated parse
    ///     looks like from here. That must degrade the same way an absent event does.
    /// </summary>
    [Test]
    public async Task RoundsNamedButNotDecoded_DegradesLikeADemoWithNoRounds()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DFakeContext ctx = new()
            {
                Gate = new FakeModuleFeatureGate()
            };

            // The name is indexed (AvailableEventNames unions both maps) but no decoded event exists.
            ctx.Frames[RoundTrack.FreezeEndEvent] = [50, 350];

            Playback2DTabViewModel vm = new();
            vm.OnActivated(ctx);

            AnnotationSession session = vm.Annotations.Session;
            session.DefaultVisibility = EnvelopeMode.Round;
            session.HoldTicks = 100;

            TimeEnvelope envelope = session.EnvelopeForNewElement(9000);

            await Assert.That(envelope.FromTick).IsEqualTo(9000);
            await Assert.That(envelope.UntilTick).IsEqualTo(9100);
        });
    }

    /// <summary>
    ///     The panel offers <c>in</c> and <c>out</c> for Round and NOT <c>hold</c>, because the window is the
    ///     round and a hold would be a second answer to "how long", nor Custom's absolute window.
    /// </summary>
    [Test]
    public async Task Panel_Round_OffersTheRampsOnly()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            panel.VisibilityIndex = 4; // what selecting the fifth item does

            await Assert.That(panel.Visibility).IsEqualTo(EnvelopeMode.Round);
            await Assert.That(controller.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.Round)
                .Because("the panel edits the session; a mode that stops here is a decorative picker");
            await Assert.That(panel.VisibilityIndex).IsEqualTo(4)
                .Because("the ComboBox reads the index straight back; a getter that disagreed with the "
                         + "setter would snap the picker to the old row. That the XAML's item order IS "
                         + "the enum's is pinned by RealTimeEnvelopeUiTests, which opens the XAML");

            await Assert.That(panel.IsEnvelopeEditorVisible).IsTrue()
                .Because("in and out still ramp a Round element in and out");
            await Assert.That(panel.IsHoldEnvelope).IsFalse();
            await Assert.That(panel.IsCustomEnvelope).IsFalse();
            await Assert.That(panel.IsFadeEnvelope).IsFalse();
            await Assert.That(panel.IsRealTimeEnvelope).IsFalse();
        });
    }

    /// <summary>The same thing in the visual tree, which is where a missed <c>IsVisible</c> binding lives.</summary>
    [Test]
    public async Task SelectingRound_OpensTheRamps_AndNeitherHoldNorTheWindow()
    {
        List<string> visible = [];

        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, 1400, 800, Playback2DRendererKind.Scene);

            vm.Annotations.Visibility = EnvelopeMode.Round;
            Playback2DTimelineHarness.Pump();

            foreach (string name in EnvelopeBoxes)
            {
                NumericUpDown? found = window.GetVisualDescendants().OfType<NumericUpDown>()
                    .FirstOrDefault(n => n.Name == name);

                if (found?.IsEffectivelyVisible == true)
                {
                    visible.Add(name);
                }
            }

            await Task.CompletedTask;
        });

        Console.WriteLine($"[round-envelope] visible = {string.Join(", ", visible)}");

        await Assert.That(visible).Contains("FadeInBox");
        await Assert.That(visible).Contains("FadeOutBox");
        await Assert.That(visible).DoesNotContain("HoldBox")
            .Because("the window is the round; a hold beside it would be a second answer to how long");
        await Assert.That(visible).DoesNotContain("CustomFromBox");
        await Assert.That(visible).DoesNotContain("CustomUntilBox");
    }

    /// <summary>
    ///     The mode is persisted by NAME, so a fifth member costs no new settings key, but only if it
    ///     actually survives the fileless path, which is where a setting quietly forgets itself on WASM.
    /// </summary>
    [Test]
    public async Task RoundVisibility_RoundTripsThroughSettings()
    {
        SettingsService settings = new(null); // the fileless WASM branch, the one that forgets things

        using (AnnotationSessionController author = new(null, settings))
        {
            author.Session.DefaultVisibility = EnvelopeMode.Round;
            author.PersistSettings();
        }

        await Assert.That(settings.Current.Playback2D.AnnotationDefaultVisibility).IsEqualTo("Round")
            .Because("the key holds an EnvelopeMode NAME, and the names are a forever contract");

        using AnnotationSessionController reader = new(null, settings);
        await Assert.That(reader.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.Round);
    }

    // An activated tab over a demo with three decoded round_freeze_end events. EventsOfType reads the
    // event TIMELINE (not the frame index), so the rounds have to be here and not only in ctx.Frames.
    private static Playback2DTabViewModel RoundedTab()
    {
        Playback2DFakeContext ctx = new()
        {
            Gate = new FakeModuleFeatureGate()
        };

        ctx.Timelines[RoundTrack.FreezeEndEvent] =
        [
            Freeze(Round1), Freeze(Round2), Freeze(Round3)
        ];

        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return vm;
    }

    private static GameEventView Freeze(int tick) => new()
    {
        Name = RoundTrack.FreezeEndEvent,
        Tick = tick,
        Fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    };
}
