#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Stats;
using DemoViewer.NET.Views.Diagnostics;
using DemoViewer.NET.Views.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Render smoke for the stats component library (docs/ui/stats-components.md). The maths is proved
///     without a harness in <see cref="StatScaleTests" />; what needs a renderer is the half that cannot
///     be unit tested: that each control actually paints, that it paints DIFFERENTLY under Dark and
///     Light, and that the inputs which would throw in a naive implementation do not.
///     <para>
///         The theme assertion is the load-bearing one. Seven of the nine controls draw with
///         <c>DrawingContext</c> and none of the nine holds a brush in code, so every colour arrives as a
///         styled property fed by <c>{DynamicResource}</c> from <c>Styles/Stats.axaml</c>. If one of them
///         ever caches a brush or resolves a token statically, Dark and Light stop differing and this
///         test is what says so.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class StatsComponentRenderTests
{
    /// <summary>
    ///     Every tier of the heat ramp actually reaches the screen. Counting "non-background" pixels
    ///     would not prove this: at the headless Default variant the whole canvas is light, so that count
    ///     saturates at the window area and a blank window passes it. Looking for the four Dark accent
    ///     values instead proves the specific thing worth proving, that a value's sentiment resolves
    ///     through the token layer and paints.
    /// </summary>
    [Test]
    public async Task Gallery_PaintsEveryTierOfTheHeatRamp()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                }

                Window window = BuildWindow(Gallery());
                window.RequestedThemeVariant = ThemeVariant.Dark;
                WriteableBitmap? frame = Render(window);
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "stats-components.png");
                frame!.Save(outPath, new PngBitmapEncoderOptions());
                byte[] pixels = FrameProbe.ToBytes(frame);

                foreach ((string token, uint hex) in DarkRamp)
                {
                    int hits = FrameProbe.CountPixels(pixels, hex);
                    Console.WriteLine($"[stats-components] {token} #{hex:X6} hits={hits}");
                    await Assert.That(hits).IsGreaterThan(0);
                }
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }
        });
    }

    /// <summary>
    ///     The tier boundaries, pinned. This is the rule the whole library exists to apply, and it is a
    ///     brush identity rather than a number, so <see cref="StatScaleTests" /> cannot reach it.
    /// </summary>
    [Test]
    public async Task Accent_PicksSoftInsideTheStrongThreshold_AndStrongOutsideIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // Sentiment 0.36: past the dead zone, short of the 0.5 strong threshold.
            StatValue soft = new()
            {
                Value = 74, Minimum = 40, Maximum = 90
            };
            // Sentiment 0.92.
            StatValue strong = new()
            {
                Value = 1.75, Minimum = 0.4, Maximum = 1.8, NeutralLow = 0.95, NeutralHigh = 1.15
            };
            // Inside the band.
            StatValue neutral = new()
            {
                Value = 1.0, Minimum = 0.4, Maximum = 1.8, NeutralLow = 0.95, NeutralHigh = 1.15
            };
            StatValue bad = new()
            {
                Value = 0.5, Minimum = 0.4, Maximum = 1.8, NeutralLow = 0.95, NeutralHigh = 1.15
            };

            // Realised so the style setters that carry the token brushes have applied.
            _ = CaptureControl(new StackPanel
            {
                Children = { soft, strong, neutral, bad }
            }, "stats-accent-tiers");

            await Assert.That(soft.Sentiment).IsGreaterThan(0);
            await Assert.That(soft.Sentiment).IsLessThan(soft.StrongSentimentAt);
            await Assert.That(soft.Accent).IsEqualTo(soft.PositiveSoftBrush);
            await Assert.That(soft.Accent).IsNotEqualTo(soft.PositiveBrush);

            await Assert.That(strong.Accent).IsEqualTo(strong.PositiveBrush);
            await Assert.That(neutral.Accent).IsNull();
            await Assert.That(bad.Accent).IsEqualTo(bad.NegativeBrush);
        });
    }

    /// <summary>
    ///     The theme contract. Identical bytes across variants would mean a colour is held in code rather
    ///     than resolved from a token, which is exactly the regression the palette's DynamicResource
    ///     migration exists to prevent.
    /// </summary>
    [Test]
    public async Task Gallery_RepaintsWhenTheThemeVariantChanges()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                byte[] dark = CaptureAt(ThemeVariant.Dark, "stats-components-dark");
                byte[] light = CaptureAt(ThemeVariant.Light, "stats-components-light");

                await Assert.That(dark.Length).IsEqualTo(light.Length);
                await Assert.That(dark.AsSpan().SequenceEqual(light)).IsFalse();
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }
        });
    }

    /// <summary>
    ///     The inputs a scoreboard actually produces on a quiet round or a one-player lobby. Each of these
    ///     divides by a zero range, formats a null, or asks for a label wider than its slice somewhere in
    ///     the draw path.
    /// </summary>
    [Test]
    public async Task EdgeCases_RenderWithoutThrowing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Window window = BuildWindow(EdgeCases());
            WriteableBitmap? frame = Render(window);
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "stats-components-edge.png");
            frame!.Save(outPath, new PngBitmapEncoderOptions());
            Console.WriteLine($"[stats-components-edge] {outPath}");
        });
    }

    /// <summary>
    ///     A cell with no scale must look like today's plain board cell: text, no bar, no tint. That is
    ///     what keeps the change additive over all 74 catalogued columns and over user-authored ones.
    /// </summary>
    [Test]
    public async Task StatValue_WithNoDomain_PaintsNoBarAndNoAccent()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatValue bare = new()
            {
                Value = 42, Width = 90, Height = 20
            };
            StatValue scaled = new()
            {
                Value = 42, Minimum = 0, Maximum = 50, Width = 90, Height = 20
            };

            byte[] bareBytes = CaptureControl(bare, "stats-cell-nodomain");
            byte[] scaledBytes = CaptureControl(scaled, "stats-cell-scaled");

            await Assert.That(bare.Fraction).IsEqualTo(0);
            await Assert.That(bare.Sentiment).IsEqualTo(0);
            await Assert.That(bare.Accent).IsNull();

            // Same number, same box: any pixel difference is the bar and the tint the scale switched on.
            await Assert.That(bareBytes.AsSpan().SequenceEqual(scaledBytes)).IsFalse();
        });
    }

    /// <summary>
    ///     A cell with a number but no usable domain draws NO bar, not an empty one.
    ///     <para>
    ///         The track is on by default now, and a track with nothing in it reads as a measured zero.
    ///         Totals rows and uncatalogued columns are both in that state, and neither is zero: they are
    ///         simply not on a scale.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StatValue_WithNoDomain_DrawsNoTrackEither()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatValue unscaled = new()
            {
                Value = 85, Width = 90, Height = 24,
                BarTrackBrush = Brushes.Magenta, BarFillBrush = Brushes.Magenta
            };
            StatValue scaled = new()
            {
                Value = 85, Minimum = 0, Maximum = 100, Width = 90, Height = 24,
                BarTrackBrush = Brushes.Magenta, BarFillBrush = Brushes.Magenta
            };

            byte[] bare = CaptureControl(unscaled, "stats-cell-untracked");
            byte[] withBar = CaptureControl(scaled, "stats-cell-tracked");

            // Magenta because it cannot collide with the harness background, which resolves to LIGHT
            // here; Gallery_PaintsEveryTierOfTheHeatRamp has the note on why a white count proves nothing.
            await Assert.That(FrameProbe.CountPixels(bare, 0xFF00FF)).IsEqualTo(0);
            await Assert.That(FrameProbe.CountPixels(withBar, 0xFF00FF)).IsGreaterThan(500);
        });
    }

    /// <summary>
    ///     The fill anchors LEFT by default, and that is a decision rather than the enum's zero value.
    ///     A shared origin is what lets fills be compared down a column; anchoring right gives every row
    ///     its own origin. <c>Auto</c> stays available for a surface with no track to hold the number.
    /// </summary>
    [Test]
    public async Task StatValue_AnchorsItsFillLeftByDefault()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatValue cell = new()
            {
                Value = 5, Minimum = 0, Maximum = 10, TextAlignment = TextAlignment.Right
            };

            await Assert.That(cell.BarAlignment).IsEqualTo(StatBarAlignment.Left);
            await Assert.That(cell.EffectiveBarAlignment).IsEqualTo(StatBarAlignment.Left);

            // Auto still follows the text, so the escape hatch works.
            cell.BarAlignment = StatBarAlignment.Auto;
            await Assert.That(cell.EffectiveBarAlignment).IsEqualTo(StatBarAlignment.Right);
        });
    }

    [Test]
    public async Task StatValue_PublishesItsValueToAutomation()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatValue cell = new()
            {
                Value = 1.43
            };

            await Assert.That(Avalonia.Automation.AutomationProperties.GetName(cell)).IsEqualTo("1.43");
        });
    }

    /// <summary>
    ///     The in-app dev gallery (Diagnostics tab) renders and its bindings resolve. Avalonia binding
    ///     errors do not throw, so a renamed view-model property would otherwise ship a panel of empty
    ///     boxes that nobody notices until someone opens it. Every control has a product call site now,
    ///     but the gallery is still the only place they are all on one screen under one scale.
    /// </summary>
    [Test]
    public async Task DevGallery_RendersAndResolvesItsBindings()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            StatsGalleryView gallery = new();
            await Assert.That(gallery.DataContext).IsTypeOf<StatsGalleryViewModel>();

            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                // Pinned to Dark, because the colour assertion below names Dark token values and the
                // headless Default variant resolves to LIGHT. Left at Default this passes or fails on
                // which variant the harness happened to pick, which is not a test.
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                }

                Window window = BuildWindow(new ScrollViewer
                {
                    Content = gallery
                });
                window.RequestedThemeVariant = ThemeVariant.Dark;
                WriteableBitmap? frame = Render(window);
                await Assert.That(frame).IsNotNull();

                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "stats-gallery.png");
                frame!.Save(outPath, new PngBitmapEncoderOptions());
                Console.WriteLine($"[stats-gallery] {outPath}");

                // The gallery drives every control off one scale, so the ramp has to reach the frame
                // here too: that is the whole reason the panel exists.
                byte[] pixels = FrameProbe.ToBytes(frame);
                await Assert.That(FrameProbe.CountPixels(pixels, 0x4CAF50)).IsGreaterThan(0);
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }

            // Driving the scale must move the readouts, or the sliders are decoration.
            StatsGalleryViewModel vm = (StatsGalleryViewModel)gallery.DataContext!;
            string before = vm.TierText;
            vm.Value = vm.NeutralLow - ((vm.NeutralLow - vm.Minimum) / 2);
            await Assert.That(vm.TierText).IsNotEqualTo(before);
            await Assert.That(vm.TierText).Contains("bad");
        });
    }

    // ── What Render leaves behind ─────────────────────────────────────────────

    /// <summary>
    ///     Measuring after a constrained render must still report the text's NATURAL width.
    ///     <para>
    ///         Render and Measure share one cached <c>FormattedText</c>, and Render narrows it to the
    ///         cell (MaxTextWidth, then a one-line trim) so a long value ellipses instead of wrapping.
    ///         Reading Width back out of that same instance returns the ELLIPSED width, so every layout
    ///         pass measures narrower than the last and the cell walks itself down toward the width of
    ///         the ellipsis. The bug needs a second layout pass to show, which is why it survives an
    ///         eyeball on a static screenshot.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StatValue_MeasuringAfterARender_StillReportsTheNaturalWidth()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            const double hostWidth = 34;
            StatValue cell = new()
            {
                Text = "1234.5678", FontSize = 13, Height = 20
            };

            // A fixed-width column, which is what every scoreboard cell lives in.
            _ = CaptureControl(new Border
            {
                Width = hostWidth,
                Child = cell
            }, "stats-cell-measure-stability");

            cell.InvalidateMeasure();
            cell.Measure(Size.Infinity);
            double natural = cell.DesiredSize.Width;

            // Nine digits and a point cannot fit in 34px at 13pt: an unconstrained measure has to ask
            // for more than the box it was just squeezed into.
            await Assert.That(natural).IsGreaterThan(hostWidth);

            // And it must be STABLE. A single pass that happens to be right is not the property here.
            cell.InvalidateMeasure();
            cell.Measure(Size.Infinity);
            await Assert.That(cell.DesiredSize.Width).IsEqualTo(natural).Within(0.01);
        });
    }

    /// <summary>
    ///     A cell that loses its accent must lose the colour with it.
    ///     <para>
    ///         The brush is baked into the cached text, and Render only ever pushed a NON-null accent
    ///         into it. So a cell that was green and then fell back into the neutral band (a re-sort, a
    ///         rebuilt scale, a column whose colour gate closed) kept painting the judgement it no
    ///         longer holds. Withdrawing a colour is as much a state as applying one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StatValue_WhenTheAccentIsWithdrawn_RepaintsWithoutIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // Magenta, because no palette token is near it: any hit is this brush and nothing else.
            const uint accentRgb = 0xFF00FF;
            StatValue cell = new()
            {
                Mode = StatValueMode.Plain,
                Value = 1.8, Minimum = 0, Maximum = 2, Format = "0.00",
                FontSize = 22, Width = 90, Height = 32,
                PositiveBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)),
                Foreground = Brushes.White
            };

            Window window = BuildWindow(cell);
            WriteableBitmap accented = Render(window)!;
            int before = FrameProbe.CountPixels(FrameProbe.ToBytes(accented), accentRgb);
            await Assert.That(before).IsGreaterThan(0);

            // The whole domain becomes the dead zone, so the same value is now judged neutral.
            cell.NeutralLow = 0;
            cell.NeutralHigh = 2;
            await Assert.That(cell.Accent).IsNull();

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap plain = window.CaptureRenderedFrame()!;
            plain.Save(Path.Combine(HeadlessSession.ArtifactDir, "stats-cell-accent-withdrawn.png"), new PngBitmapEncoderOptions());

            await Assert.That(FrameProbe.CountPixels(FrameProbe.ToBytes(plain), accentRgb)).IsEqualTo(0);
        });
    }

    /// <summary>
    ///     Two different counts must not paint identically.
    ///     <para>
    ///         The strip stopped drawing marks at the edge of its box, so in the 92px column it was
    ///         given, nine, ten, eleven and twelve were all eight dots: four distinct facts rendered as
    ///         one picture, with nothing on screen admitting that anything had been dropped. The cap it
    ///         declares (MaxPips) was never the real limit; the arranged width was.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PipStrip_TooNarrowForItsMarks_WritesTheNumberInstead()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // Room for about four marks, well inside the declared cap of twelve.
            byte[] nine = CaptureControl(Pips(9, 60), "stats-pips-narrow-9");
            byte[] twelve = CaptureControl(Pips(12, 60), "stats-pips-narrow-12");

            const uint ink = 0xFF00FF;
            int nineInk = FrameProbe.CountPixels(nine, ink);
            int twelveInk = FrameProbe.CountPixels(twelve, ink);

            await Assert.That(nineInk).IsGreaterThan(0);
            await Assert.That(twelveInk).IsGreaterThan(0);
            await Assert.That(nineInk).IsNotEqualTo(twelveInk);

            // Given the room, marks are still marks: more of them is more ink.
            int five = FrameProbe.CountPixels(CaptureControl(Pips(5, 220), "stats-pips-wide-5"), ink);
            int nineWide = FrameProbe.CountPixels(CaptureControl(Pips(9, 220), "stats-pips-wide-9"), ink);
            await Assert.That(nineWide).IsGreaterThan(five);
        });
    }

    /// <summary>A strip with an unmistakable ink colour, in a box of the given width.</summary>
    private static Border Pips(int count, double width) =>
        new()
        {
            Width = width,
            Child = new PipStrip
            {
                Count = count,
                Height = 18,
                FontSize = 13,
                PipBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF))
            }
        };

    /// <summary>
    ///     Both arms reach the frame, each in its own colour, and both measured against the shared
    ///     extent rather than against each other.
    ///     <para>
    ///         <see cref="DivergingBar" /> was the one control neither fixture held, so nothing said
    ///         whether it painted at all. It is also the control that replaced four numeric columns with
    ///         one shape, which makes "it painted, and the longer count is the longer arm" the entire
    ///         claim it makes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DivergingBar_PaintsAnArmPerSide_AgainstTheSharedExtent()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // Pure green and pure magenta: nothing in either palette is within reach of either, so a
            // hit is this bar's arm and not the chrome behind it.
            const uint won = 0x00FF00;
            const uint lost = 0xFF00FF;

            byte[] both = CaptureControl(Duels(9, 5), "stats-duel-both-arms");
            int wonInk = FrameProbe.CountPixels(both, won);
            int lostInk = FrameProbe.CountPixels(both, lost);
            await Assert.That(wonInk).IsGreaterThan(0);
            await Assert.That(lostInk).IsGreaterThan(0);
            await Assert.That(wonInk).IsGreaterThan(lostInk);

            // A clean sweep draws the one arm it has and nothing on the other side of the line.
            byte[] sweep = CaptureControl(Duels(6, 0), "stats-duel-one-arm");
            await Assert.That(FrameProbe.CountPixels(sweep, won)).IsGreaterThan(0);
            await Assert.That(FrameProbe.CountPixels(sweep, lost)).IsEqualTo(0);

            // A player who fought no opening duels still occupies a row on a live column scale: the
            // break-even line and nothing else, not a bar of some default length.
            byte[] none = CaptureControl(Duels(0, 0), "stats-duel-no-duels");
            await Assert.That(FrameProbe.CountPixels(none, won)).IsEqualTo(0);
            await Assert.That(FrameProbe.CountPixels(none, lost)).IsEqualTo(0);
        });
    }

    /// <summary>A duel bar in unmistakable colours, on the shared extent a real column would pass it.</summary>
    private static DivergingBar Duels(double positive, double negative) =>
        new()
        {
            Positive = positive, Negative = negative, Extent = 12,
            Width = 220, Height = 22,
            PositiveBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)),
            NegativeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF))
        };

    /// <summary>
    ///     One unusable bullet must not take the whole plot with it.
    ///     <para>
    ///         Both series share ONE scale computed over both, so a non-finite sample reaching
    ///         <c>extent</c> flows into <c>scale</c> and from there into every projected point of BOTH
    ///         traces: one bad bullet and the reference pattern disappears with it. A blank square reads
    ///         as "no spray recorded for this weapon", which is a different answer and a wrong one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SprayPlot_WithANonFiniteSample_DrawsEveryOtherOne()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            const uint shot = 0xFF00FF;
            SprayPatternPoint[] pattern =
            [
                new(0, 0f, 0f, 8), new(1, 0.4f, -1.2f, 8), new(2, 0.9f, -2.6f, 8),
                new(3, 1.7f, -4.1f, 8)
            ];
            SpraySample[] landed =
            [
                new(0, 0, 0.1f, -0.2f), new(1, 1, 0.5f, -1.4f), new(2, 2, 1.1f, -2.9f)
            ];
            SpraySample[] poisoned = [.. landed, new SpraySample(3, 3, float.NaN, float.NaN)];

            byte[] clean = CaptureControl(Spray(pattern, landed), "stats-spray-clean");
            byte[] withNaN = CaptureControl(Spray(pattern, poisoned), "stats-spray-nan");

            int ink = FrameProbe.CountPixels(clean, shot);
            await Assert.That(ink).IsGreaterThan(0);

            // The NaN bullet cannot be drawn, but nothing else changes: same scale, same pattern, same
            // three bullets. Identical ink is the assertion, not merely "some ink survived".
            await Assert.That(FrameProbe.CountPixels(withNaN, shot)).IsEqualTo(ink);
        });
    }

    /// <summary>A plot in an unmistakable ink colour, sized as the player-details card sizes it.</summary>
    private static SprayPlot Spray(
        IReadOnlyList<SprayPatternPoint> pattern, IReadOnlyList<SpraySample> shots) =>
        new()
        {
            Width = 300, Height = 300,
            Pattern = pattern,
            Shots = shots,
            PatternBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x80)),
            ShotBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF))
        };

    // ── Where the cards land ──────────────────────────────────────────────────

    /// <summary>
    ///     No two cards on the player-details Overview board may claim the same cell.
    ///     <para>
    ///         A Grid child with no <c>Grid.Row</c> silently lands in row 0, and an opaque card there
    ///         paints over whatever row 0 already held. That is what the Spray card did to the Form card:
    ///         a whole card's worth of content hidden, with no binding error, no exception, and nothing
    ///         in the markup that looks wrong next to its neighbours. Only a capture would have caught
    ///         it, and only if somebody scrolled; counting the cells is cheaper and needs no demo.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PlayerDetailsOverview_GivesEveryCardItsOwnCell()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // No DataContext: the cards are declared in markup, so the layout under test is there
            // whether or not a player has been opened.
            PlayerDetailsView view = new();
            _ = CaptureControl(view, "player-details-overview-cells");

            Grid[] boards = view.GetVisualDescendants().OfType<Grid>()
                .Where(g => g.Children.OfType<Border>().Count(IsCard) > 1)
                .ToArray();
            await Assert.That(boards.Length).IsEqualTo(1);

            Grid board = boards[0];
            Dictionary<(int Row, int Column), string> occupied = new();
            List<string> clashes = [];
            foreach (Border card in board.Children.OfType<Border>().Where(IsCard))
            {
                string name = CardName(card);
                int row = Grid.GetRow(card);
                int column = Grid.GetColumn(card);
                for (int r = row; r < row + Grid.GetRowSpan(card); r++)
                {
                    for (int c = column; c < column + Grid.GetColumnSpan(card); c++)
                    {
                        if (occupied.TryGetValue((r, c), out string? held))
                        {
                            clashes.Add($"'{name}' lands on '{held}' at row {r}, column {c}");
                        }
                        else
                        {
                            occupied[(r, c)] = name;
                        }
                    }
                }
            }

            await Assert.That(clashes).IsEmpty();

            // And the grid has to DECLARE every row it hands out. Avalonia squeezes a child placed past
            // the last RowDefinition into that last row, which is the same overlap by another route.
            int deepest = occupied.Keys.Max(cell => cell.Row);
            await Assert.That(board.RowDefinitions.Count).IsGreaterThanOrEqualTo(deepest + 1);
        });
    }

    private static bool IsCard(Border border) => border.Classes.Contains("pdCard");

    /// <summary>A card's header, so a clash names the two cards rather than two cell indices.</summary>
    private static string CardName(Border card) =>
        card.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("pdCardHeader"))?.Text ?? "unnamed card";

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>Every control in every mode, with values shaped like a real scoreboard column.</summary>
    private static StackPanel Gallery()
    {
        double[] ratings = [1.43, 1.06, 0.91, 0.86, 0.76];
        StatScale hltv = StatScale.Banded(0.76, 1.43, 0.95, 1.10);
        StatScale aim = StatScale.Absolute(0, 100, 45, 65);

        StackPanel rows = new()
        {
            Spacing = 4
        };

        foreach (double r in ratings)
        {
            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new StatValue
                    {
                        Value = r, Scale = hltv, Width = 86, Height = 18,
                        IsLeader = Math.Abs(r - 1.43) < 0.001
                    },
                    new StatValue
                    {
                        Value = r * 60, Scale = aim, Width = 86, Height = 18, Format = "0"
                    },
                    new StatValue
                    {
                        Value = r * 50, Scale = aim, Width = 60, Height = 18, Format = "0",
                        Classes = { "chip" }
                    },
                    new SegmentedBar
                    {
                        Width = 130, Height = 14, Compact = true, FontSize = 9,
                        Segments =
                        [
                            new StatSegment(8, null, "8"), new StatSegment(11, null, "11"),
                            new StatSegment(11, null, "11"), new StatSegment(10, null, "10")
                        ]
                    }
                }
            });
        }

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TeamBadge
                {
                    Team = TeamBadge.TeamCt, Label = "My Team", Outcome = TeamOutcome.Win,
                    Detail = "13 : 9", HorizontalAlignment = HorizontalAlignment.Left
                },
                rows,
                new SegmentedBar
                {
                    Height = 26,
                    Segments =
                    [
                        new StatSegment(73, Brushes.Teal, "28%"), new StatSegment(63, Brushes.RoyalBlue, "24%"),
                        new StatSegment(70, Brushes.Chocolate, "27%"), new StatSegment(55, Brushes.Firebrick, "21%")
                    ]
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new StatTile
                        {
                            Label = "ADR", Value = 87, Minimum = 40, Maximum = 100,
                            NeutralLow = 65, NeutralHigh = 85, IsHero = true
                        },
                        new StatTile
                        {
                            Label = "K/D", Value = 1.75, Minimum = 0.4, Maximum = 1.8,
                            NeutralLow = 0.95, NeutralHigh = 1.15, Caption = "+0.31"
                        },
                        new StatTile
                        {
                            Label = "KAST", Value = 74, Minimum = 40, Maximum = 90, Format = "0"
                        },
                        new RingGauge
                        {
                            Value = 11.96, Minimum = -12, Maximum = 12, NeutralLow = -0.5,
                            NeutralHigh = 0.5, Format = "+0.0;-0.0;0", Caption = "RTG"
                        },
                        new RingGauge
                        {
                            Value = -8.57, Minimum = -12, Maximum = 12, NeutralLow = -0.5,
                            NeutralHigh = 0.5, Format = "+0.0;-0.0;0", Caption = "RTG"
                        }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        new Sparkline
                        {
                            Width = 180, Values = [0, 2, 1, 3, 0, 1, 4, 2, 0, 1, 2, 3]
                        },
                        new Sparkline
                        {
                            Width = 180, Mode = SparklineMode.Bars,
                            Values = [12, 80, 45, 130, 0, 66, 150, 90, 20, 40, 75, 110]
                        },
                        new Sparkline
                        {
                            Width = 180, Mode = SparklineMode.Dots,
                            Values = [1, 1, 0, 1, 0, 0, 1, 1, 1, 0, 1, 1]
                        }
                    }
                },
                // Opening duels for two players on ONE extent, which is the only arrangement in which
                // the arms are comparable down a column.
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        new DivergingBar
                        {
                            Width = 150, Positive = 9, Negative = 5, Extent = 12
                        },
                        new DivergingBar
                        {
                            Width = 150, Positive = 3, Negative = 11, Extent = 12
                        }
                    }
                }
            }
        };
    }

    /// <summary>The inputs that break a naive draw path.</summary>
    private static StackPanel EdgeCases() =>
        new StackPanel
        {
            Spacing = 6,
            Children =
            {
                // Every peer identical: Min == Max, so the bar domain has no width.
                new StatValue
                {
                    Value = 5, Minimum = 5, Maximum = 5, Width = 90, Height = 18
                },
                // No value at all, and a value with no text to draw.
                new StatValue
                {
                    Value = null, Width = 90, Height = 18
                },
                new StatValue
                {
                    Value = double.NaN, Minimum = 0, Maximum = 10, Width = 90, Height = 18
                },
                // Six digits in a 48px column: the trimming path.
                new StatValue
                {
                    Value = 123456.789, Minimum = 0, Maximum = 200000, Width = 48, Height = 18
                },
                // Negative zero, and a value far outside its domain.
                new StatValue
                {
                    Value = -0.0, Minimum = -1, Maximum = 1, Width = 90, Height = 18
                },
                new StatValue
                {
                    Value = 9999, Minimum = 0, Maximum = 10, Width = 90, Height = 18
                },
                // Nothing to apportion, and slices far too narrow for their labels.
                new SegmentedBar
                {
                    Height = 20, Segments = []
                },
                new SegmentedBar
                {
                    Height = 20, Segments = [new StatSegment(0), new StatSegment(-4, null, "bad")]
                },
                new SegmentedBar
                {
                    Width = 40, Height = 12,
                    Segments =
                    [
                        new StatSegment(1, null, "wide label"), new StatSegment(1, null, "another"),
                        new StatSegment(1, null, "third")
                    ]
                },
                // Empty, single-point and all-equal series: the divide-by-zero shapes.
                new Sparkline
                {
                    Width = 120, Values = []
                },
                new Sparkline
                {
                    Width = 120, Values = [7]
                },
                new Sparkline
                {
                    Width = 120, Values = [3, 3, 3, 3]
                },
                new Sparkline
                {
                    Width = 120, Mode = SparklineMode.Bars, Values = [0, 0, 0]
                },
                // A player who fought no opening duels (nothing to scale against), and a bar left to
                // scale against itself, which is the self-scaling fallback nobody should ship.
                new DivergingBar
                {
                    Width = 150, Positive = 0, Negative = 0, Extent = 12
                },
                new DivergingBar
                {
                    Width = 150, Positive = 1, Negative = 0
                },
                // A full ring (the sweep that collapses start onto end) and one with no value.
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new RingGauge
                        {
                            Value = 100, Minimum = 0, Maximum = 100, Caption = "full"
                        },
                        new RingGauge
                        {
                            Value = null, Caption = "none"
                        },
                        new RingGauge
                        {
                            Value = 50, Minimum = 0, Maximum = 100, Width = 14, Height = 14,
                            Thickness = 20
                        }
                    }
                },
                // A team that is neither side, with no outcome to show.
                new TeamBadge
                {
                    Team = 0, Label = "Spectators", HorizontalAlignment = HorizontalAlignment.Left
                },
                new StatTile
                {
                    Label = "no value", Value = null
                }
            }
        };

    // ── Harness ───────────────────────────────────────────────────────────────

    private static byte[] CaptureAt(ThemeVariant variant, string name)
    {
        // The variant goes on the Application, not the Window: the palette resolves through
        // ResourceDictionary.ThemeDictionaries and the UiCapture host learned the same lesson (L1).
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = variant;
        }

        Window window = BuildWindow(Gallery());
        window.RequestedThemeVariant = variant;
        WriteableBitmap frame = Render(window)!;
        frame.Save(Path.Combine(HeadlessSession.ArtifactDir, $"{name}.png"), new PngBitmapEncoderOptions());
        return FrameProbe.ToBytes(frame);
    }

    private static byte[] CaptureControl(Control control, string name)
    {
        Window window = BuildWindow(control);
        WriteableBitmap frame = Render(window)!;
        frame.Save(Path.Combine(HeadlessSession.ArtifactDir, $"{name}.png"), new PngBitmapEncoderOptions());
        return FrameProbe.ToBytes(frame);
    }

    private static Window BuildWindow(Control content) =>
        new()
        {
            Width = 900,
            Height = 460,
            Content = new Border
            {
                Padding = new Thickness(12),
                Child = content
            }
        };

    private static WriteableBitmap? Render(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window.CaptureRenderedFrame();
    }


    /// <summary>
    ///     The Dark values of the four heat-ramp tokens, as they are authored in DarkPalette.axaml. Pinned
    ///     here on purpose: if someone retunes a token, this test fails and the capture gets re-read,
    ///     which is the review step the ramp deserves.
    /// </summary>
    private static readonly (string Token, uint Hex)[] DarkRamp =
    [
        ("StatPositive", 0x4CAF50),
        ("StatPositiveSoft", 0x5FA894),
        ("StatNegativeSoft", 0xD98A3F),
        ("StatNegative", 0xDC5A52)
    ];


}
