#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Dossier Editing And Export (plan.md §3, Phase 5): the exporter's two forms and two files, the
///     notes store's persistence, the editor's star, rewrite, leave-out and note actions, and the tab
///     collecting its sections' lines, with the one-pager as the default export.
/// </summary>
[NotInParallel]
public class DossierEditingTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly byte[] _png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static DossierDocument Doc(params DossierFinding[] findings) => new()
    {
        TeamName = "Falcons <B>",
        SampleLine = "6 demos across 2 maps",
        Summary = "They default A on pistols.",
        Findings = findings
    };

    private static DossierFinding Line(string key, string section, string text, bool starred = false, byte[]? png = null) =>
        new() { Key = key, Section = section, Text = text, IsStarred = starred, ImagePng = png };

    private static DossierFindingSource[] Sources() =>
    [
        new("map|de_nuke", DossierEditorViewModel.MapPoolSection, "de_nuke: 2 played, 1-1"),
        new("map|de_mirage", DossierEditorViewModel.MapPoolSection, "de_mirage: 1 played, 0-1"),
        new("heatmap|de_nuke|full buy", DossierEditorViewModel.HeatmapSection, "de_nuke CT full buy: 10 rounds", () => _png)
    ];

    [Test]
    public async Task TheOnePager_PrintsOnlyStarredLines_AndIsTheDefaultForm()
    {
        DossierDocument doc = Doc(
            Line("a", "Map Pool Record", "de_nuke: 2 played", starred: true),
            Line("b", "Map Pool Record", "de_mirage: 1 played"),
            Line("c", "Opening Tendencies", "de_nuke T · site A: 4 of 10 rounds"));

        string md = DossierExporter.Markdown(doc);
        string longMd = DossierExporter.Markdown(doc, DossierForm.LongForm);

        using (Assert.Multiple())
        {
            await Assert.That(md).StartsWith("# Falcons <B> one-pager\n");
            await Assert.That(md).Contains("Sample: 6 demos across 2 maps");
            await Assert.That(md).Contains("They default A on pistols.");
            await Assert.That(md).Contains("## Map Pool Record\n\n- de_nuke: 2 played\n");
            await Assert.That(md).DoesNotContain("de_mirage");
            await Assert.That(md).DoesNotContain("Opening Tendencies").Because("a section with nothing starred is left off the one-pager");

            await Assert.That(longMd).StartsWith("# Falcons <B> dossier\n");
            await Assert.That(longMd).Contains("- ★ de_nuke: 2 played\n- de_mirage: 1 played\n");
            await Assert.That(longMd).Contains("## Opening Tendencies");
        }
    }

    [Test]
    public async Task TheOnePager_WithNothingStarred_SaysHowToBuildIt()
    {
        DossierDocument doc = Doc(Line("a", "Map Pool Record", "de_nuke: 2 played"));

        using (Assert.Multiple())
        {
            await Assert.That(DossierExporter.Markdown(doc)).Contains(DossierExporter.NothingStarredLine);
            await Assert.That(DossierExporter.Html(doc)).Contains(DossierExporter.NothingStarredLine);
        }
    }

    [Test]
    public async Task TheHtml_IsSelfContained_Escaped_AndEmbedsTheHeatmap()
    {
        DossierDocument doc = Doc(
            Line("h", "Setup Heatmaps By Buy", "de_nuke CT full buy: <10> rounds", starred: true, png: _png),
            Line("b", "Map Pool Record", "de_mirage: 1 played"));

        string html = DossierExporter.Html(doc);
        string longHtml = DossierExporter.Html(doc, DossierForm.LongForm);

        using (Assert.Multiple())
        {
            await Assert.That(html).StartsWith("<!doctype html>");
            await Assert.That(html).Contains("<title>Falcons &lt;B&gt; one-pager</title>");
            await Assert.That(html).Contains("de_nuke CT full buy: &lt;10&gt; rounds");
            await Assert.That(html).Contains("src=\"data:image/png;base64," + Convert.ToBase64String(_png) + "\"");
            await Assert.That(html).DoesNotContain("de_mirage");
            await Assert.That(html).DoesNotContain("http").Because("no external reference: the emailed file renders alone");
            await Assert.That(html).DoesNotContain("<script");
            await Assert.That(html).DoesNotContain("<link");
            await Assert.That(longHtml).Contains("<li class=\"starred\">");
            await Assert.That(longHtml).Contains("de_mirage");
        }
    }

    [Test]
    public async Task TheExport_AddsNoRatingGradeOrWinProbability()
    {
        DossierDocument doc = Doc(Line("a", "Map Pool Record", "de_nuke: 2 played, 1-1", starred: true));

        foreach (string text in new[]
                 {
                     DossierExporter.Markdown(doc), DossierExporter.Html(doc),
                     DossierExporter.Markdown(doc, DossierForm.LongForm), DossierExporter.Html(doc, DossierForm.LongForm)
                 })
        {
            string lower = text.ToLowerInvariant();
            using (Assert.Multiple())
            {
                await Assert.That(lower).DoesNotContain("rating");
                await Assert.That(lower).DoesNotContain("grade");
                await Assert.That(lower).DoesNotContain("probability");
                await Assert.That(lower).DoesNotContain("chance");
            }
        }
    }

    [Test]
    public async Task TheStore_PersistsStarsEditsHiddenNotesAndSummary_BesideTeamsJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-dossier-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid team = Guid.NewGuid();
            DossierNotesStore store = new(root);
            int changes = 0;
            store.Changed += () => changes++;
            store.SetStarred(team, "map|de_nuke", true);
            store.SetStarred(team, "map|de_nuke", true);
            store.SetEdit(team, "map|de_mirage", "  mirage is their comfort pick ", "de_mirage: 1 played");
            store.SetEdit(team, "map|de_inferno", "de_inferno: 3 played", "de_inferno: 3 played");
            store.SetHidden(team, "heatmap|x", true);
            string? note = store.AddNote(team, "  watch the B apps smoke  ");
            store.SetSummary(team, "  Aggressive CT.  ");

            await Assert.That(changes).IsEqualTo(5).Because("a repeat star and an edit equal to the generated text change nothing");
            await Assert.That(File.Exists(Path.Combine(root, DossierNotesStore.FileName))).IsTrue();

            DossierTeamNotes reloaded = new DossierNotesStore(root).For(team);
            using (Assert.Multiple())
            {
                await Assert.That(reloaded.Starred).IsEquivalentTo(["map|de_nuke"]);
                await Assert.That(reloaded.Edits["map|de_mirage"]).IsEqualTo("mirage is their comfort pick");
                await Assert.That(reloaded.Edits.ContainsKey("map|de_inferno")).IsFalse();
                await Assert.That(reloaded.Hidden).IsEquivalentTo(["heatmap|x"]);
                await Assert.That(reloaded.Notes.Single().Text).IsEqualTo("watch the B apps smoke");
                await Assert.That(DossierNotesStore.NoteKey(reloaded.Notes.Single().Id)).IsEqualTo(note);
                await Assert.That(reloaded.Summary).IsEqualTo("Aggressive CT.");
            }

            // A file at a newer schema is refused and never overwritten.
            string path = Path.Combine(root, DossierNotesStore.FileName);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\": 99, \"teams\": []}");
            DossierNotesStore refused = new(root);
            refused.SetSummary(team, "overwrite?");
            using (Assert.Multiple())
            {
                await Assert.That(refused.FileProblem).IsNotNull();
                await Assert.That(await File.ReadAllTextAsync(path)).Contains("99");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TheEditor_StarsRewritesLeavesOutAndAddsNotes_AndKeepsThemAcrossARebuild()
    {
        DossierNotesStore store = new(null);
        DossierEditorViewModel editor = new(store);
        Guid team = Guid.NewGuid();
        editor.Load(team, "Falcons", "3 demos across 2 maps", Sources());

        using (Assert.Multiple())
        {
            await Assert.That(editor.Findings.Count).IsEqualTo(3);
            await Assert.That(editor.Findings[0].ShowsSectionHeader).IsTrue();
            await Assert.That(editor.Findings[1].ShowsSectionHeader).IsFalse();
            await Assert.That(editor.Findings[2].ShowsSectionHeader).IsTrue();
            await Assert.That(editor.ExportForm).IsEqualTo(DossierForm.OnePager).Because("the short form is the default export");
            await Assert.That(editor.ExportFormat).IsEqualTo(DossierFormat.Html);
            await Assert.That(editor.ExportLabel).IsEqualTo("Export one-pager");
            await Assert.That(editor.CountsLine).IsEqualTo("3 findings · 0 starred");
        }

        DossierFindingViewModel nuke = editor.Findings[0];
        editor.ToggleStarCommand.Execute(nuke);
        nuke.BeginEditCommand.Execute(null);
        await Assert.That(nuke.EditText).IsEqualTo("de_nuke: 2 played, 1-1");
        nuke.EditText = "de_nuke: their best map";
        editor.CommitEditCommand.Execute(nuke);
        editor.RemoveCommand.Execute(editor.Findings[1]);
        editor.NewNoteText = "they stack B after a lost pistol";
        editor.AddNoteCommand.Execute(null);
        editor.Summary = "Slow defaults.";

        using (Assert.Multiple())
        {
            await Assert.That(nuke.IsStarred).IsTrue();
            await Assert.That(nuke.IsEdited).IsTrue();
            await Assert.That(nuke.IsEditing).IsFalse();
            await Assert.That(editor.Findings.Select(f => f.Key)).DoesNotContain("map|de_mirage");
            await Assert.That(editor.HasHidden).IsTrue();
            await Assert.That(editor.Findings.Last().IsNote).IsTrue();
            await Assert.That(editor.Findings.Last().Section).IsEqualTo(DossierEditorViewModel.NotesSection);
            await Assert.That(editor.CountsLine).IsEqualTo("3 findings · 1 starred · 1 left out");
        }

        // A section rebuild in another order keeps the star, the rewrite and the left-out line by key.
        editor.Load(team, "Falcons", "3 demos across 2 maps", [.. Sources().Reverse()]);
        DossierFindingViewModel again = editor.Findings.Single(f => f.Key == "map|de_nuke");
        using (Assert.Multiple())
        {
            await Assert.That(again.IsStarred).IsTrue();
            await Assert.That(again.Text).IsEqualTo("de_nuke: their best map");
            await Assert.That(editor.Findings.Any(f => f.Key == "map|de_mirage")).IsFalse();
            await Assert.That(editor.Summary).IsEqualTo("Slow defaults.");
        }

        // The one-pager is the starred line alone; "starred only" previews it.
        DossierDocument doc = editor.Document();
        string md = DossierExporter.Markdown(doc);
        editor.ShowStarredOnly = true;
        using (Assert.Multiple())
        {
            await Assert.That(md).Contains("- de_nuke: their best map");
            await Assert.That(md).Contains("Slow defaults.");
            await Assert.That(md).DoesNotContain("full buy");
            await Assert.That(doc.Findings.Single(f => f.Key.StartsWith("heatmap", StringComparison.Ordinal)).ImagePng).IsEquivalentTo(_png);
            await Assert.That(editor.Findings.Select(f => f.Key)).IsEquivalentTo(["map|de_nuke"]);
        }

        // Reset puts the generated line back; restore brings the left-out line back.
        editor.ShowStarredOnly = false;
        editor.ResetEditCommand.Execute(again);
        editor.RestoreHiddenCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(again.Text).IsEqualTo("de_nuke: 2 played, 1-1");
            await Assert.That(store.For(team).Edits).IsEmpty();
            await Assert.That(editor.Findings.Any(f => f.Key == "map|de_mirage")).IsTrue();
        }

        // Removing a note removes it outright.
        editor.RemoveCommand.Execute(editor.Findings.Single(f => f.IsNote));
        await Assert.That(store.For(team).Notes).IsEmpty();
    }

    [Test]
    public async Task Export_WritesTheOnePagerAsHtml_ByDefault_AndTheChosenFormOtherwise()
    {
        List<(string Text, string Stem, string Extension)> written = [];
        DossierEditorViewModel editor = new(new DossierNotesStore(null), export: (text, stem, ext) =>
        {
            written.Add((text, stem, ext));
            return Path.Combine(Path.GetTempPath(), stem + ext);
        });

        await Assert.That(editor.ExportCommand.CanExecute(null)).IsFalse().Because("no team selected");
        editor.Load(Guid.NewGuid(), "Falcons", "3 demos", Sources());
        editor.ToggleStarCommand.Execute(editor.Findings[0]);
        editor.ExportCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(written.Count).IsEqualTo(1);
            await Assert.That(written[0].Extension).IsEqualTo(".html");
            await Assert.That(written[0].Stem).IsEqualTo("Falcons-one-pager");
            await Assert.That(written[0].Text).Contains("<title>Falcons one-pager</title>");
            await Assert.That(written[0].Text).DoesNotContain("de_mirage");
            await Assert.That(editor.ExportLine).IsEqualTo("one-pager written: Falcons-one-pager.html");
        }

        editor.SelectedFormOption = DossierEditorViewModel.FormOptions.Single(o => o.Value == DossierForm.LongForm);
        editor.SelectedFormatOption = DossierEditorViewModel.FormatOptions.Single(o => o.Value == DossierFormat.Markdown);
        editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(editor.ExportLabel).IsEqualTo("Export long form");
            await Assert.That(written[1].Extension).IsEqualTo(".md");
            await Assert.That(written[1].Stem).IsEqualTo("Falcons-dossier");
            await Assert.That(written[1].Text).StartsWith("# Falcons dossier");
            await Assert.That(written[1].Text).Contains("de_mirage");
        }

        DossierEditorViewModel browser = new(new DossierNotesStore(null), isBrowser: true);
        browser.Load(Guid.NewGuid(), "Falcons", "", Sources());
        using (Assert.Multiple())
        {
            await Assert.That(browser.ExportCommand.CanExecute(null)).IsFalse();
            await Assert.That(browser.HasExportUnavailableNote).IsTrue();
            await Assert.That(browser.MarkdownText()).StartsWith("# Falcons one-pager");
        }
    }

    [Test]
    public async Task TheSectionBuilders_StateEverySample_AndSkipZeroCounts()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DossierEditorViewModel.CountText(4, 10)).IsEqualTo("4 of 10 rounds");
            await Assert.That(DossierEditorViewModel.CountText(1, 0)).IsEqualTo("1 round");
            await Assert.That(DossierEditorViewModel.FromPeriodDiff(null, null, "x")).IsEmpty();
        }
    }

    [Test]
    public async Task TheTab_CollectsItsSections_IntoTheEditor_AndExportsTheStarredOnes()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache, run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record("/d/s1.dem", 10, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_nuke", 9, 16));
            cache.Upsert(Record("/d/s2.dem", 11, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_mirage", 9, 13));
        }

        await teams.Idle;
        Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup != null && r.CoreLineup.Contains(Ids(1)[0]))).Id;
        List<string> written = [];
        VetoHistoryStore vetoes = new(null);
        using DossierTabViewModel vm = new(teams, cache, vetoes, isBrowser: false, notes: new DossierNotesStore(null),
            export: (text, _, _) =>
            {
                written.Add(text);
                return "x.html";
            });

        await Assert.That(vm.Editor.HasTeam).IsFalse();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);

        DossierFindingViewModel[] maps = [.. vm.Editor.Findings.Where(f => f.Section == DossierEditorViewModel.MapPoolSection)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Editor.HasTeam).IsTrue();
            await Assert.That(maps.Length).IsEqualTo(2);
            await Assert.That(maps.Select(f => f.Key)).Contains("map|de_nuke");
            await Assert.That(vm.Editor.Findings.Any(f => f.Section == DossierEditorViewModel.PeriodDiffSection)).IsTrue();
        }

        // A veto step the user adds lands as a line of its own.
        vm.NewVetoMap = "de_ancient";
        vm.AddVetoCommand.Execute(null);
        await Assert.That(vm.Editor.Findings.Any(f => f.Section == DossierEditorViewModel.VetoSection && f.Text.Contains("de_ancient"))).IsTrue();

        vm.Editor.ToggleStarCommand.Execute(vm.Editor.Findings.Single(f => f.Key == "map|de_nuke"));
        vm.Editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(written.Single()).Contains("one-pager");
            await Assert.That(written.Single()).Contains("de_nuke: 1 played");
            await Assert.That(written.Single()).DoesNotContain("de_mirage");
        }

        vm.SelectedTeam = null;
        await Assert.That(vm.Editor.HasTeam).IsFalse();
        await Assert.That(vm.Editor.HasFindings).IsFalse();
    }

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map, int ctScore, int tScore)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            CtScore = ctScore,
            TScore = tScore,
            CtSideWins = ctScore,
            TSideWins = tScore
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }
}
