#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

public class RoundIndexTableTests
{
    [Test]
    public async Task TheRuleset_LoadsUnderItsQualifiedId_WithTheTable()
    {
        RulesetDoc doc = RoundIndexTableParityTests.LoadRuleset();

        await Assert.That(doc.Id).IsEqualTo(RoundIndexTable.RulesetId);
        await Assert.That(doc.Show!.Tables.Select(t => t.Name)).IsEquivalentTo([RoundIndexTable.TableName], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Samples_AreInTickThenEntityIndexOrder_AndSkipRowsWithoutAPosition()
    {
        List<PositionSample> samples = RoundIndexTable.Samples(
        [
            Row(128, 3, 9, 1f, "B", 1),
            Row(64, 2, 7, 2f, "", true),
            Row(64, 1, 4, 3f, null, false),
            (Dims(64, 5), new Dictionary<string, object?> { ["pos_x"] = null, ["entity_index"] = 1 })
        ]);

        await Assert.That(string.Join(" ", samples.Select(s => $"{s.Tick}/{s.PlayerSlot}/{s.Place ?? "null"}/{s.IsAlive}")))
            .IsEqualTo("64/1/null/False 64/2//True 128/3/B/True");
        await Assert.That(samples[0].Position.X).IsEqualTo(3f);
    }

    private static (IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>) Row(
        int tick, int slot, int entityIndex, float x, string? place, object alive) =>
        (Dims(tick, slot), new Dictionary<string, object?>
        {
            ["pos_x"] = x, ["pos_y"] = 0d, ["pos_z"] = 0d, ["place"] = place, ["is_alive"] = alive, ["entity_index"] = entityIndex
        });

    private static Dictionary<string, object?> Dims(int tick, int slot) => new() { ["tick"] = tick, ["player_slot"] = slot };
}
