#region

using DemoViewer.NET.Services.Strats;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     One slot's sheet (strat-model.md §3.14, §9 step 6): lines for <c>actor == slot</c> and <c>all</c>,
///     context lines by the dependency rule, branches attached to the right lines, the mini-map polyline,
///     and an unassigned slot printing its letter.
/// </summary>
public class RoleSheetTests
{
    private static CalloutResolver Callouts() => new(
        ["TRamp", "BombsiteA", "Connector", "Stairs", "Jungle"],
        new CalloutTable
        {
            Map = "de_mirage",
            Aliases =
            [
                new CalloutAlias { Alias = "A ramp", Place = "TRamp", Primary = true },
                new CalloutAlias { Alias = "A site", Place = "BombsiteA", Primary = true }
            ]
        });

    private static string Line(RoleSheetLine line) => (line.IsContext ? "context: " : "own: ") + line.Text;

    [Test]
    public async Task SlotA_GetsItsOwnPeekAndTheAllMove_PlusTheTwoUtilityStepsAsContext()
    {
        RoleSheet sheet = RoleSheet.Derive(SchemaSample(), "A", Callouts());

        await Assert.That(sheet.Lines.Select(Line)).IsEquivalentTo(
        [
            "context: B throws smoke A ramp → A site (Stairs)",
            "context: C throws molotov A ramp → A site (Jungle)",
            "own: A peeks A ramp → Connector",
            "own: All move A ramp → A site"
        ]);
    }

    [Test]
    public async Task SlotB_GetsItsOwnThrowAndTheAllMove_PlusTheLaterMolotovAsContext_NotTheEarlierOne()
    {
        // Nothing precedes B's own throw (the earliest step), so it gets no context of its own; the
        // molotov (76 s) lands at the same place the all-move (60 s) ends at and happens first, so it is
        // context for that line. The dependency rule reads "at the same or earlier real time" as a larger
        // atSeconds, since the round clock counts down.
        RoleSheet sheet = RoleSheet.Derive(SchemaSample(), "B", Callouts());

        await Assert.That(sheet.Lines.Select(Line)).IsEquivalentTo(
        [
            "own: B throws smoke A ramp → A site (Stairs) (throw on the 1:30 call, not before)",
            "context: C throws molotov A ramp → A site (Jungle)",
            "own: All move A ramp → A site"
        ]);
    }

    [Test]
    public async Task SlotCsOwnLine_CarriesItsNote()
    {
        RoleSheet sheet = RoleSheet.Derive(SchemaSample(), "C", Callouts());
        RoleSheetLine? molotov = sheet.Lines.FirstOrDefault(l => !l.IsContext && l.Text.Contains("molotov", StringComparison.Ordinal));

        await Assert.That(molotov).IsNotNull();
        await Assert.That(molotov!.Text).IsEqualTo("C throws molotov A ramp → A site (Jungle)");
    }

    [Test]
    public async Task TheBranchAfterMollysStep_AttachesOnlyToSlotC()
    {
        RoleSheet c = RoleSheet.Derive(SchemaSample(), "C");
        RoleSheet a = RoleSheet.Derive(SchemaSample(), "A");

        await Assert.That(c.Branches.Select(b => b.Text))
            .IsEquivalentTo(["if contact at Connector before 1:05 → A exec, double smoke, step 4"]);
        await Assert.That(a.Branches).IsEmpty();
    }

    [Test]
    public async Task DAndE_HaveOnlyTheAllMoveAsTheirOwnLine_AndPrintTheBareLetterWithNoRoster()
    {
        // D and E own only the "all" step; both utility throws land where it ends (BombsiteA), and both
        // happened earlier in real time (a larger atSeconds), so both come along as context the same way
        // they do for slot A's own "all" line.
        RoleSheet d = RoleSheet.Derive(SchemaSample(), "D", Callouts());

        await Assert.That(d.Header.Slot).IsEqualTo("D");
        await Assert.That(d.Header.SlotName).IsNull();
        await Assert.That(d.Header.Role).IsEqualTo("awp");
        await Assert.That(d.Lines.Select(Line)).IsEquivalentTo(
        [
            "context: B throws smoke A ramp → A site (Stairs)",
            "context: C throws molotov A ramp → A site (Jungle)",
            "own: All move A ramp → A site"
        ]);
    }

    [Test]
    public async Task ARoster_ResolvesTheSlotsName()
    {
        RoleSheet sheet = RoleSheet.Derive(SchemaSample(), "B", roster: new Dictionary<string, string?> { ["B"] = "Ferris" });

        await Assert.That(sheet.Header.SlotName).IsEqualTo("Ferris");
    }

    [Test]
    public async Task ThePositionsOfEachStep_BecomeTheSlotsPolyline_InStepOrder()
    {
        RoleSheet a = RoleSheet.Derive(SchemaSample(), "A");
        RoleSheet b = RoleSheet.Derive(SchemaSample(), "B");
        RoleSheet e = RoleSheet.Derive(SchemaSample(), "E");

        static string Point(RoleSheetPoint p) => $"{p.AtSeconds}:{p.X},{p.Y}";

        await Assert.That(a.Positions.Select(Point)).IsEquivalentTo(["90:400,-1800", "65:-600,-1400"]);
        await Assert.That(b.Positions.Select(Point)).IsEquivalentTo(["90:420,-1750"]);
        await Assert.That(e.Positions).IsEmpty();
    }

    [Test]
    public async Task WithNoCallouts_PlacesPrintTheCanonicalNameSplitIntoWords()
    {
        RoleSheet sheet = RoleSheet.Derive(SchemaSample(), "C");
        RoleSheetLine molotov = sheet.Lines.Single(l => !l.IsContext && l.Text.Contains("molotov", StringComparison.Ordinal));

        await Assert.That(molotov.Text).IsEqualTo("C throws molotov T Ramp → Bombsite A (Jungle)");
    }

    [Test]
    public async Task TheHeader_CarriesTheStratsMetadata()
    {
        RoleSheetHeader header = RoleSheet.Derive(SchemaSample(), "A").Header;

        using (Assert.Multiple())
        {
            await Assert.That(header.StratName).IsEqualTo("A exec, double smoke");
            await Assert.That(header.Map).IsEqualTo("de_mirage");
            await Assert.That(header.Side).IsEqualTo("T");
            await Assert.That(header.Type).IsEqualTo("execute");
            await Assert.That(header.TargetSite).IsEqualTo("A");
            await Assert.That(header.Economy).IsEqualTo("full");
            await Assert.That(header.Tempo).IsEqualTo("slow");
            await Assert.That(header.TriggerText).IsEqualTo("on call at 1:15");
            await Assert.That(header.Status).IsEqualTo("Active");
            await Assert.That(header.Revision).IsEqualTo(4);
            await Assert.That(header.Role).IsEqualTo("entry");
        }
    }
}
