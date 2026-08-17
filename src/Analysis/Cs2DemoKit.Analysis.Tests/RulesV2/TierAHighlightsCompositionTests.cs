#region

using Cs2DemoKit.Analysis.Abstractions;
using Cs2DemoKit.Analysis.Catalog;
using Cs2DemoKit.Analysis.RulesetsV2.Compile;
using Cs2DemoKit.Analysis.RulesetsV2.Model;
using Cs2DemoKit.Analysis.RulesetsV2.Resolve;
using Cs2DemoKit.Analysis.Yaml;

#endregion

namespace Cs2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The shipped Tier A highlight catalog. Demo-free: it composes EVERY
///     top-level <c>rules/*.rules.yaml</c> (the non-recursive production scan set) together and
///     asserts the new highlights resolve with the authored <c>score:</c>/<c>kind:</c> threaded
///     end-to-end — a representative sample across all four tracks (skill / funny / lowlight /
///     hidden). Composing the WHOLE set at once also guards the cross-ruleset wiring
///     (<c>player_stats use: [kast]</c>) and the earlier demotions under the full catalog.
/// </summary>
[Category("Unit")]
public class TierAHighlightsCompositionTests
{
    private const string ProfileId = "Cs2GotvProfile";

    [Test]
    public async Task AllShippedRules_ComposeCleanly_AndTierAHighlightsCarryScoreAndKind()
    {
        IReadOnlyList<RulesetDoc> docs = LoadAllShippedDocs();
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed = RulesetComposition.Compose(docs, adapter, 64.0, ProfileId);

        await Assert.That(composed.Success).IsTrue()
            .Because("the whole shipped rule set must compose: " + string.Join("; ", composed.Diagnostics));

        Dictionary<string, CheckedHighlight> byId = composed.Rulesets
            .SelectMany(rs => rs.Highlights.Select(h => (Key: $"{rs.Id.Id}.{h.HighlightId}", H: h)))
            .ToDictionary(x => x.Key, x => x.H, StringComparer.Ordinal);

        // ── Tier A: a representative highlight from each new ruleset, each track ──────────────
        await AssertHighlight(byId, "highlights_multikill.ace", 100, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_multikill.triple_kill", 55, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_aim.jumping_no_scope", 95, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_aim.wallbang_kill", 75, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_objective.ninja_defuse", 88, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_objective.knife_kill", 90, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_funny.jumping_knife_kill", 95, HighlightKind.Funny);
        await AssertHighlight(byId, "highlights_funny.blind_kill_lucky", 60, HighlightKind.Funny);
        await AssertHighlight(byId, "highlights_lowlights.teamkill", 40, HighlightKind.Lowlight);
        await AssertHighlight(byId, "highlights_lowlights.knifed", 65, HighlightKind.Lowlight);

        // ── Windowed multi-kills (burst pulses) ──────────────────────────────────────────────
        await AssertHighlight(byId, "highlights_rapid.rapid_triple", 82, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_rapid.rapid_quad", 92, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_rapid.instant_ace", 100, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_rapid.collateral", 95, HighlightKind.Highlight);

        // ── Clutch (reads the new round.clutch.size context) + retake ─────────────────────────
        await AssertHighlight(byId, "highlights_clutch.retake_multi", 80, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_clutch.clutch_1v3", 90, HighlightKind.Highlight);
        await AssertHighlight(byId, "highlights_clutch.clutch_1v5", 100, HighlightKind.Highlight);

        // ── Earlier demotions still hold under the full compose ──────────────────────────────
        await AssertHighlight(byId, "kast.kast", 50, HighlightKind.Hidden);                 // default score
        await AssertHighlight(byId, "player_stats.opening_death", 40, HighlightKind.Lowlight);
        await AssertHighlight(byId, "player_stats.traded_death", 50, HighlightKind.Hidden);  // default score
        await AssertHighlight(byId, "post_plant_double.post_plant_double", 78, HighlightKind.Highlight);
    }

    private static async Task AssertHighlight(
        Dictionary<string, CheckedHighlight> byId, string key, int score, HighlightKind kind)
    {
        await Assert.That(byId.ContainsKey(key)).IsTrue().Because($"highlight '{key}' must be present");
        CheckedHighlight h = byId[key];
        await Assert.That(h.Score).IsEqualTo(score).Because($"'{key}' score");
        await Assert.That(h.Kind).IsEqualTo(kind).Because($"'{key}' kind");
    }

    /// <summary>Every top-level <c>rules/*.rules.yaml</c> (non-recursive — the production scan set).</summary>
    private static IReadOnlyList<RulesetDoc> LoadAllShippedDocs()
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "rules");
        return
        [
            .. Directory.EnumerateFiles(rulesDir, "*.rules.yaml", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => RulesetDocumentLoader.Load(File.ReadAllText(p), Path.GetFileName(p)).Doc
                             ?? throw new InvalidOperationException($"shipped ruleset failed to load: {p}"))
        ];
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
