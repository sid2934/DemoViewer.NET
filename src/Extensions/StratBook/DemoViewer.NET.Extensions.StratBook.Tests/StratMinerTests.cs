#region

using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Strat Mining's comparison and clustering over synthetic rounds: players pair by position not slot,
///     unknown utility never scores as none, grenades match on landing not lineup, complete linkage keeps a
///     far round out, sites never mix, copies drop, and the output and key are stable.
/// </summary>
public class StratMinerTests
{
    private static readonly (float X, float Y)[] _stack = [(0, 0), (100, 0), (200, 0), (0, 800), (100, 800)];

    private static RoundSignature Round(string demo, int round, (float X, float Y)[] players, IReadOnlyList<MinedThrow>? throws,
        string? site = "A", PatternKind kind = PatternKind.Execute, double takeSeconds = 30, int slotOffset = 0) =>
        new()
        {
            DemoPath = demo,
            Round = round,
            Map = "de_mirage",
            Side = 2,
            Kind = kind,
            Site = site,
            TickRate = 64,
            FreezeEndTick = 1000,
            AnchorTick = 1000 + (int)(takeSeconds * 64),
            Won = round % 2 == 0,
            Anchors =
            [
                [.. players.Select((p, i) => new MinedPawn((i + slotOffset) % 5, p.X, p.Y, 0, i < 3 ? "TRamp" : "Palace"))],
                [.. players.Select((p, i) => new MinedPawn((i + slotOffset) % 5, p.X + 50, p.Y, 0, null))]
            ],
            Throws = throws
        };

    private static MinedThrow Smoke(float x, float y, double seconds, Guid? lineup = null) =>
        new(GrenadeKind.Smoke, seconds, new WorldPoint(x, y, 0), "Stairs", new WorldPoint(0, 0, 0), 0, null, lineup);

    private static (float X, float Y)[] Shift((float X, float Y)[] players, float by) => [.. players.Select(p => (p.X + by, p.Y))];

    [Test]
    public async Task Players_PairByPosition_NotBySlot()
    {
        RoundSignature a = Round("a.dem", 1, _stack, []);
        RoundSignature b = Round("b.dem", 1, [.. _stack.Reverse()], [], slotOffset: 3);

        await Assert.That(StratMiner.PositionDistance(a.Anchors, b.Anchors)).IsEqualTo(0).Within(1e-9);
    }

    [Test]
    public async Task UnknownUtility_CostsThePenalty_NeverTheNoneScore()
    {
        RoundSignature threw = Round("a.dem", 1, _stack, [Smoke(900, 900, -5)]);
        RoundSignature none = Round("b.dem", 1, _stack, []);
        RoundSignature unknown = Round("c.dem", 1, _stack, null);

        using (Assert.Multiple())
        {
            await Assert.That(StratMiner.Distance(threw, none)).IsEqualTo(StratMiner.UtilityWeight).Within(1e-9)
                .Because("rows that show no grenade are a real difference");
            await Assert.That(StratMiner.Distance(threw, unknown)).IsEqualTo(StratMiner.UnknownUtilityPenalty).Within(1e-9)
                .Because("a demo without grenade rows is unknown, not empty");
        }
    }

    [Test]
    public async Task Grenades_MatchOnTheLanding_WhateverTheLineup()
    {
        List<MinedThrow> a = [Smoke(900, 900, -5, Guid.NewGuid()), Smoke(-900, 0, -4)];
        List<MinedThrow> b = [Smoke(950, 880, -3, Guid.NewGuid()), Smoke(3000, 3000, -4)];

        using (Assert.Multiple())
        {
            await Assert.That(StratMiner.UtilitySimilarity(a, b)).IsEqualTo(0.5).Within(1e-9)
                .Because("the stairs smoke from another spot is the same smoke; the far one is not");
            await Assert.That(StratMiner.UtilitySimilarity([], [])).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Mine_GroupsTheNearRounds_KeepsAFarOneOut_AndNeverMixesSites()
    {
        List<MinedThrow> utility = [Smoke(900, 900, -5), Smoke(-900, 0, -4)];
        List<RoundSignature> rounds =
        [
            Round("a.dem", 2, _stack, utility),
            Round("b.dem", 4, Shift(_stack, 60), utility),
            Round("c.dem", 6, Shift(_stack, -60), [Smoke(900, 900, -5)]),
            Round("d.dem", 8, Shift(_stack, 2000), utility),
            Round("e.dem", 1, _stack, utility, site: "B")
        ];

        IReadOnlyList<MinedPattern> patterns = StratMiner.Mine(rounds);

        MinedPattern pattern = patterns.Single();
        using (Assert.Multiple())
        {
            await Assert.That(pattern.Members.Select(m => m.DemoPath)).IsEquivalentTo(["a.dem", "b.dem", "c.dem"]);
            await Assert.That(pattern.Members[0].DemoPath).IsEqualTo("a.dem").Because("the medoid comes first");
            await Assert.That(pattern.Site).IsEqualTo("A");
            await Assert.That(pattern.Wins).IsEqualTo(3);
            await Assert.That(pattern.UtilityCompared).IsTrue();
            await Assert.That(pattern.CommonThrows.Select(c => c.Rounds)).IsEquivalentTo([3, 2])
                .Because("both smokes are thrown in at least half the rounds");
        }
    }

    [Test]
    public async Task ACopiedDemo_IsDropped_SoItNeverPairsWithItsOriginal()
    {
        List<RoundSignature> rounds = [Round("full.dem", 3, _stack, []), Round("trimmed.dem", 3, _stack, [])];

        await Assert.That(StratMiner.DropCopies(rounds).Select(r => r.DemoPath)).IsEquivalentTo(["full.dem"]);
        await Assert.That(StratMiner.Mine(rounds)).IsEmpty();
    }

    [Test]
    public async Task TheOutput_AndTheKey_DoNotDependOnInputOrder()
    {
        List<MinedThrow> utility = [Smoke(900, 900, -5)];
        List<RoundSignature> rounds =
        [
            Round("a.dem", 2, _stack, utility),
            Round("b.dem", 4, Shift(_stack, 40), utility),
            Round("c.dem", 6, Shift(_stack, 80), utility)
        ];

        MinedPattern forward = StratMiner.Mine(rounds).Single();
        MinedPattern backward = StratMiner.Mine(Enumerable.Reverse(rounds)).Single();
        MinedPattern fewer = StratMiner.Mine(rounds.Take(2)).Single();

        using (Assert.Multiple())
        {
            await Assert.That(backward.Key).IsEqualTo(forward.Key);
            await Assert.That(backward.Members.Select(m => m.DemoPath)).IsEquivalentTo(forward.Members.Select(m => m.DemoPath));
            await Assert.That(fewer.Key).IsEqualTo(forward.Key).Because("the key follows the shape, not the member set");
        }
    }
}
