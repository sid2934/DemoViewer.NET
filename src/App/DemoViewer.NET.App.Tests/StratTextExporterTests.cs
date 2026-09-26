#region

using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The call sheet and the LAN Print HTML (strat-model.md §3.13, §3.14, §9 step 6): the Markdown shape
///     §3.13 gives verbatim, pinned against a committed golden, and the self-contained HTML page a Role
///     View export would hand to <see cref="LanPrint" />.
/// </summary>
public class StratTextExporterTests
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

    private static bool Updating => Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1";

    [Test]
    public async Task ACallSheetLine_MatchesSection313sOwnExampleVerbatim()
    {
        // "**1:30** B throws smoke A ramp → A site (Stairs)" is strat-model.md §3.13's own worked example.
        string sheet = StratTextExporter.CallSheet(SchemaSample(), Callouts());

        await Assert.That(sheet).Contains("**1:30** B throws smoke A ramp → A site (Stairs)");
    }

    [Test]
    public async Task TheSampleStratsCallSheet_MatchesTheCommittedGolden()
    {
        string? repo = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found from the test output directory");
        string path = Path.Combine(repo, "tests", "fixtures", "strats", "schema-v1.sample.callsheet.md");
        string rendered = StratTextExporter.CallSheet(SchemaSample(), Callouts());

        if (Updating)
        {
            File.WriteAllText(path, rendered);
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException($"missing {path}; regenerate with PB2D_GOLDEN_UPDATE=1");
        }

        string golden = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        await Assert.That(rendered).IsEqualTo(golden);
    }

    [Test]
    public async Task ABranch_PrintsIndentedUnderTheStepItFollows_WithTheTargetsStepNumber()
    {
        string sheet = StratTextExporter.CallSheet(SchemaSample(), Callouts());
        int molotovLine = sheet.IndexOf("**1:16**", StringComparison.Ordinal);
        int branchLine = sheet.IndexOf("if contact at Connector before 1:05", StringComparison.Ordinal);

        using (Assert.Multiple())
        {
            await Assert.That(molotovLine).IsGreaterThan(-1);
            await Assert.That(branchLine).IsGreaterThan(molotovLine);
            await Assert.That(sheet).Contains("if contact at Connector before 1:05 → A exec, double smoke, step 4");
        }
    }

    [Test]
    public async Task ALookupThatCannotResolveTheTarget_PrintsItsRawId()
    {
        StratDocument doc = Minimal(Guid.NewGuid());
        doc.Branches.Add(new StratBranch
        {
            Id = Guid.NewGuid(),
            AfterStepId = doc.Steps[0].Id,
            Condition = new BranchCondition { Text = "smoke fades" },
            Target = new BranchTarget { StratId = Guid.NewGuid() }
        });

        string sheet = StratTextExporter.CallSheet(doc);

        await Assert.That(sheet).Contains(doc.Branches[0].Target.StratId.ToString());
    }

    [Test]
    public async Task ALineupReference_PrintsTheResolvedTitle_ViaTheSharedPhrasing()
    {
        string resolved = StratTextExporter.CallSheet(SchemaSample(), Callouts(),
            lineupTitle: id => id == Guid.Parse("e4f5a6b7-c8d9-4e0f-9a1b-2c3d4e5f6a7b") ? "Molotov into Jungle" : null);

        await Assert.That(resolved).Contains("[lineup: Molotov into Jungle]");
    }

    [Test]
    public async Task TheNotes_PrintAfterTheSteps()
    {
        string sheet = StratTextExporter.CallSheet(SchemaSample(), Callouts());

        await Assert.That(sheet.TrimEnd()).EndsWith("Stairs smoke first, CT smoke second, then jungle molly.");
    }

    [Test]
    public async Task TheHtmlPage_HasOneSectionPerSheet_WithPrintPageBreaksAndNoExternalReferences()
    {
        StratDocument doc = SchemaSample();
        CalloutResolver callouts = Callouts();
        IReadOnlyList<RoleSheet> sheets =
        [
            RoleSheet.Derive(doc, "A", callouts),
            RoleSheet.Derive(doc, "B", callouts)
        ];

        string html = RoleSheetHtmlWriter.Html(doc, sheets);

        using (Assert.Multiple())
        {
            await Assert.That(html).Contains("<title>A exec, double smoke role sheets</title>");
            await Assert.That(CountOf(html, "<section class=\"sheet\">")).IsEqualTo(2);
            await Assert.That(html).Contains("Slot A");
            await Assert.That(html).Contains("Slot B");
            await Assert.That(html).Contains("page-break-after");
            await Assert.That(html).DoesNotContain("<link");
            await Assert.That(html).DoesNotContain("http://");
            await Assert.That(html).DoesNotContain("https://");
            await Assert.That(html).DoesNotContain("src=\"");
        }
    }

    [Test]
    public async Task AContextLine_RendersWithTheContextClass_AndAnOwnLineWithout()
    {
        StratDocument doc = SchemaSample();
        RoleSheet a = RoleSheet.Derive(doc, "A", Callouts());
        string html = RoleSheetHtmlWriter.Html(doc, [a]);

        using (Assert.Multiple())
        {
            await Assert.That(html).Contains("<li class=\"context\">");
            await Assert.That(html).Contains("<li>1:05");
        }
    }

    [Test]
    public async Task AStratNameWithMarkup_IsEscaped()
    {
        StratDocument doc = Minimal(Guid.NewGuid());
        doc.Name = "A & B <exec>";
        RoleSheet sheet = RoleSheet.Derive(doc, "A");

        string html = RoleSheetHtmlWriter.Html(doc, [sheet]);

        using (Assert.Multiple())
        {
            await Assert.That(html).Contains("A &amp; B &lt;exec&gt;");
            await Assert.That(html).DoesNotContain("A & B <exec>");
        }
    }

    [Test]
    public async Task APositionsPolyline_RendersAnSvgWithOnePointPerStep_AndIsOmittedWithNone()
    {
        StratDocument doc = SchemaSample();
        RoleSheet withPositions = RoleSheet.Derive(doc, "A");
        RoleSheet withNone = RoleSheet.Derive(doc, "E");

        string htmlWith = RoleSheetHtmlWriter.Html(doc, [withPositions]);
        string htmlWithout = RoleSheetHtmlWriter.Html(doc, [withNone]);

        using (Assert.Multiple())
        {
            await Assert.That(htmlWith).Contains("<svg class=\"minimap\"");
            await Assert.That(htmlWith).Contains("<polyline");
            await Assert.That(CountOf(htmlWith, "<circle")).IsEqualTo(withPositions.Positions.Count);
            await Assert.That(htmlWithout).DoesNotContain("<svg");
        }
    }

    [Test]
    public async Task WriteTempFile_WritesTheExactHtml_ToAFreshDotHtmlPathEachTime()
    {
        string html = "<!doctype html><html><body>hi</body></html>";
        string first = LanPrint.WriteTempFile(html);
        string second = LanPrint.WriteTempFile(html);

        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(first).IsNotEqualTo(second);
                await Assert.That(first).EndsWith(".html");
                await Assert.That(File.ReadAllText(first)).IsEqualTo(html);
                await Assert.That(File.ReadAllText(second)).IsEqualTo(html);
            }
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
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
