#region

using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     D8 §§1–2: the envelope DURATIONS are authored in seconds, stored in ticks, and converted at the
///     LOADED DEMO'S rate rather than at a literal 64.
///     <para>
///         Those two halves are one test suite because they are one defect. A tick is not a unit a user
///         can reason about, so the toolbar shows seconds; and the moment it does, the rate stops being
///         invisible — on a 128-tick parse a user typing "5 s" against a hard-coded 64 would get 2.5 s of
///         hold and a real-time stroke replaying at half speed. Showing the number is what makes the
///         wrong divisor a visible bug rather than a latent one.
///     </para>
/// </summary>
[NotInParallel]
public class EnvelopeSecondsTests
{
    /// <summary>
    ///     <b>The sharp one.</b> Five seconds of hold on a 128-tick session is 640 ticks. At D7a's
    ///     literal it would be 320 — a hold half as long as the one the user asked for, in a control that
    ///     now says "5.00" either way.
    /// </summary>
    [Test]
    [Arguments(64, 320)]
    [Arguments(128, 640)]
    [Arguments(102, 510)]
    public async Task FiveSecondsOfHold_IsFiveSecondsOfTheDemosOwnTicks(int rate, int expected)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            // Through the real seam, not by assigning the session: the rate reaches the session off the
            // ClockIdentity the tab hands to an attach, which is the path §1 built.
            await controller.AttachDemoAsync(null, new ClockIdentity(ClockIdentity.DvFrameClock, rate,
                1000, 0, 0));
            panel.Resync();

            await Assert.That(panel.TicksPerSecond).IsEqualTo(rate);

            panel.HoldSeconds = 5.0;

            await Assert.That(panel.HoldTicks).IsEqualTo(expected);
            await Assert.That(controller.Session.HoldTicks).IsEqualTo(expected)
                .Because("the panel edits the session; a duration that stops at the panel changes no ink");

