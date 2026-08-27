#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.Views.Playback2D;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The authoring preferences: the recent-colour strip, the per-button pen and the <c>Custom</c>
///     envelope.
///     <para>
///         All three shipped as plumbing with no control on the end of it — a persisted key, a view-model
///         property, and nothing a user could ever reach. These are the tests that keep them wired: a
///         value the panel writes has to survive a settings round trip, or the surface is decorative
///         again.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DAnnotationToolsTests
{
    [Test]
    public async Task RecentColors_PushNewestFirst_AndDeDuplicate()
    {
        using AnnotationSessionController controller = new(null, null);

        controller.RememberColor(0xFFFF0000);
        controller.RememberColor(0xFF00FF00);
        controller.RememberColor(0xFFFF0000);

        await Assert.That(controller.RecentColors.Count).IsEqualTo(2)
            .Because("re-using a colour moves it, it does not add a second copy");
        await Assert.That(controller.RecentColors[0]).IsEqualTo("#FFFF0000");
        await Assert.That(controller.RecentColors[1]).IsEqualTo("#FF00FF00");
    }

    /// <summary>
    ///     The swatch row holds eight, stated as a literal. Read off
    ///     <c>AnnotationSessionController.MaxRecentColors</c> it passed at a cap of 2 and at 500 alike —
    ///     the number is a UI decision and this is the test that owns it.
    /// </summary>
    [Test]
    public async Task RecentColors_StopAtTheCap_DroppingTheOldest()
    {
        using AnnotationSessionController controller = new(null, null);

        for (uint i = 0; i <= 8; i++)
        {
            controller.RememberColor(0xFF000000u | i);
        }

        await Assert.That(controller.RecentColors.Count).IsEqualTo(8);
        await Assert.That(controller.RecentColors.Contains("#FF000000")).IsFalse()
            .Because("the first colour is the one that falls off the end");
    }

    /// <summary>
    ///     Re-drawing with the colour already at the front is not a change, so it must not bump the
    ///     version the panel rebuilds off — nor trigger a settings write per stroke.
    /// </summary>
    [Test]
    public async Task RecentColors_RepeatingTheFrontColour_IsNotAChange()
    {
        using AnnotationSessionController controller = new(null, null);

        await Assert.That(controller.RememberColor(0xFF123456)).IsTrue();
        int version = controller.RecentColorsVersion;

        await Assert.That(controller.RememberColor(0xFF123456)).IsFalse();
        await Assert.That(controller.RecentColorsVersion).IsEqualTo(version);
    }

    /// <summary>
    ///     "Recent" means recently DRAWN WITH. A ColorPicker raises a change on every pointer move
    ///     through its spectrum, so pushing on style change filled the strip with eight shades of one
    ///     drag; the commit is the only moment that counts as use.
    /// </summary>
    [Test]
    public async Task ACommittedStroke_PushesItsOwnColour_AndAStyleChangeDoesNot()
    {
        using AnnotationSessionController controller = new(null, null);

        controller.Session.Style = new AnnotationStyle(0xFFABCDEF, 6f, 1f);
        await Assert.That(controller.RecentColors).IsEmpty()
            .Because("moving the picker is not using a colour");

        controller.Document.Apply(new DocDelta.Add(Stroke(0xFF102030), 0));

        await Assert.That(controller.RecentColors.Count).IsEqualTo(1);
        await Assert.That(controller.RecentColors[0]).IsEqualTo("#FF102030");
    }

    /// <summary>The right button's pen earns its swatch on the same terms as the left one's.</summary>
    [Test]
    public async Task ASecondaryInkStroke_PushesItsColourToo()
    {
        using AnnotationSessionController controller = new(null, null);

        controller.Document.Apply(new DocDelta.Add(Stroke(0xFF29B6F6), 0));

        await Assert.That(controller.RecentColors[0]).IsEqualTo("#FF29B6F6");
    }

    /// <summary>
    ///     The authored window, end to end: it reaches settings and comes back as a real envelope rather
    ///     than <c>TimeEnvelope.Static</c> under a different name.
    /// </summary>
    [Test]
    public async Task CustomWindow_RoundTripsThroughSettings()
    {
        SettingsService settings = new(null); // the fileless WASM branch — the one that forgets things

        using (AnnotationSessionController author = new(null, settings))
        {
            author.Session.DefaultVisibility = EnvelopeMode.Custom;
            author.Session.FadeInTicks = 12;
            author.Session.FadeOutTicks = 24;
            author.Session.SetCustomWindow(1500, 2500);
            author.PersistSettings();
        }

        await Assert.That(settings.Current.Playback2D.AnnotationCustomFromTick).IsEqualTo(1500);
        await Assert.That(settings.Current.Playback2D.AnnotationCustomUntilTick).IsEqualTo(2500);

        using AnnotationSessionController reader = new(null, settings);
        TimeEnvelope envelope = reader.Session.EnvelopeForNewElement(999_999);

        await Assert.That(reader.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.Custom);
        await Assert.That(envelope).IsNotEqualTo(TimeEnvelope.Static)
            .Because("Custom used to be a synonym for Always — one persisted string and no behaviour");
        await Assert.That(envelope.FromTick).IsEqualTo(1500);
        await Assert.That(envelope.UntilTick).IsEqualTo(2500);
        await Assert.That(envelope.FadeInTicks).IsEqualTo(12);
        await Assert.That(envelope.FadeOutTicks).IsEqualTo(24);
    }

    /// <summary>
    ///     <b>Dragging the ink <c>ColorPicker</c> used to rewrite <c>settings.json</c> on every pointer
    ///     sample.</b> <c>SettingsService.Write</c> is a synchronous read-serialize-temp-write-move-reload,
    ///     and the reload fires <c>IOptionsMonitor.OnChange</c> INLINE — re-composing the 2D keymap
    ///     profile and, with the Settings page open, re-reflecting thirty properties and twenty-one
    ///     keybind rows. A one-second colour drag was a few hundred of those on the UI thread.
    ///     <para>
    ///         The configuration's reload token is the thing every downstream <c>OnChange</c> hangs off,
    ///         so counting it counts the cost directly.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RapidStyleChanges_CoalesceIntoASingleSettingsWrite()
    {
        SettingsService settings = new(null); // the fileless branch — still writes, still reloads
        IConfigurationRoot root = (IConfigurationRoot)settings.Configuration;

        int reloads = 0;
        using IDisposable watch = ChangeToken.OnChange(root.GetReloadToken,
            () => Interlocked.Increment(ref reloads));

        using AnnotationSessionController controller = new(null, settings)
        {
            // Long enough that only an explicit flush can land it — the assertion is about coalescing,
            // not about how fast a timer runs on a loaded CI box.
            StylePersistDelay = TimeSpan.FromSeconds(30)
        };

        // What one second of dragging through the picker's spectrum looks like from here.
        for (int i = 0; i < 200; i++)
        {
            controller.Session.Style = new AnnotationStyle(0xFF000000u | (uint)i, 8f, 1f);
            controller.PersistSettings();
        }

        await Assert.That(reloads).IsEqualTo(0)
            .Because("200 write-and-reload cycles on the UI thread IS the defect");

        controller.FlushStyleSettings();

        await Assert.That(reloads).IsEqualTo(1);
        await Assert.That(settings.Current.Playback2D.AnnotationColorArgb).IsEqualTo(0xFF0000C7u)
            .Because("the sample the user let go on is the one that has to reach the file");
    }

    [Test]
    public async Task SecondaryPen_RoundTripsThroughSettings()
    {
        SettingsService settings = new(null);

        using (AnnotationSessionController author = new(null, settings))
        {
            author.Session.SecondaryStyle = new AnnotationStyle(0xFF00FF00, 6f, 1f);
            author.Session.SecondaryTool = ToolKind.Erase;
            author.PersistSettings();
        }

        await Assert.That(settings.Current.Playback2D.AnnotationSecondaryTool).IsEqualTo("Erase");

        using AnnotationSessionController reader = new(null, settings);
        await Assert.That(reader.Session.SecondaryStyle.ColorArgb).IsEqualTo(0xFF00FF00u);
        await Assert.That(reader.Session.SecondaryTool).IsEqualTo(ToolKind.Erase);
    }

    /// <summary>
    ///     The binding is a hand-editable string. Nonsense means "no override", and so does
    ///     <c>PanZoom</c>: middle- and Ctrl-drag already pan under every tool, so a third way to pan
    ///     bound to the button that is supposed to draw would only take the second pen away.
    /// </summary>
    [Test]
    [Arguments("Same")]
    [Arguments("PanZoom")]
    [Arguments("nonsense")]
    [Arguments("")]
    public async Task SecondaryTool_UnusableValues_MeanNoOverride(string persisted)
    {
        SettingsService settings = new(null);
        settings.Write(s => s.Playback2D.AnnotationSecondaryTool = persisted);

        using AnnotationSessionController controller = new(null, settings);

        await Assert.That(controller.Session.SecondaryTool).IsNull();
    }

    /// <summary>
    ///     The panel is what the toolbar binds to, so the envelope editor's visibility is a contract:
    ///     Always — the shipped default — must not put four spin boxes in a toolbar that already reflows
    ///     at 820 px.
    /// </summary>
    [Test]
    public async Task Panel_EnvelopeEditor_IsOfferedOnlyWhenTheModeNeedsIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await Assert.That(panel.IsEnvelopeEditorVisible).IsFalse();

            panel.Visibility = EnvelopeMode.Fade;
            await Assert.That(panel.IsEnvelopeEditorVisible).IsTrue();
            await Assert.That(panel.IsFadeEnvelope).IsTrue();
            await Assert.That(panel.IsCustomEnvelope).IsFalse();

            panel.Visibility = EnvelopeMode.Custom;
            await Assert.That(panel.IsCustomEnvelope).IsTrue();
            await Assert.That(panel.IsFadeEnvelope).IsFalse();
        });
    }

    /// <summary>
    ///     <b>The Real-time mode, through the ComboBox index a user actually moves.</b> The panel's index
    ///     adapter is the only place the XAML's item order and <c>EnvelopeMode</c>'s declaration order
    ///     have to agree, and a mode that reaches the panel but not the session is a picker that changes
    ///     nothing — the exact shape the Custom mode once shipped in.
    /// </summary>
    [Test]
    public async Task Panel_RealTime_OffersTheRelativeControls_AndReachesTheSession()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            panel.VisibilityIndex = 3; // what selecting the fourth item does

            await Assert.That(panel.Visibility).IsEqualTo(EnvelopeMode.RealTime);
            await Assert.That(controller.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.RealTime)
                .Because("the panel edits the session; a mode that stops here is a decorative picker");
            await Assert.That(panel.VisibilityIndex).IsEqualTo(3)
                .Because("the ComboBox reads the index straight back; a getter that disagreed with the "
                         + "setter would snap the picker to the old row. That the XAML's item order IS "
                         + "the enum's is pinned by RealTimeEnvelopeUiTests, which opens the XAML");

            // in / out / hold — the three relative controls, and not Custom's absolute window. Each
            // section runs the element's own trapezoid shifted by its draw offset, so all three keep
            // their meaning per section, while from/until would be a second answer to "when".
            await Assert.That(panel.IsEnvelopeEditorVisible).IsTrue();
            await Assert.That(panel.IsHoldEnvelope).IsTrue();
            await Assert.That(panel.IsRealTimeEnvelope).IsTrue();
            await Assert.That(panel.IsCustomEnvelope).IsFalse();
            await Assert.That(panel.IsFadeEnvelope).IsFalse()
                .Because("IsFadeEnvelope answers WHICH MODE; IsHoldEnvelope answers which controls");
        });
    }

    /// <summary>
    ///     The mode is persisted by NAME, so a fourth member costs no new key — but only if it actually
    ///     survives the fileless path, which is where a setting quietly forgets itself.
    /// </summary>
    [Test]
    public async Task RealTimeVisibility_RoundTripsThroughSettings()
    {
        SettingsService settings = new(null); // the fileless WASM branch — the one that forgets things

        using (AnnotationSessionController author = new(null, settings))
        {
            author.Session.DefaultVisibility = EnvelopeMode.RealTime;
            author.PersistSettings();
        }

        await Assert.That(settings.Current.Playback2D.AnnotationDefaultVisibility).IsEqualTo("RealTime")
            .Because("the key holds an EnvelopeMode NAME, and the names are a forever contract");

        using AnnotationSessionController reader = new(null, settings);
        await Assert.That(reader.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.RealTime);
    }

    /// <summary>
    ///     A visibility string this build cannot make sense of degrades to <c>Always</c> — including a
    ///     bare NUMBER, which <c>Enum.TryParse</c> accepts for any value in range whether a member is
    ///     defined there or not. That is a mode nothing switches on, arriving at the toolbar's ComboBox
    ///     as an out-of-range index; the same fence <c>AnnotationStore</c> puts on a hand-edited kind.
    /// </summary>
    [Test]
    [Arguments("RealTime", EnvelopeMode.RealTime)]
    [Arguments("realtime", EnvelopeMode.RealTime)]
    [Arguments("Round", EnvelopeMode.Round)]
    [Arguments("round", EnvelopeMode.Round)]
    [Arguments("7", EnvelopeMode.Always)]
    [Arguments("Nonsense", EnvelopeMode.Always)]
    public async Task PersistedVisibility_ParsesByName_AndFencesTheRest(string persisted,
        EnvelopeMode expected)
    {
        SettingsService settings = new(null);
        settings.Write(s => s.Playback2D.AnnotationDefaultVisibility = persisted);

        using AnnotationSessionController controller = new(null, settings);

        await Assert.That(controller.Session.DefaultVisibility).IsEqualTo(expected);
    }

    [Test]
    public async Task Panel_CustomWindow_ComposesTheTemplateTheRendererReads()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 4242);

            panel.Visibility = EnvelopeMode.Custom;
            panel.FadeInTicks = 5;
            panel.CustomFromTick = 900;
            panel.CustomUntilTick = 1900;

            TimeEnvelope envelope = controller.Session.EnvelopeForNewElement(0);
            await Assert.That(envelope.FromTick).IsEqualTo(900);
            await Assert.That(envelope.UntilTick).IsEqualTo(1900);
            await Assert.That(envelope.FadeInTicks).IsEqualTo(5);

            // "⌖ now" moves the window to the playhead and KEEPS its length — a coach re-using the same
            // three-second callout on the next round should not have to re-type the length.
            panel.CustomWindowFromNowCommand.Execute(null);
            await Assert.That(panel.CustomFromTick).IsEqualTo(4242);
            await Assert.That(panel.CustomUntilTick).IsEqualTo(5242);
        });
    }

    /// <summary>
    ///     Seeding the panel must not destroy what it is seeding FROM. The ramps and the window share the
    ///     same template, so a pull that assigned the ramps first re-composed the template out of the
    ///     window the panel was still showing — and the persisted Custom window silently became 0..320.
    /// </summary>
    [Test]
    public async Task Panel_SeededFromSettings_KeepsTheAuthoredCustomWindow()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            SettingsService settings = new(null);
            settings.Write(s =>
            {
                s.Playback2D.AnnotationDefaultVisibility = "Custom";
                s.Playback2D.AnnotationFadeInTicks = 12;
                s.Playback2D.AnnotationFadeOutTicks = 24;
                s.Playback2D.AnnotationCustomFromTick = 1500;
                s.Playback2D.AnnotationCustomUntilTick = 2500;
            });

            using AnnotationSessionController controller = new(null, settings);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await Assert.That(panel.CustomFromTick).IsEqualTo(1500);
            await Assert.That(panel.CustomUntilTick).IsEqualTo(2500);
            await Assert.That(controller.Session.NewElementEnvelope.FromTick).IsEqualTo(1500);
            await Assert.That(controller.Session.NewElementEnvelope.UntilTick).IsEqualTo(2500);
            await Assert.That(controller.Session.NewElementEnvelope.FadeInTicks).IsEqualTo(12);

            panel.Resync();
            await Assert.That(panel.CustomFromTick).IsEqualTo(1500)
                .Because("a re-seed on demo attach is the same pull, run again");
        });
    }

    [Test]
    public async Task Panel_RightButtonErases_IsTheSecondaryToolBinding()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await Assert.That(panel.RightButtonErases).IsFalse()
                .Because("the shipped right button is the SECOND PEN, not the eraser");
            await Assert.That(controller.Session.SecondaryTool).IsNull();

            panel.RightButtonErases = true;
            await Assert.That(controller.Session.SecondaryTool).IsEqualTo(ToolKind.Erase);

            panel.RightButtonErases = false;
            await Assert.That(controller.Session.SecondaryTool).IsNull();
        });
    }

    [Test]
    public async Task Panel_Swatches_MirrorTheControllerNewestFirst_AndPaintThePrimaryPen()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            controller.RememberColor(0xFF00FF00);
            controller.RememberColor(0xFFFF0000);

            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await Assert.That(panel.HasRecentColors).IsTrue();
            await Assert.That(panel.RecentColors.Count).IsEqualTo(2);
            await Assert.That(panel.RecentColors[0].Argb).IsEqualTo(0xFFFF0000u);

            panel.ApplyRecentColorCommand.Execute(panel.RecentColors[1]);

            await Assert.That(panel.InkColorHex).IsEqualTo("#FF00FF00");
            await Assert.That(controller.Session.Style.ColorArgb).IsEqualTo(0xFF00FF00u);
        });
    }

    /// <summary>A malformed persisted row is dropped, not guessed at — and does not take the strip down.</summary>
    [Test]
    public async Task Panel_Swatches_DropMalformedPersistedRows()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            SettingsService settings = new(null);
            settings.Write(s => s.Playback2D.AnnotationRecentColors =
                ["#FF112233", "not-a-colour", "#GGGGGGGG", "#FF445566"]);

            using AnnotationSessionController controller = new(null, settings);
            controller.LoadRecentColors();
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await Assert.That(panel.RecentColors.Count).IsEqualTo(2);
            await Assert.That(panel.RecentColors[0].Hex).IsEqualTo("#FF112233");
            await Assert.That(panel.RecentColors[1].Hex).IsEqualTo("#FF445566");
        });
    }

    /// <summary>The opacity control was the missing half: a persisted value nothing could ever set.</summary>
    [Test]
    public async Task Panel_InkOpacity_ReachesBothPens()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            panel.InkOpacity = 0.4;

            await Assert.That(controller.Session.Style.Opacity).IsEqualTo(0.4f);
            await Assert.That(controller.Session.SecondaryStyle.Opacity).IsEqualTo(0.4f)
                .Because("two pens that could drift into two opacities is a bug waiting to be filed");
        });
    }

    /// <summary>
    ///     <b>The UI half of <c>AnnotationAutoSave</c>.</b> The setting was read at runtime and had no
    ///     control anywhere — the same shape the opacity slider above was written to close. The toggle
    ///     lives in the toolbar's PERSISTENCE row beside the line that names the destination, because
    ///     "does this get written, and where" is one question.
    /// </summary>
    [Test]
    public async Task Panel_AutoSaveToggle_ReachesTheControllerAndTheToolbar()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(null, null);
            using AnnotationsPanelViewModel panel = new(controller, () => 0);

            await Assert.That(panel.AutoSaveSidecar).IsTrue();

            panel.AutoSaveSidecar = false;
            await Assert.That(controller.AutoSave).IsFalse()
                .Because("the panel property is the only thing that can ever set the key");

            // Session-only by construction here (no store), so the toggle must present as unavailable
            // rather than as a promise: a checkbox controlling saving where nothing saves is the defect
            // one layer down.
            await Assert.That(panel.CanAutoSave).IsFalse();

            AnnotationToolbar toolbar = new()
            {
                DataContext = panel
            };
            CheckBox box = toolbar.FindControl<CheckBox>("AutoSaveToggle")
                           ?? throw new InvalidOperationException(
                               "AutoSaveToggle is not in the toolbar — the key is unreachable again.");

            // The control's own binding, evaluated: a property the view-model exposes and no control
            // binds is exactly what this finding was.
            toolbar.Measure(new Size(2000, 400));
            Console.WriteLine($"[autosave-ui] checked={box.IsChecked} enabled={box.IsEnabled}");

            await Assert.That(box.IsChecked).IsFalse();
            await Assert.That(box.IsEnabled).IsFalse();

            panel.AutoSaveSidecar = true;
            await Assert.That(box.IsChecked).IsTrue()
                .Because("the binding is two-way and live, not a one-shot read at load");
        });
    }

    private static AnnotationElement Stroke(uint argb) =>
        new(Guid.NewGuid(), AnnotationKind.Freehand,
            new AnnotationStyle(argb, 6f, 1f), new SpaceRef.World(0), TimeEnvelope.Static,
            [new InkPoint(0, 0, 0.5f), new InkPoint(10, 0, 0.5f)], null);
}
