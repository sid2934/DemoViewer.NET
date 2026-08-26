#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The annotation toolbar's envelope spinners must be wide enough to SHOW the value they hold.
///     <para>
///         This is the "a control too small to render its own contents" defect, and it is invisible to
///         every other kind of test: the binding is correct, the view-model is correct, the control is
///         present, hit-testable and non-zero — it simply clips. <c>Playback2DHudLayoutTests</c> asserts
///         that a control is inside the column and reachable, which a two-digit-wide spinner passes.
///     </para>
///     <para>
///         The trap being guarded is that a Fluent <c>NumericUpDown</c>'s chrome does not scale with the
///         width it is given: the two spinner buttons and the border cost a CONSTANT ~68 px and the inner
///         <c>TextBox</c>'s padding another ~16, so width buys glyphs one-for-one above an ~84 px floor.
///         At the 86 px these fields shipped at, <c>in</c> and <c>out</c> had <b>2 px</b> of glyph space.
///         Sizing by eye is what produced that, so this measures the rendered text instead.
///     </para>
/// </summary>
[NotInParallel]
public class EnvelopeSpinnerWidthTests
{
    // TWO unit domains since D8, and the worst case is measured in each rather than in whichever one the
    // test happened to be written against.
    //
    // TICKS (from/until): six digits covers any tick a real demo produces — the reference Nuke demo is
    // 19 237 frames, five digits — and both fields accept eight.
    private const int SixDigits = 123456;

    // SECONDS (in/out/hold): the spinners' own Maximum, which is what the control clamps anything larger
    // down to, so nothing wider can ever appear in one. "600.00" is five digits and a period where the
    // tick fields ask for six digits — the seconds domain is the CHEAPER of the two, which is why D8
    // moved no width.
    private const double WidestSeconds = 600;

    /// <summary>Every spinner visible in the Custom envelope renders its widest value unclipped.</summary>
    [Test]
    public async Task CustomEnvelopeSpinners_ShowTheirWidestValue_Unclipped()
    {
        await AssertSpinnersFit(EnvelopeMode.Custom,
            ["FadeInBox", "FadeOutBox", "CustomFromBox", "CustomUntilBox"]);
    }

    /// <summary>
    ///     Same for the Fade envelope, whose <c>hold</c> field is hidden in Custom — a field that only
    ///     appears in one mode is a field only one mode's test can measure.
    /// </summary>
    [Test]
    public async Task FadeEnvelopeSpinners_ShowTheirWidestValue_Unclipped()
    {
        await AssertSpinnersFit(EnvelopeMode.Fade, ["FadeInBox", "FadeOutBox", "HoldBox"]);
    }

    private static async Task AssertSpinnersFit(EnvelopeMode mode, string[] names)
    {
        List<string> tooNarrow = [];
        List<string> measured = [];

        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext _) = Playback2DTimelineHarness.Tab();
            (Window window, Playback2DView _) =
                Playback2DTimelineHarness.Show(vm, 1400, 800, Playback2DRendererKind.Scene);

            vm.Annotations.Visibility = mode;
            vm.Annotations.FadeInSeconds = WidestSeconds;
            vm.Annotations.FadeOutSeconds = WidestSeconds;
            vm.Annotations.HoldSeconds = WidestSeconds;
            vm.Annotations.CustomFromTick = SixDigits;
            vm.Annotations.CustomUntilTick = SixDigits + 1;
            Playback2DTimelineHarness.Pump();

            foreach (string name in names)
            {
                NumericUpDown box = window.GetVisualDescendants().OfType<NumericUpDown>()
                                        .FirstOrDefault(n => n.Name == name)
                                    ?? throw new InvalidOperationException(
                                        $"{name} is not in the visual tree — the envelope editor did "
                                        + $"not open for {mode}, so this test measured nothing.");

                TextBox inner = box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()
                                ?? throw new InvalidOperationException(
                                    $"{name} has no inner TextBox. A NumericUpDown that never built its "
                                    + "template is the missing-control-theme defect, not a sizing one.");

                // What the box actually holds, measured in the font it actually renders in — not a
                // digit-width constant, which would drift the moment the theme's face or size changed.
                string text = inner.Text ?? "";
                double needed = MeasureText(text, inner);
                double available = inner.Bounds.Width - inner.Padding.Left - inner.Padding.Right;

                measured.Add($"{name} text='{text}' needs={needed:F1} has={available:F1}");
                if (available < needed)
                {
                    tooNarrow.Add($"{name}: '{text}' needs {needed:F1} px, has {available:F1} px");
                }
            }

            await Task.CompletedTask;
        });

        Console.WriteLine($"[spinner-width] {string.Join(" · ", measured)}");
        await Assert.That(tooNarrow).IsEmpty()
            .Because("an envelope spinner too narrow to render its own value clips silently — "
                     + "the binding, the view-model and the hit test all still pass");
    }

    private static double MeasureText(string text, TextBox inner) =>
        new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(inner.FontFamily, inner.FontStyle, inner.FontWeight),
            inner.FontSize, Brushes.Black).Width;
}
