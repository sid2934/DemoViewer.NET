#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shipped rulesets load together, and a scoreboard column is keyed by nothing but its
///     <c>label:</c>. The engine unions every ruleset's columns into ONE game table and ONE round
///     table by label, and the projector writes each player's cell with plain assignment, so two
///     rulesets that put the same label on the same board do not produce two columns: the one
///     materialized later silently overwrites the other, the header looks right, and every number in
///     it is plausible. Nothing at load, check or render time says so. "Later" is
///     <c>RulesetComposition.OrderByDependency</c>: a depth-first walk in file-name order that places
///     each <c>use:</c> target before its user, so a <c>use:</c> edge can reorder two files without
///     either being renamed. The same overwrite happens when ONE ruleset lists a label twice on a
///     board, so claimants are counted per entry rather than per ruleset.
///     <para>
///         Coverage-skipped entries are not dropped the way <c>ShowLowering.LowerScoreboard</c>
///         drops them. That can only add a phantom claimant, never hide a collision, so it errs on
///         the side of a red test.
///     </para>
///     <para>
///         The same label on DIFFERENT boards is fine and deliberate: kast's per-round counters and
///         player_stats' per-match totals share FlashAst, WB, Smoke and Shots because they are the
///         same quantity at two scopes, and a reader expects one name for it.
///     </para>
/// </summary>
public class ShippedRulesetLabelTests
{
    /// <summary>No label is claimed twice on one board across the shipped rulesets' show: blocks.</summary>
    [Test]
    public async Task ScoreboardLabels_AreUniquePerBoard_AcrossTheShippedRulesets()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(Path.Combine(root, "rules"));
        await Assert.That(loaded.Success).IsTrue()
            .Because(string.Join("\n", loaded.Errors.Select(e => e.ToString())));
        await Assert.That(loaded.Rulesets.Count).IsGreaterThan(1)
            .Because("a collision needs at least two rulesets to exist between");

        Dictionary<string, List<string>> claimants = new(StringComparer.Ordinal);
        foreach (RulesetDoc doc in loaded.Rulesets)
        {
            if (doc.Show is not { Scoreboard.Count: > 0 } show)
            {
                continue;
            }

            foreach (ScoreboardEntry entry in show.Scoreboard)
            {
                string label = entry.Label ?? entry.Stat;
                foreach (string board in BoardsOf(doc, entry))
                {
                    string key = board + "/" + label;
                    if (!claimants.TryGetValue(key, out List<string>? owners))
                    {
                        owners = [];
                        claimants[key] = owners;
                    }

                    owners.Add(doc.Id);
                }
            }
        }

        string[] collisions = claimants
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key} is claimed by {string.Join(" and ", pair.Value)}")
            .ToArray();
        await Assert.That(collisions).IsEmpty()
            .Because("the later ruleset's column silently overwrites the earlier one:\n" + string.Join("\n", collisions));
    }

    /// <summary>
    ///     The boards an entry lands on, the way <c>ShowLowering</c> decides it: an explicit
    ///     <c>boards:</c> wins; a highlight ref (bare id or <c>id.count</c>) is always the match
    ///     board; a stat or tally target follows its <c>per:</c>.
    /// </summary>
    private static IReadOnlyList<string> BoardsOf(RulesetDoc doc, ScoreboardEntry entry)
    {
        if (entry.Boards is { Count: > 0 } boards)
        {
            return boards;
        }

        string highlightId = entry.Stat.EndsWith(".count", StringComparison.Ordinal)
            ? entry.Stat[..^".count".Length]
            : entry.Stat;
        if (doc.Highlights.Any(h => h.Id == highlightId))
        {
            return ["match"];
        }

        StatDef? stat = doc.Stats.FirstOrDefault(s => s.Id == entry.Stat)
                        ?? doc.Stats.FirstOrDefault(s => s.Thresholds?.Any(t => t.Target == entry.Stat) == true);
        if (stat is null)
        {
            throw new InvalidOperationException(
                $"show: scoreboard entry '{entry.Stat}' in ruleset '{doc.Id}' names neither a stat, a highlight nor a tally target");
        }

        return [stat.Per == PerScope.Round ? "round" : "match"];
    }
}
