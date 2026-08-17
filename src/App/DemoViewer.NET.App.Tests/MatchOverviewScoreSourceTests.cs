#region

using System.Globalization;
using Avalonia.Threading;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using Cs2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.Shell;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Match Overview's final score comes from the ANALYSIS ENGINE — per-team round wins, i.e. the rounds
///     each team won regardless of which side it was on. This gate sweeps every demo it can find and pins the
///     internal consistency of that score (totals reconcile with the round count and with the side split when
///     one is shown), that a completed match reaches a legal premier result, plus team naming.
///     <para>
///         <b>The defect this gate exists for.</b> Round wins are counted off the <c>$round_end</c> logical
///         event, and the GOTV profile bound it to <c>round_officially_ended</c> plus the terminal
///         <c>cs_win_panel_match</c>. Which per-round marker a demo actually carries depends on the
///         RECORDING SERVER, not the profile: third-party tournament servers (ESL, BLAST) emit only
///         <c>cs_pre_restart</c>, yet they record through SourceTV and so classify as
///         <c>GotvMatchmaking</c> off their <c>SourceTV Demo</c> client name — indistinguishable from Valve
///         matchmaking in the header. Measured: furia-vs-vitality fires <c>round_officially_ended</c> zero
///         times across 15 rounds, so only the once-per-match terminal marker ever fired and the match
///         scored 0–1. Fixed by <c>Cs2GotvPreRestartProfile</c>, selected off the events the demo actually
///         contains rather than off the header.
///     </para>
///     <para>
///         The fix is a separate PROFILE and not an extra event on the GOTV binding because a profile's
///         <c>$round_end</c> events must be mutually exclusive per round: the v2 <c>count:</c> planner
///         emits one unguarded increment per concrete event, so listing both markers double-counted every
///         Valve round (a 16-round match scored 26–6 across "32" rounds).
///     </para>
///     <para>
///         It deliberately does NOT compare against the Library card badge. That badge reads
///         <c>CCSTeam.m_iScore</c>, which loses the winning team's final round on a demo cut at the buzzer —
///         both pro demos here report a 12 that cannot be a completed premier result.
///     </para>
///     <para>
///         The gate therefore sweeps EVERY demo it can find, not just the reference one: on the reference demo
///         alone the old and new implementations agree, so a single-demo test would happily pass against the
///         bug. A pro demo is what discriminates, and the sweep says so out loud when it doesn't find one.
///     </para>
/// </summary>
[NotInParallel]
public class MatchOverviewScoreSourceTests
{
    // Searched in order; every one that exists is swept. Pro demos carry clan names and are the ones that
    // actually discriminate, so they lead. Both tournament families are covered on several maps each:
    // one demo per family would not show whether the dialect selection is family-wide or a fluke of one
    // recording (furia m3-nuke starts mid-match and has no pre-match period, so on its own it never
    // exercises the pre-match guard at all).
    //
    // vitality-vs-fut-m3-nuke is the match-restart case: the server restarts after a warmup/knife round
    // (`begin_new_match` fires TWICE, ticks 346 and 4506). Before the evaluator's match-restart reset it
    // scored 14-11 across "25" rounds — the knife round counted into the real match — and was excluded
    // here; it now pins the reset (13-11 over 24, the real result).
    private static readonly string[] _candidates =
    [
        "demos/pro-demos/furia-vs-vitality-m1-mirage.dem",
        "demos/pro-demos/furia-vs-vitality-m2-inferno.dem",
        "demos/pro-demos/furia-vs-vitality-m3-nuke.dem",
        "demos/pro-demos/furia-vs-vitality-m4-overpass.dem",
        "demos/pro-demos/vitality-vs-fut-m1-mirage.dem",
        "demos/pro-demos/vitality-vs-fut-m2-dust2.dem",
        "demos/pro-demos/vitality-vs-fut-m3-nuke.dem",
        "demos/benchmarks/003816248937665266002_0544286934.dem",
        "demos/003802730901763260580_1218921269.dem"
    ];