            // ...and the envelope the renderer actually reads is five seconds wide on this demo's clock.
            controller.Session.DefaultVisibility = EnvelopeMode.Fade;
            TimeEnvelope envelope = controller.Session.EnvelopeForNewElement(1000);
            await Assert.That(envelope.UntilTick!.Value - envelope.FromTick!.Value).IsEqualTo(expected);
        });
    }

    /// <summary>
    ///     The same arithmetic for the two ramps, which share the seconds treatment with the hold and are
    ///     the fields a rate-blind conversion would silently double or halve on every fade.
    /// </summary>
    [Test]
    public async Task TheRamps_ConvertAtTheDemosRateToo()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await controller.AttachDemoAsync(null,
                new ClockIdentity(ClockIdentity.DvFrameClock, 128, 1000, 0, 0));
            panel.Resync();

            panel.FadeInSeconds = 0.25;
            panel.FadeOutSeconds = 1.5;

            await Assert.That(panel.FadeInTicks).IsEqualTo(32);
            await Assert.That(panel.FadeOutTicks).IsEqualTo(192);
            await Assert.That(controller.Session.FadeInTicks).IsEqualTo(32);
            await Assert.That(controller.Session.FadeOutTicks).IsEqualTo(192);
        });
    }

    /// <summary>
    ///     <b>Ticks are the source of truth, and the seconds are a projection re-derived from them.</b>
    ///     A value the user typed survives any number of panel reloads unchanged.
    ///     <para>
    ///         The failure this forbids is the other design — holding the typed seconds beside the ticks —
    ///         where each reload re-rounds one against the other and a 5.30 s hold walks to 5.29, 5.28,
    ///         and on down. Quantizing ONCE, to within half a tick (7.8 ms at 64), is invisible; a value
    ///         that moves every time the panel is re-seeded is not.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(64, 5.0)]
    [Arguments(64, 5.3)]   // 339.2 ticks — deliberately NOT on a tick boundary
    [Arguments(128, 0.17)] // 21.76 ticks, on the rate a naive conversion gets wrong
    [Arguments(64, 0.0)]
    public async Task SecondsRoundTripThroughTicks_WithoutDrift(int rate, double typed)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await controller.AttachDemoAsync(null, new ClockIdentity(ClockIdentity.DvFrameClock, rate,
                1000, 0, 0));
            panel.Resync();

            panel.HoldSeconds = typed;
            panel.FadeInSeconds = typed;
            panel.FadeOutSeconds = typed;

            int holdTicks = panel.HoldTicks;
            double holdSeconds = panel.HoldSeconds;

            await Assert.That(Math.Abs(holdSeconds - typed)).IsLessThanOrEqualTo(0.5 / rate)
                .Because("the one quantization a user meets is half a tick, at the moment they type");

            for (int reload = 0; reload < 5; reload++)
            {
                // A demo attach re-seeds the panel from the session; Resync is that pull, run by hand.
                panel.Resync();

                // ...and a seconds value written straight back is the round trip the spinner performs
                // every time it re-parses its own displayed text.
                (double hold, double fadeIn, double fadeOut) =
                    (panel.HoldSeconds, panel.FadeInSeconds, panel.FadeOutSeconds);
                panel.HoldSeconds = hold;
                panel.FadeInSeconds = fadeIn;
                panel.FadeOutSeconds = fadeOut;

                await Assert.That(panel.HoldTicks).IsEqualTo(holdTicks)
                    .Because($"reload {reload + 1} moved the stored tick value");
                await Assert.That(panel.HoldSeconds).IsEqualTo(holdSeconds);
                await Assert.That(panel.FadeInTicks).IsEqualTo(holdTicks);
                await Assert.That(panel.FadeOutTicks).IsEqualTo(holdTicks);
            }
        });
    }

    /// <summary>
    ///     A demo attach that changes the rate changes every DURATION on the panel without moving a
    ///     single tick — so the projections have to be re-raised off the attach, which no
    ///     <c>[ObservableProperty]</c> setter would do.
    /// </summary>
    [Test]
    public async Task ADemoAttach_ThatChangesTheRate_ReDisplaysEveryDuration()
    {
        List<string> raised = [];

        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            panel.HoldTicks = 320;
            await Assert.That(panel.HoldSeconds).IsEqualTo(5.0);

            panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

            await controller.AttachDemoAsync(null,
                new ClockIdentity(ClockIdentity.DvFrameClock, 128, 1000, 0, 0));

            await Assert.That(panel.HoldTicks).IsEqualTo(320)
                .Because("storage is ticks, and a rate change is not an edit");
            await Assert.That(panel.HoldSeconds).IsEqualTo(2.5)
                .Because("320 ticks IS 2.5 s on a 128-tick parse — the display is what moved");
        });

        Console.WriteLine($"[seconds-reraise] {string.Join(", ", raised.Distinct())}");
        await Assert.That(raised).Contains(nameof(AnnotationsPanelViewModel.HoldSeconds));
        await Assert.That(raised).Contains(nameof(AnnotationsPanelViewModel.FadeInSeconds));
        await Assert.That(raised).Contains(nameof(AnnotationsPanelViewModel.FadeOutSeconds));
    }

    /// <summary>
    ///     End to end from the CONTEXT: the tab reads the demo's rate, stamps it on the
    ///     <c>ClockIdentity</c> it attaches with, and the session divides by it.
    /// </summary>
    [Test]
    public async Task TheTabSourcesTheRate_FromTheDemoContext()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DFakeContext ctx = new()
            {
                TickRate = 128,
                Gate = new FakeModuleFeatureGate()
            };

            Playback2DTabViewModel vm = new();
            vm.OnActivated(ctx);

            await Assert.That(vm.Annotations.Session.TicksPerSecond).IsEqualTo(128)
                .Because("the rate is a property of the loaded parse, and ClockIdentity already carries "
                         + "it — inventing a second path to the same number is a second thing to forget");

            vm.Annotations.HoldSeconds = 5.0;
            await Assert.That(vm.Annotations.HoldTicks).IsEqualTo(640);
        });
    }
}
