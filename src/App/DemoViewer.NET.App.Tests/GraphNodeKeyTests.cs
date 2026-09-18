#region

using DemoViewer.NET.ViewModels;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Analysis graph's node identity.
///     <para>
///         The property under test is that a NAME does not identify a node once the graph carries the
///         per-player copies that actually ran: one real demo produces 3 791 nodes over 432 distinct
///         names, 371 of them repeating once per player slot. Breakpoints are persisted against this
///         key, so a collision is a breakpoint arming on the wrong player, or on ten of them.
///     </para>
/// </summary>
public sealed class GraphNodeKeyTests
{
    [Test]
    public async Task SameNameDifferentSlots_AreDifferentKeys()
    {
        GraphNodeKey a = GraphNodeKey.ForPlayer(0, 3, "Alive");
        GraphNodeKey b = GraphNodeKey.ForPlayer(0, 7, "Alive");

        await Assert.That(a).IsNotEqualTo(b)
            .Because("two players' copies of one template node are two nodes");
        await Assert.That(a.ToString()).IsNotEqualTo(b.ToString())
            .Because("the persisted form has to separate them too, not just the struct");
    }

    [Test]
    public async Task SameNameDifferentTemplates_AreDifferentKeys()
    {
        // A v1 config beside a v2 ruleset materializes the same human under two templates.
        await Assert.That(GraphNodeKey.ForPlayer(0, 3, "Alive"))
            .IsNotEqualTo(GraphNodeKey.ForPlayer(1, 3, "Alive"));
    }

    [Test]
    public async Task GameScopeAndPerPlayer_WithTheSameName_AreDifferentKeys()
    {
        await Assert.That(GraphNodeKey.ForGameScope("Alive"))
            .IsNotEqualTo(GraphNodeKey.ForPlayer(0, 0, "Alive"));
    }

    [Test]
    [Arguments(-1, -1, "Alive")]
    [Arguments(0, 0, "Alive")]
    [Arguments(2, 14, "round_team_alive")]
    [Arguments(0, 5, "weird:name:with:colons")]
    public async Task RoundTripsThroughItsWireForm(int template, int slot, string name)
    {
        GraphNodeKey original = slot < 0
            ? GraphNodeKey.ForGameScope(name)
            : GraphNodeKey.ForPlayer(template, slot, name);

        await Assert.That(GraphNodeKey.TryParse(original.ToString(), out GraphNodeKey parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(original)
            .Because("a persisted breakpoint has to resolve to the node it was set on");
        await Assert.That(parsed.Name).IsEqualTo(name)
            .Because("a node name may contain colons, so parsing splits a bounded number of times");
    }

    [Test]
    [Arguments("Alive")]
    [Arguments("")]
    [Arguments("p:3:Alive")]
    [Arguments("pX:3:Alive")]
    [Arguments("p0:Alive")]
    [Arguments("p0:3:")]
    [Arguments("g:")]
    public async Task RejectsAnythingThatIsNotAKey(string text)
    {
        await Assert.That(GraphNodeKey.TryParse(text, out _)).IsFalse()
            .Because("a bare name is what a pre-v2 breakpoint file holds, and it identifies nothing now");
    }

    [Test]
    public async Task DefaultValue_IsGameScope_NotSlotZero()
    {
        // A struct's default zeroes its fields, so a -1 sentinel on PlayerSlot would have made
        // default(GraphNodeKey) claim to be template 0 / slot 0 - a namespace real nodes occupy.
        await Assert.That(default(GraphNodeKey).IsPerPlayer).IsFalse();
        await Assert.That(GraphNodeKey.ForPlayer(0, 0, "Alive").IsPerPlayer).IsTrue();
        await Assert.That(GraphNodeKey.ForGameScope("Alive").IsPerPlayer).IsFalse();
    }

    [Test]
    public async Task TryParse_AssignsAGameScopeDefault_OnFailure()
    {
        bool ok = GraphNodeKey.TryParse("not a key", out GraphNodeKey parsed);

        await Assert.That(ok).IsFalse();
        await Assert.That(parsed.IsPerPlayer).IsFalse()
            .Because("a caller ignoring the bool must not get a key claiming to be slot 0");
    }

    [Test]
    public async Task NodeViewModel_ExposesTheKeyAsIGraphNodeKey()
    {
        GraphNodeViewModel vm = new("Alive")
        {
            NodeKey = GraphNodeKey.ForPlayer(0, 3, "Alive")
        };

        await Assert.That(((IGraphNode)vm).Key).IsEqualTo(GraphNodeKey.ForPlayer(0, 3, "Alive").ToString());
        await Assert.That(vm.Name).IsEqualTo("Alive")
            .Because("the name is still what the node box displays");
    }

    [Test]
    public async Task EveryPlayersKeys_AreDistinct_AcrossAWholeRoster()
    {
        // The property that makes a breakpoint survive a player switch: ten slots' copies of the same
        // template node are ten keys, and a lookup table over the whole roster loses none of them.
        string[] names = ["Alive", "Survived", "Traded", "round_team_alive"];
        int[] slots = [1, 5, 6, 7, 9, 10, 11, 12, 13, 14];

        Dictionary<string, (int Slot, string Name)> byKey = new(StringComparer.Ordinal);
        foreach (int slot in slots)
        {
            foreach (string name in names)
            {
                byKey[GraphNodeKey.ForPlayer(0, slot, name).ToString()] = (slot, name);
            }
        }

        await Assert.That(byKey.Count).IsEqualTo(slots.Length * names.Length)
            .Because("no key may collide, or one player's breakpoint resolves to another's column");

        // Slot 7's key still resolves to slot 7, whichever player the graph happens to be drawing.
        (int Slot, string Name) found = byKey[GraphNodeKey.ForPlayer(0, 7, "Alive").ToString()];
        await Assert.That(found.Slot).IsEqualTo(7);
        await Assert.That(found.Name).IsEqualTo("Alive");
    }

    [Test]
    public async Task NodeViewModel_DefaultsToGameScope()
    {
        // The Workbench authoring graph and the pre-evaluation skeleton both rely on this default.
        GraphNodeViewModel vm = new("Alive");

        await Assert.That(vm.NodeKey).IsEqualTo(GraphNodeKey.ForGameScope("Alive"));
        await Assert.That(((IGraphNode)vm).Key).IsEqualTo("g:Alive");
    }
}