    [Test]
    public async Task FinalScore_IsAConsistentCompletedResult_OnEveryAvailableDemo()
    {
        string? repoRoot = FindRepoRoot();
        List<string> demos = repoRoot is null
            ? []
            : _candidates.Select(c => Path.Combine(repoRoot, c)).Where(File.Exists).ToList();

        if (demos.Count == 0)
        {
            throw new SkipTestException("no demo files available — the score sweep needs at least one");
        }

        int discriminating = 0;
        foreach (string demo in demos)
        {
            // The Library card's source, computed directly: CCSTeam.m_iScore replayed to the last frame.
            ParsedDemo parsed = DemoParser.Parse(await File.ReadAllBytesAsync(demo));
            (int? libCt, int? libT, string? ctClan, string? tClan) = DemoLibraryService.ExtractFinalScore(parsed);
            if (libCt is null || libT is null)
            {
                continue; // warmup-only / team-less demo — the card omits the score, so there is nothing to match
            }

            if (!string.IsNullOrWhiteSpace(ctClan))
            {
                discriminating++;
            }

            await HeadlessSession.RunOnUi(async () =>
            {
                MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty());
                try
                {
                    await vm.LoadDemoFromPathAsync(demo);

                    // The score resolves off the load path (library entry, or a gated replay) — wait for it.
                    for (int i = 0; i < 200 && !vm.MatchOverviewTab.HasScore; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        await Task.Delay(50);
                    }

                    Dispatcher.UIThread.RunJobs();
                    MatchOverviewTabViewModel o = vm.MatchOverviewTab;
                    string name = Path.GetFileName(demo);

                    using (Assert.Multiple())
                    {
                        await Assert.That(o.HasScore).IsTrue().Because($"{name}: the score must resolve");

                        // Team totals must account for every round the analysis counted — an attribution
                        // error shows up here even when each individual number looks plausible.
                        int ct = int.Parse(o.CtTeamScoreDisplay, CultureInfo.InvariantCulture);
                        int t = int.Parse(o.TTeamScoreDisplay, CultureInfo.InvariantCulture);
                        await Assert.That(o.RoundCountDisplay)
                            .IsEqualTo((ct + t).ToString(CultureInfo.InvariantCulture))
                            .Because($"{name}: rounds must reconcile with the team totals");

                        if (o.HasSideSplit)
                        {
                            int sideCt = int.Parse(o.CtSideWinsDisplay, CultureInfo.InvariantCulture);
                            int sideT = int.Parse(o.TSideWinsDisplay, CultureInfo.InvariantCulture);
                            await Assert.That(sideCt + sideT).IsEqualTo(ct + t)
                                .Because($"{name}: a shown side split must cover every round");
                        }

                        // Team NAMES are read off the library entry (populated by the open's own fan-out),
                        // so a shell with an empty library legitimately falls back to the side labels. The
                        // naming path is covered where a library exists; here we only pin the fallback shape.
                        await Assert.That(o.CtTeamLabel).IsNotEmpty();
                        await Assert.That(o.TTeamLabel).IsNotEmpty();

                        // The assertion the round-end fix has to survive. Every demo swept here is a
                        // COMPLETE match, so the winner must land on a legal premier result (13 in
                        // regulation, 15 drawn OT, 16 OT win). This is what the declared-but-silent
                        // round-end candidate used to break: the affected demos scored 0–1, and this
                        // line — not the consistency checks above — is what says so.
                        await Assert.That(o.ScoreLooksComplete).IsTrue()
                            .Because($"{name}: {ct}–{t} is not a completed premier result — the round-win "
                                     + "count is wrong (a silent $round_end candidate shadowing a live one "
                                     + "is the known failure mode)");
                    }

                    Console.WriteLine($"[score-sweep] {name,-46} {libCt}:{libT}  "
                                      + $"rounds={o.RoundCountDisplay} split={o.HasSideSplit} "
                                      + $"labels={o.CtTeamLabel}/{o.TTeamLabel}");
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }

        if (discriminating == 0)
        {
            // Not a failure — but say it plainly, because without a pro demo this gate cannot tell the
            // authoritative score from the analysis-derived one that used to be wrong.
            Console.WriteLine("[score-sweep] WARNING: no clan-carrying (pro/HLTV) demo was available — this "
                              + "run could not discriminate the authoritative score from the analysis-derived one.");
        }
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
