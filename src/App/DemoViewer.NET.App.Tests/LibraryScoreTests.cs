#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Final-score extraction + card display. The extraction is validated against a REAL demo (CCSTeam
///     m_iScore entity-replayed to match end) with structural asserts — a plausible scoreboard, not a pinned
///     value — and skips when no demo is present. The display logic (HasScore / HasClans / subtitle) is a pure
///     unit test.
/// </summary>
[NotInParallel]
public class LibraryScoreTests
{
    // ── Real-demo extraction (gated) ──────────────────────────────────────────

    [Test]
    [Category("Integration")]
    public async Task ExtractFinalScore_RealDemo_ProducesPlausibleScoreboard()
    {
        string[] candidates =
        [
            "/Users/austingray/Development/DemoViewer.NET/demos/match730_003826256877184877003_0981591541_410.dem",
            "/Users/austingray/Development/DemoViewer.NET/demos/pro-demos/vitality-vs-fut-m2-dust2.dem"
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            throw new SkipTestException("no score-extraction demo present");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        (int? ct, int? t, string? _, string? _) = DemoLibraryService.ExtractFinalScore(parsed);

        // A real completed match yields both sides, non-negative, summing to a plausible round count.
        await Assert.That(ct).IsNotNull();
        await Assert.That(t).IsNotNull();
        await Assert.That(ct!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(t!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(ct.Value + t.Value).IsGreaterThanOrEqualTo(3);
        await Assert.That(ct.Value + t.Value).IsLessThanOrEqualTo(60); // sane upper bound (incl. long OT)
    }

    // ── Display logic (pure) ──────────────────────────────────────────────────

    [Test]
    public async Task HasScore_TrueOnlyWhenBothSidesPresentAndNonWarmup()
    {
        DemoEntry e = Entry();

        await Assert.That(e.HasScore).IsFalse(); // nothing set

        e.CtScore = 13; // only one side
        await Assert.That(e.HasScore).IsFalse();

        e.TScore = 11;
        await Assert.That(e.HasScore).IsTrue();

        e.CtScore = 0;
        e.TScore = 0; // warmup-only: both zero
        await Assert.That(e.HasScore).IsFalse();
    }

    [Test]
    public async Task Subtitle_ShowsClanMatchupWhenPresent_ElseServer()
    {
        DemoEntry e = Entry();
        e.ServerName = "BLAST.tv Premier CS2 Server";

        // No clans (matchmaking) → subtitle is the server name.
        await Assert.That(e.HasClans).IsFalse();
        await Assert.That(e.SubtitleDisplay).IsEqualTo("BLAST.tv Premier CS2 Server");

        // Clans present (pro/HLTV) → subtitle is the matchup, CT side first.
        e.CtClan = "Vitality";
        e.TClan = "FUT";
        await Assert.That(e.HasClans).IsTrue();
        await Assert.That(e.SubtitleDisplay).IsEqualTo("Vitality vs FUT");
    }

    private static DemoEntry Entry() => new()
    {
        FilePath = "/demos/x.dem",
        FileName = "x.dem",
        Directory = "/demos",
        FileSizeBytes = 1000,
        Modified = new DateTime(2026, 7, 1)
    };
}
