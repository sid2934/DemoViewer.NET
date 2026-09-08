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
using DemoViewer.NET.Controls.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Render smoke for the stats component library (docs/ui/stats-components.md). The maths is proved
///     without a harness in <see cref="StatScaleTests" />; what needs a renderer is the half that cannot
///     be unit tested: that each control actually paints, that it paints DIFFERENTLY under Dark and
///     Light, and that the inputs which would throw in a naive implementation do not.
///     <para>
///         The theme assertion is the load-bearing one. These six controls draw with
///         <c>DrawingContext</c> and hold no brush in code, so every colour arrives as a styled property
///         fed by <c>{DynamicResource}</c> from <c>Styles/Stats.axaml</c>. If one of them ever caches a
///         brush or resolves a token statically, Dark and Light stop differing and this test is what
///         says so.
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
                frame!.Save(outPath);
                byte[] pixels = ToBytes(frame);

                foreach ((string token, uint hex) in DarkRamp)
                {
                    int hits = CountPixels(pixels, hex);
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
            frame!.Save(outPath);
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
        frame.Save(Path.Combine(HeadlessSession.ArtifactDir, $"{name}.png"));
        return ToBytes(frame);
    }

    private static byte[] CaptureControl(Control control, string name)
    {
        Window window = BuildWindow(control);
        WriteableBitmap frame = Render(window)!;
        frame.Save(Path.Combine(HeadlessSession.ArtifactDir, $"{name}.png"));
        return ToBytes(frame);
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
    ///     The frame as bytes, NORMALISED to B,G,R,A order whatever the platform framebuffer declares.
    ///     Worth the extra step: the headless Skia surface hands back RGBA here, and a colour assertion
    ///     that assumes BGRA still passes for any colour whose red and blue happen to be close (#4CAF50
    ///     is, #5FA894 is not), so the swap fails silently on exactly one of the four ramp tokens.
    /// </summary>
    private static byte[] ToBytes(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        PixelFormat? format;
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
            format = fb.Format;
        }

        if (format == PixelFormat.Rgba8888)
        {
            for (int i = 0; i + 3 < buffer.Length; i += 4)
            {
                (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
            }
        }

        return buffer;
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

    /// <summary>
    ///     Counts pixels near one RGB value in a BGRA frame. Near, not exact: 11px anti-aliased glyphs
    ///     and a 3px round-capped arc leave few fully-covered pixels at the pure token value. The
    ///     tolerance is well inside the smallest gap between the four ramp entries, so a hit for one tier
    ///     can never be a near-miss of another.
    /// </summary>
    private static int CountPixels(byte[] buffer, uint rgb, int tolerance = 20)
    {
        int wantR = (byte)(rgb >> 16), wantG = (byte)(rgb >> 8), wantB = (byte)rgb;
        int hits = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (Math.Abs(buffer[i] - wantB) <= tolerance
                && Math.Abs(buffer[i + 1] - wantG) <= tolerance
                && Math.Abs(buffer[i + 2] - wantR) <= tolerance)
            {
                hits++;
            }
        }

        return hits;
    }

}
