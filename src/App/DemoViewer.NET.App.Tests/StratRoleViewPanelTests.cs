#region

using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Role View And LAN Print (plan.md §3; strat-model.md §3.14): one slot's parts on screen, switched by
///     <see cref="StratRoleViewPanelViewModel.SelectedSlot" />, and Print writing every slot's sheet as one
///     HTML page (the item's own done line: "a strat prints to one sheet per slot").
/// </summary>
public class StratRoleViewPanelTests
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

    private static string Line(RoleViewLineRow line) => (line.IsContext ? "context: " : "own: ") + line.Text;

    [Test]
    public async Task WithNoStrat_ThePanelIsEmpty()
    {
        StratRoleViewPanelViewModel vm = new();

        vm.Configure(null, null, null, null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasStrat).IsFalse();
            await Assert.That(vm.Header).IsNull();
            await Assert.That(vm.Lines).IsEmpty();
            await Assert.That(vm.Branches).IsEmpty();
            await Assert.That(vm.CanPrint).IsFalse();
        }
    }

    [Test]
    public async Task ConfiguringAStrat_ShowsTheDefaultSlotsSheet()
    {
        StratRoleViewPanelViewModel vm = new();
        StratDocument doc = SchemaSample();

        vm.Configure(doc, Callouts(), null, null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasStrat).IsTrue();
            await Assert.That(vm.SelectedSlot).IsEqualTo("A").Because("the picker starts on the first slot");
            await Assert.That(vm.Lines.Select(Line)).IsEquivalentTo(
            [
                "context: B throws smoke A ramp → A site (Stairs)",
                "context: C throws molotov A ramp → A site (Jungle) [lineup: e4f5a6b7-c8d9-4e0f-9a1b-2c3d4e5f6a7b]",
                "own: A peeks A ramp → Connector",
                "own: All move A ramp → A site"
            ]);
            await Assert.That(vm.CanPrint).IsTrue();
        }
    }

    [Test]
    public async Task ChangingTheSlot_RebuildsTheLinesAndBranches()
    {
        StratRoleViewPanelViewModel vm = new();
        vm.Configure(SchemaSample(), Callouts(), null, null);

        vm.SelectedSlot = "C";

        using (Assert.Multiple())
        {
            await Assert.That(vm.Lines.Any(l => !l.IsContext && l.Text.Contains("molotov", StringComparison.Ordinal))).IsTrue();
            await Assert.That(vm.Branches).IsEquivalentTo(["if contact at Connector before 1:05 → A exec, double smoke, step 4"]);
        }

        vm.SelectedSlot = "A";
        await Assert.That(vm.Branches).IsEmpty().Because("the branch after the molotov step belongs to C alone");
    }

    [Test]
    public async Task TheHeaderLines_MatchTheSheetsMasthead()
    {
        StratRoleViewPanelViewModel vm = new();
        vm.Configure(SchemaSample(), Callouts(), null, null);
        vm.SelectedSlot = "D";

        using (Assert.Multiple())
        {
            await Assert.That(vm.SlotLine).IsEqualTo("Slot D (awp)").Because("D has a role but no roster name");
            await Assert.That(vm.MetaLine).IsEqualTo("de_mirage · T execute (site A) · full economy · slow tempo");
            await Assert.That(vm.TriggerLine).IsEqualTo("Trigger: on call at 1:15");
            await Assert.That(vm.StatusFooter).IsEqualTo("Status: Active · revision 4");
        }
    }

    [Test]
    public async Task ARoster_ResolvesTheSlotsNameInTheHeaderLine()
    {
        StratRoleViewPanelViewModel vm = new();
        vm.Configure(SchemaSample(), Callouts(), new Dictionary<string, string?> { ["B"] = "Ferris" }, null);
        vm.SelectedSlot = "B";

        await Assert.That(vm.SlotLine).IsEqualTo("Slot B: Ferris (support)");
    }

    [Test]
    public async Task PositionsCount_FollowsTheSelectedSlot()
    {
        StratRoleViewPanelViewModel vm = new();
        vm.Configure(SchemaSample(), Callouts(), null, null);

        await Assert.That(vm.PositionCount).IsEqualTo(2).Because("slot A's own two positions");
        await Assert.That(vm.HasPositions).IsTrue();

        vm.SelectedSlot = "E";
        using (Assert.Multiple())
        {
            await Assert.That(vm.PositionCount).IsEqualTo(0);
            await Assert.That(vm.HasPositions).IsFalse();
        }
    }

    [Test]
    public async Task Print_WritesAllFiveSlotsAsOnePage_RegardlessOfWhichOneIsSelected()
    {
        string? capturedHtml = null;
        string? capturedHint = null;
        StratRoleViewPanelViewModel vm = new(print: (html, hint) =>
        {
            capturedHtml = html;
            capturedHint = hint;
            return "/tmp/A exec-roles-abc.html";
        });
        vm.Configure(SchemaSample(), Callouts(), null, null);
        vm.SelectedSlot = "C";

        vm.PrintCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(capturedHtml).IsNotNull();
            await Assert.That(CountOf(capturedHtml!, "<section class=\"sheet\">")).IsEqualTo(5);
            await Assert.That(capturedHint).IsNotNull();
            await Assert.That(capturedHint).Contains("roles");
            await Assert.That(vm.StatusLine).IsEqualTo("opened A exec-roles-abc.html");
        }
    }

    [Test]
    public async Task Print_WhenTheHostCannotOpenTheFile_ReportsIt()
    {
        StratRoleViewPanelViewModel vm = new(print: (_, _) => null);
        vm.Configure(SchemaSample(), Callouts(), null, null);

        vm.PrintCommand.Execute(null);

        await Assert.That(vm.StatusLine).IsEqualTo("could not open the role sheets");
    }

    [Test]
    public async Task OnTheBrowserHost_PrintIsUnavailable_EvenWithAStratOpen()
    {
        StratRoleViewPanelViewModel vm = new(isBrowser: true);
        vm.Configure(SchemaSample(), Callouts(), null, null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.CanPrint).IsFalse();
            await Assert.That(vm.HasPrintUnavailableNote).IsTrue();
            await Assert.That(vm.PrintUnavailableNote).Contains("browser");
        }
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}
