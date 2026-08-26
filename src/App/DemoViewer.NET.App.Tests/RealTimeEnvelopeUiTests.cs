#region

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     D7's <c>Real-time</c> mode as the toolbar actually renders it: the fourth item exists and is
///     wide enough to read, and picking it opens the three RELATIVE envelope fields without opening
///     <c>Custom</c>'s absolute window.
///     <para>
///         The spinners themselves are not re-measured here — Real-time shows the very same
///         <c>FadeInBox</c> / <c>FadeOutBox</c> / <c>HoldBox</c> at the same width that
///         <see cref="EnvelopeSpinnerWidthTests" /> already pins against rendered text. What is new is
///         the ComboBox: <c>Real-time</c> is the longest of the four labels, and a ComboBox spends a
///         constant on its drop-down glyph and padding exactly the way a <c>NumericUpDown</c> spends one
///         on its two buttons, so the label was the one control the row's own sizing comment did not
///         already cover.
///     </para>
/// </summary>
[NotInParallel]
public class RealTimeEnvelopeUiTests
{
    private const string RealTimeLabel = "Real-time";

    /// <summary>
    ///     The fourth item is present and its index is <c>EnvelopeMode.RealTime</c>'s. The panel's index
    ///     adapter is a raw cast, so XAML order and enum order are one contract with two halves and this
    ///     is the half a view-model test cannot see.
    /// </summary>
    [Test]
    public async Task VisibilityBox_CarriesAFourthItem_AtRealTimesOwnIndex()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, 1400, 800, Playback2DRendererKind.Scene);

            ComboBox box = VisibilityBox(window);

            await Assert.That(box.ItemCount).IsEqualTo(4);

            vm.Annotations.Visibility = EnvelopeMode.RealTime;
            Playback2DTimelineHarness.Pump();

            await Assert.That(box.SelectedIndex).IsEqualTo((int)EnvelopeMode.RealTime)
                .Because("the panel's index adapter casts, so the item order IS the enum's order");
        });
    }

    /// <summary>
    ///     Picking Real-time opens <c>in</c>, <c>out</c> and <c>hold</c>, and leaves <c>from</c> /
    ///     <c>until</c> shut. Per plan D7 §3 each section runs the element's own trapezoid shifted by the
    ///     offset it was drawn at, so all three keep their meaning per section — while an absolute window
    ///     would be a second, contradictory answer to "when".
    /// </summary>
    [Test]
    public async Task SelectingRealTime_OpensTheRelativeFields_AndNotTheAbsoluteWindow()
    {
        List<string> visible = [];

        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, 1400, 800, Playback2DRendererKind.Scene);

            vm.Annotations.Visibility = EnvelopeMode.RealTime;
            Playback2DTimelineHarness.Pump();

            string[] names =
                ["FadeInBox", "FadeOutBox", "HoldBox", "CustomFromBox", "CustomUntilBox"];
            foreach (string name in names)
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

        Console.WriteLine($"[realtime-envelope] visible = {string.Join(", ", visible)}");

        await Assert.That(visible).Contains("FadeInBox");
        await Assert.That(visible).Contains("FadeOutBox");
        await Assert.That(visible).Contains("HoldBox")
            .Because("the hold is what decides whether the stroke dissolves from the start or chases "
                     + "its own tail — the same control produces both, so Real-time has to have it");
        await Assert.That(visible).DoesNotContain("CustomFromBox");
        await Assert.That(visible).DoesNotContain("CustomUntilBox");
    }

    /// <summary>
    ///     The label renders unclipped. This is the "a control too small to show its own contents"
    ///     defect one control over from the spinners the same row already learned it on: every other kind
    ///     of test passes a ComboBox whose selected text is cut off mid-word.
    /// </summary>
    [Test]
    public async Task VisibilityBox_RendersItsLongestLabel_Unclipped()
    {
        double needed = 0;
        double available = 0;

        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, 1400, 800, Playback2DRendererKind.Scene);

            vm.Annotations.Visibility = EnvelopeMode.RealTime;
            Playback2DTimelineHarness.Pump();

            ComboBox box = VisibilityBox(window);

            // The SELECTED content, never a popup item: a closed ComboBox has not realized its item
            // containers, but excluding anything inside a ComboBoxItem keeps this honest if it ever does.
            TextBlock label = box.GetVisualDescendants().OfType<TextBlock>()
                                  .FirstOrDefault(t => t.Text == RealTimeLabel
                                                       && !t.GetVisualAncestors().OfType<ComboBoxItem>()
                                                           .Any())
                              ?? throw new InvalidOperationException(
                                  $"'{RealTimeLabel}' is not rendered in {box.Name}. Either the fourth "
                                  + "item is missing or the selection never reached the presenter — "
                                  + "this test would otherwise measure nothing.");

            // Measured in the font it actually renders in, not against a digit-width constant: the point
            // is to survive a theme whose chrome grows, which a hard-coded number would not.
            needed = new FormattedText(RealTimeLabel, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight),
                label.FontSize, Brushes.Black).Width;
            available = label.Bounds.Width;

            await Task.CompletedTask;
        });

        Console.WriteLine(
            $"[visibility-width] '{RealTimeLabel}' needs={needed:F1} has={available:F1}");

        await Assert.That(available).IsGreaterThanOrEqualTo(needed)
            .Because("a mode picker that clips its own longest label is the sizing defect the envelope "
                     + "row already shipped once, one control to the left");
    }

    private static ComboBox VisibilityBox(Window window) =>
        window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "VisibilityBox")
        ?? throw new InvalidOperationException(
            "VisibilityBox is not in the visual tree — the annotation toolbar did not mount, so "
            + "nothing below this line is measuring the toolbar.");
}
