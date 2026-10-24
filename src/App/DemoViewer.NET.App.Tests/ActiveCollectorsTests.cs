#region

using DemoViewer.NET.Services.DemoProcessing.Collectors;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

public class ActiveCollectorsTests
{
    private const string Demo = "/d/match.dem";

    private static CollectorSet Set(params string[] ids) =>
        CollectorSet.Of([.. ids.Select(id => CollectorKit.Register(CollectorKit.Counting(id)))]);

    private static string[] Ids(CollectorSet set) => [.. set.All.Select(c => c.Id)];

    [Test]
    public async Task TwoVisitsOfOnePath_AnswerTheirUnion_AndEachRetractsOnlyItsOwn()
    {
        ActiveCollectors active = new();
        IDisposable background = active.Begin(Demo, Set("c.a"));
        IDisposable open = active.Begin(Demo.ToUpperInvariant(), Set("c.b"));

        await Assert.That(Ids(active.For(Demo))).IsEquivalentTo(["c.a", "c.b"], CollectionOrdering.Matching);

        background.Dispose();
        await Assert.That(Ids(active.For(Demo))).IsEquivalentTo(["c.b"], CollectionOrdering.Matching).Because("the open's passes are still running");

        open.Dispose();
        await Assert.That(active.For(Demo).IsEmpty).IsTrue();
    }

    [Test]
    public async Task TwoVisitsWithTheSameSet_KeepItUntilBothEnd()
    {
        ActiveCollectors active = new();
        CollectorSet set = Set("c.a");
        IDisposable first = active.Begin(Demo, set);
        IDisposable second = active.Begin(Demo, set);

        first.Dispose();
        first.Dispose();
        await Assert.That(Ids(active.For(Demo))).IsEquivalentTo(["c.a"], CollectionOrdering.Matching).Because("a second end of one visit retracts nothing more");

        second.Dispose();
        await Assert.That(active.For(Demo).IsEmpty).IsTrue();
    }

    [Test]
    public async Task AnotherPath_IsNotAffected()
    {
        ActiveCollectors active = new();
        using IDisposable other = active.Begin("/d/other.dem", Set("c.a"));
        IDisposable mine = active.Begin(Demo, Set("c.b"));
        mine.Dispose();

        using (Assert.Multiple())
        {
            await Assert.That(active.For(Demo).IsEmpty).IsTrue();
            await Assert.That(Ids(active.For("/d/other.dem"))).IsEquivalentTo(["c.a"], CollectionOrdering.Matching);
        }
    }
}
