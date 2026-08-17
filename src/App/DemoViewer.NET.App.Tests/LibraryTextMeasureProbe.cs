#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DemoViewer.NET.Modules.Library;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Regression for the fast-scroll library crash (reported 2026-07-09): Avalonia's
///     line-wrap splitter throws "Cannot split: requested length N consumes entire run"
///     (ShapedTextRun.Split) on degenerate player names — invisible bidi-isolate format chars plus
///     an orphaned combining mark. The card's players TextBlock wraps, and virtualized scrolling
///     re-measures cards at realize time, so one such name crashed the whole app. The fix is
///     display-boundary sanitization (<see cref="DisplayText.Sanitize" /> in
///     <c>DemoEntry.PlayersDisplay</c>); this test measures the EXACT card configuration over the
///     exact string from the library that hit it, at every width, and must never throw.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class LibraryTextMeasureProbe
{
    // The real string that crashed the app: player "ุ⁧⁧Vetxed" = orphaned Thai combining mark
    // U+0E38 + two U+2067 RIGHT-TO-LEFT-ISOLATE format chars. Unsanitized, it throws at width≈87.
    private static readonly string[] _poisonPlayers =
    [
        "Jonah High", "D3LCH", "Flex", "D8", "ุ⁧⁧Vetxed", "Little Michael Jackson",
        "Krunch bar", "SackBoy", "aubbby", "JustCole", "DemoRecorder"
    ];

    [Test]
    public async Task PoisonPlayerNames_MeasureCleanAtEveryCardWidth()
    {
        DemoEntry entry = new()
        {
            FilePath = "/demos/p.dem",
            FileName = "p.dem",
            Directory = "/demos",
            FileSizeBytes = 1,
            Modified = new DateTime(2026, 7, 9),
            MapName = "de_dust2",
            Players = _poisonPlayers,
            State = DemoIndexState.Indexed
        };

        string display = entry.PlayersDisplay;
        Exception? failure = null;
        int measured = 0;

        await HeadlessSession.RunOnUi(() =>
        {
            try
            {
                for (double width = 40; width <= 320; width += 1)
                {
                    // EXACT card players-block configuration (LibraryTabView.axaml).
                    TextBlock tb = new()
                    {
                        Text = display,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    tb.Measure(new Size(width, double.PositiveInfinity));
                    measured++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });

        Console.WriteLine($"[textprobe] display=\"{display[..40]}…\" measured={measured} failure={failure?.Message}");
        await Assert.That(failure).IsNull().Because(failure?.ToString() ?? "no failure");
        await Assert.That(measured).IsEqualTo(281);

        // The sanitizer removed the invisible format chars and nothing visible.
        await Assert.That(display).Contains("Vetxed");
        await Assert.That(display.Contains('⁧')).IsFalse();
    }

    /// <summary>
    ///     Opt-in corpus sweep (DEMOVIEWER_TEXTPROBE=/path/to/strings.txt, one player-list string
    ///     per line): every SANITIZED string must measure clean at card widths. Use against a real
    ///     library's cached player lists when hunting new poison strings.
    /// </summary>
    [Test]
    public async Task Corpus_SanitizedStrings_MeasureClean()
    {
        string? path = Environment.GetEnvironmentVariable("DEMOVIEWER_TEXTPROBE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new SkipTestException("DEMOVIEWER_TEXTPROBE not set");
        }

        string[] strings = await File.ReadAllLinesAsync(path);
        List<string> failures = new();
        int measured = 0;

        await HeadlessSession.RunOnUi(() =>
        {
            foreach (string raw in strings)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string s = DisplayText.Sanitize(raw);
                for (double width = 60; width <= 300; width += 4)
                {
                    TextBlock tb = new()
                    {
                        Text = s,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    try
                    {
                        tb.Measure(new Size(width, double.PositiveInfinity));
                        measured++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"width={width} msg={ex.Message} text={raw}");
                        break;
                    }
                }
            }

            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });

        Console.WriteLine($"[textprobe corpus] measured={measured} failures={failures.Count}");
        foreach (string f in failures.Take(10))
        {
            Console.WriteLine($"[textprobe corpus] FAIL {f}");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures.Take(5)));
    }
}
