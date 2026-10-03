#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The history log's pure half (strat-model.md §3.8): the op applier over RFC 6901 pointers, the inverse,
///     and the commit rule (decision 2): same-path merging inside one commit and the 30 s idle rule.
/// </summary>
public class StratHistoryTests
{
    private static readonly string[] UnmergedPaths = ["/steps/1/atSeconds", "/steps/0/atSeconds", "/steps/1/atSeconds"];
    private static readonly string[] InverseKinds = [PatchOp.Add, PatchOp.Remove, PatchOp.Replace];
    private static readonly string[] EditedTags = ["first", "a/b", "appended"];

    private static PatchOp Replace(string path, double from, double to) =>
        PatchOp.ReplaceOp(path, JsonValue.Create(from), JsonValue.Create(to));

    [Test]
    public async Task ConsecutiveReplacesOnOnePath_MergeIntoOne_WithTheFirstFromAndTheLastValue()
    {
        StratCommitBuffer buffer = new();
        buffer.Record(Replace("/steps/1/atSeconds", 82, 80), Created);
        buffer.Record(Replace("/steps/1/atSeconds", 80, 78), Created.AddSeconds(1));
        buffer.Record(Replace("/steps/1/atSeconds", 78, 76), Created.AddSeconds(2));

        PatchOp op = buffer.Drain().Single();
        using (Assert.Multiple())
        {
            await Assert.That(op.From!.GetValue<double>()).IsEqualTo(82);
            await Assert.That(op.Value!.GetValue<double>()).IsEqualTo(76);
            await Assert.That(buffer.HasPending).IsFalse();
        }
    }

    [Test]
    public async Task OnlyConsecutiveOpsMerge_AndADifferentPathStartsANewOp()
    {
        StratCommitBuffer buffer = new();
        buffer.Record(Replace("/steps/1/atSeconds", 82, 80), Created);
        buffer.Record(Replace("/steps/0/atSeconds", 90, 88), Created);
        buffer.Record(Replace("/steps/1/atSeconds", 80, 78), Created);

        await Assert.That(buffer.Drain().Select(o => o.Path))
            .IsEquivalentTo(UnmergedPaths);
    }

