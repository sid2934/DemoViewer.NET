#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The aggregation rules (<c>docs/player-overview.md</c>) cover the whole catalogue, and the
///     <c>player_match</c> facet stores exactly what they read: a column with no rule cannot be pooled, and a
///     facet column no rule reads is a re-walk of the library spent on nothing.
/// </summary>
public class ColumnRuleSweepTests
{
    private static readonly string Shipped = RuleSetLocator.ResolveShippedRulesDirectory();

    private static IReadOnlyList<string> ShippedFacetColumns()
    {
        RulesetDoc doc = new MergedRulesBuild(() => YamlConfigLoader.LoadWithOverlay(Shipped, null)).EnabledDoc(PlayerMatchFacet.RulesetId)
                         ?? throw new InvalidOperationException("rules/ ships no player_match");
        return [.. doc.Show!.Tables.Single(t => t.Name == PlayerMatchFacet.Table).Columns.Select(c => c.Label ?? c.Stat)];
    }

    private static Dictionary<string, double> Row(params (string Column, double Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);

    [Test]
    public async Task EveryCatalogueColumn_HasARule()
    {
        List<string> missing = [.. ColumnCatalogue.All.Select(m => m.Key).Where(k => ColumnRules.For(k) is null)];

        using (Assert.Multiple())
        {
            await Assert.That(missing).IsEmpty().Because($"no aggregation rule for {string.Join(", ", missing)}");
            await Assert.That(ColumnRules.All.Keys.Where(k => ColumnCatalogue.All.All(m => m.Key != k))).IsEmpty()
                .Because("a rule for a column the catalogue does not know is a typo");
        }
    }

    [Test]
    public async Task EverySightColumn_IsNotAggregable_AndOutOfTheFacet()
    {
        List<ColumnMeta> sight = [.. ColumnCatalogue.All.Where(m => m.RequiresVisibility)];

        using (Assert.Multiple())
        {
            await Assert.That(sight.Count).IsEqualTo(17);
            await Assert.That(sight.Where(m => ColumnRules.For(m.Key) is not ColumnRule.NotAggregable)).IsEmpty();
            await Assert.That(ShippedFacetColumns().Where(ColumnCatalogue.RequiresVisibility)).IsEmpty()
                .Because("a forward run has no line-of-sight geometry, so a sight column would store a permanent 0");
        }
    }

    [Test]
    public async Task TheFacetColumns_AreExactlyWhatTheRulesRead()
    {
        IReadOnlyList<string> facet = ShippedFacetColumns();

        using (Assert.Multiple())
        {
            await Assert.That(facet.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(facet.Count);
            await Assert.That(facet.Order(StringComparer.Ordinal)).IsEquivalentTo(ColumnRules.FacetColumns.Order(StringComparer.Ordinal));
            await Assert.That(facet).Contains(ColumnRules.Rounds);
        }
    }

    [Test]
    public async Task ARate_PoolsFromItsSummedComponents_NeverFromTheMeanOfRates()
    {
        // ADR 100 over 10 rounds and 50 over 30: the mean of rates is 75, the pool 2500 / 40.
        Dictionary<string, double> shortGame = Row(("EnemyDmg", 1000), ("rounds", 10), ("HLTV", 1.5), ("TotalFK", 3), ("TotalFD", 1));
        Dictionary<string, double> longGame = Row(("EnemyDmg", 1500), ("rounds", 30), ("HLTV", 0.9), ("TotalFK", 0), ("TotalFD", 4));

        using (Assert.Multiple())
        {
            await Assert.That(ColumnRules.For("ADR")!.Pool([shortGame, longGame])).IsEqualTo(2500.0 / 40);
            await Assert.That(ColumnRules.For("ADR")!.Pool([shortGame])).IsEqualTo(100.0).Because("one demo pools to its own value");
            await Assert.That(Math.Abs(ColumnRules.For("HLTV")!.Pool([shortGame, longGame])!.Value - (((1.5 * 10) + (0.9 * 30)) / 40))).IsLessThan(1e-12)
                .Because("HLTV pools as the rounds-weighted mean");
            await Assert.That(ColumnRules.For("FK±")!.Pool([shortGame, longGame])).IsEqualTo(-2.0);
            await Assert.That(ColumnRules.For("Duel%")!.Pool([shortGame, longGame])).IsEqualTo(3.0 / 8 * 100);
            await Assert.That(ColumnRules.For("Duel%")!.Pool([Row(("TotalFK", 0), ("TotalFD", 0))])).IsEqualTo(0.0)
                .Because("the engine reads a rate over nothing as 0");
            await Assert.That(ColumnRules.For("TotalK")!.Pool([shortGame, longGame])).IsEqualTo(0.0).Because("a missing cell reads as zero");
            await Assert.That(ColumnRules.For("Kills")!.Pool([shortGame])).IsNull();
        }
    }
}