    [Test]
    public async Task ADragLetGoAtItsOrigin_AndAnAddThenRemove_LeaveNothing()
    {
        StratCommitBuffer buffer = new();
        buffer.Record(Replace("/steps/1/atSeconds", 82, 80), Created);
        buffer.Record(Replace("/steps/1/atSeconds", 80, 82), Created);
        buffer.Record(PatchOp.AddOp("/tags/0", JsonValue.Create("x")), Created);
        buffer.Record(PatchOp.RemoveOp("/tags/0", JsonValue.Create("x")), Created);

        await Assert.That(buffer.Drain().Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnAddThenAReplace_IsOneAdd_AndARemoveThenAnAdd_IsOneReplace()
    {
        StratCommitBuffer buffer = new();
        buffer.Record(PatchOp.AddOp("/notes", JsonValue.Create("a")), Created);
        buffer.Record(PatchOp.ReplaceOp("/notes", JsonValue.Create("a"), JsonValue.Create("ab")), Created);
        buffer.Record(PatchOp.RemoveOp("/tempo", JsonValue.Create("slow")), Created);
        buffer.Record(PatchOp.AddOp("/tempo", JsonValue.Create("fast")), Created);

        IReadOnlyList<PatchOp> ops = buffer.Drain();
        using (Assert.Multiple())
        {
            await Assert.That(ops.Count).IsEqualTo(2);
            await Assert.That(ops[0].Op).IsEqualTo(PatchOp.Add);
            await Assert.That(ops[0].Value!.GetValue<string>()).IsEqualTo("ab");
            await Assert.That(ops[1].Op).IsEqualTo(PatchOp.Replace);
            await Assert.That(ops[1].From!.GetValue<string>()).IsEqualTo("slow");
            await Assert.That(ops[1].Value!.GetValue<string>()).IsEqualTo("fast");
        }
    }

    [Test]
    public async Task ACommitFallsDue_ThirtySecondsAfterTheLastEdit_NotTheFirst()
    {
        StratCommitBuffer buffer = new();
        await Assert.That(buffer.IsIdleCommitDue(Created.AddHours(1))).IsFalse().Because("nothing is pending");

        buffer.Record(Replace("/steps/1/atSeconds", 82, 80), Created);
        buffer.Record(Replace("/steps/0/atSeconds", 90, 88), Created.AddSeconds(20));

        using (Assert.Multiple())
        {
            await Assert.That(buffer.IsIdleCommitDue(Created.AddSeconds(35))).IsFalse()
                .Because("the idle clock restarts at every edit");
            await Assert.That(buffer.IsIdleCommitDue(Created.AddSeconds(49.9))).IsFalse();
            await Assert.That(buffer.IsIdleCommitDue(Created.AddSeconds(50))).IsTrue();
        }
    }

    [Test]
    public async Task Apply_WalksPointersIntoObjectsAndArrays_WithEscapes_AndLeavesTheInputAlone()
    {
        StratDocument document = Minimal();
        document.Tags = ["a/b"];

        StratDocument edited = StratHistory.Apply(document,
        [
            PatchOp.ReplaceOp("/steps/0/to/place", JsonValue.Create("BombsiteA"), JsonValue.Create("Stairs")),
            PatchOp.AddOp("/tags/-", JsonValue.Create("appended")),
            PatchOp.AddOp("/tags/0", JsonValue.Create("first")),
            PatchOp.RemoveOp("/steps/1", null),
            PatchOp.AddOp("/extra~1field", JsonValue.Create(1))
        ]);

        using (Assert.Multiple())
        {
            await Assert.That(edited.Steps.Count).IsEqualTo(1);
            await Assert.That(edited.Steps[0].To!.Place).IsEqualTo("Stairs");
            await Assert.That(edited.Tags).IsEquivalentTo(EditedTags);
            await Assert.That(edited.Extra!.ContainsKey("extra/field")).IsTrue().Because("~1 unescapes to /");
            await Assert.That(document.Steps.Count).IsEqualTo(2);
            await Assert.That(StratHistory.Escape("a/b~c")).IsEqualTo("a~1b~0c");
        }
    }

    [Test]
    public async Task Apply_RefusesAPathThatDoesNotResolve()
    {
        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => StratHistory.Apply(Minimal(), [Replace("/steps/9/atSeconds", 1, 2)]));
        await Assert.That(thrown.Message).Contains("/steps/9/atSeconds");
    }

    [Test]
    public async Task Inverse_SwapsFromAndValue_AndReversesTheOps()
    {
        HistoryEntry entry = new()
        {
            Revision = 3,
            Ops =
            [
                Replace("/steps/0/atSeconds", 90, 88),
                PatchOp.AddOp("/tags/0", JsonValue.Create("x")),
                PatchOp.RemoveOp("/notes", JsonValue.Create("n"))
            ]
        };

        HistoryEntry inverse = entry.Inverse();
        using (Assert.Multiple())
        {
            await Assert.That(inverse.Ops.Select(o => o.Op)).IsEquivalentTo(InverseKinds);
            await Assert.That(inverse.Ops[0].Value!.GetValue<string>()).IsEqualTo("n");
            await Assert.That(inverse.Ops[1].From!.GetValue<string>()).IsEqualTo("x");
            await Assert.That(inverse.Ops[2].From!.GetValue<double>()).IsEqualTo(88);
            await Assert.That(inverse.Ops[2].Value!.GetValue<double>()).IsEqualTo(90);
        }
    }

    [Test]
    public async Task Materialize_ReturnsNull_ForARevisionTheLogDoesNotReach()
    {
        StratStore store = new(null);
        StratDocument document = store.Create(Team, "de_mirage", "T", "execute", "A exec");

        using (Assert.Multiple())
        {
            await Assert.That(store.Materialize(document.Id, 1)!.Name).IsEqualTo("A exec");
            await Assert.That(store.Materialize(document.Id, 2)).IsNull();
            await Assert.That(store.Materialize(document.Id, 0)).IsNull();
        }
    }

    [Test]
    public async Task Diff_GivesOpsThatTurnOneStateIntoTheOther_WithFromFilled()
    {
        StratDocument before = Minimal();
        StratDocument after = before.Clone();
        after.Name = "A split";
        after.Economy = "full";
        after.Steps[1].AtSeconds = 76;
        after.Steps.Add(Step(3, 60, "all", "move"));

        List<PatchOp> ops = StratHistory.Diff(StratHistory.ToNode(before), StratHistory.ToNode(after));
        StratDocument rebuilt = StratHistory.Apply(before, ops);
        StratDocument undone = StratHistory.Apply(rebuilt, ops.AsEnumerable().Reverse().Select(o => o.Inverse()));

        using (Assert.Multiple())
        {
            await Assert.That(StratStore.Serialize(rebuilt)).IsEqualTo(StratStore.Serialize(after));
            await Assert.That(StratStore.Serialize(undone)).IsEqualTo(StratStore.Serialize(before));
            await Assert.That(ops.Single(o => o.Path == "/name").From!.GetValue<string>()).IsEqualTo("A exec");
            await Assert.That(ops.Single(o => o.Path == "/economy").Op).IsEqualTo(PatchOp.Add);
            await Assert.That(ops.Single(o => o.Path == "/steps").Op).IsEqualTo(PatchOp.Replace)
                .Because("an array whose length changed is replaced whole");
            await Assert.That(StratHistory.Diff(StratHistory.ToNode(before), StratHistory.ToNode(before.Clone()))).IsEmpty();
        }
    }

    [Test]
    public async Task ValueAt_ReadsAPointer_AndNullForNothing()
    {
        JsonNode root = StratHistory.ToNode(Minimal());

        using (Assert.Multiple())
        {
            await Assert.That(StratHistory.ValueAt(root, "/steps/1/atSeconds")!.GetValue<double>()).IsEqualTo(82);
            await Assert.That(StratHistory.ValueAt(root, "/steps/9/atSeconds")).IsNull();
            await Assert.That(StratHistory.ValueAt(root, "/nothing")).IsNull();
            await Assert.That(StratHistory.ValueAt(root, "")).IsSameReferenceAs(root);
        }
    }
}
