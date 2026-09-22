#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="MatchFacetForm" /> against the real catalog and the shipped corpus in
///     <c>rules/</c>, which is where design.md §6.7's claim for this interaction is settled.
///     <para>
///         <b>The claim is that a form scoped to ONE view beats a text pane that cannot scope at
///         all.</b> Both halves are asserted here: the catalog's 18 views and 83 typed facets narrow
///         to 9 rows on <c>kill</c> and 21 on <c>shot_landed</c>, while
///         <c>WorkbenchCompletionSource.Build</c> offers every facet name in the catalog whatever the
///         stat fires on.
///     </para>
///     <para>
///         Pure by construction: the embedded catalog, a YAML parse and a static projection, so there
///         is no Avalonia app and no demo, and the class stays in every tier.
///     </para>
/// </summary>
public class MatchFacetFormTests
{
    /// <summary>
    ///     The catalog numbers the interaction is sized against. A form is static metadata that is
    ///     already loaded, and these are its dimensions: 18 views, 83 facets, largest form 21 rows.
    /// </summary>
    [Test]
    public async Task TheCatalog_Carries18Views_And83TypedFacets()
    {
        CatalogRoot catalog = CatalogResource.Load();
        MatchFacetForm[] forms = [.. catalog.Views.Select(v => MatchFacetForm.For(catalog, v.Name, null))];

        Dictionary<string, int> byType = forms.SelectMany(f => f.Rows)
            .GroupBy(r => r.Type.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Console.WriteLine($"[facets] views={forms.Length} rows={forms.Sum(f => f.Rows.Count)} "
                          + string.Join(" ", byType.OrderBy(e => e.Key, StringComparer.Ordinal)
                              .Select(e => $"{e.Key}={e.Value}")));

        await Assert.That(forms.Length).IsEqualTo(18);
        await Assert.That(forms.Sum(f => f.Rows.Count)).IsEqualTo(83);
        await Assert.That(byType["bool"]).IsEqualTo(40);
        await Assert.That(byType["int"]).IsEqualTo(21);
        await Assert.That(byType["float"]).IsEqualTo(16);
        await Assert.That(byType["string"]).IsEqualTo(6);

        // Every facet in the catalog reaches a control. A type the form cannot draw is listed and
        // not editable, which is honest, but there is none today and a new one should be noticed.
        await Assert.That(forms.SelectMany(f => f.Rows).Any(r => r.Editor == MatchFacetEditor.Unsupported))
            .IsFalse()
            .Because("bool, int, float and string are the only facet types the catalog carries");
    }

    /// <summary>
    ///     The narrowing itself: a form offers the facets of ITS view, not the catalog's 83. The three
    ///     sizes design.md §6.7 quotes are the bounds a form's layout has to hold.
    /// </summary>
    [Test]
    public async Task AFormIsScopedToItsView_RatherThanToTheWhole83()
    {
        CatalogRoot catalog = CatalogResource.Load();

        await Assert.That(MatchFacetForm.For(catalog, "shot_landed", null).Rows.Count).IsEqualTo(21);
        await Assert.That(MatchFacetForm.For(catalog, "shot", null).Rows.Count).IsEqualTo(16);
        await Assert.That(MatchFacetForm.For(catalog, "kill", null).Rows.Count).IsEqualTo(9);

        int widest = catalog.Views.Max(v => MatchFacetForm.For(catalog, v.Name, null).Rows.Count);
        await Assert.That(widest).IsEqualTo(21)
            .Because("21 rows is the largest form there is, which is what makes this cheap to draw");
    }

    /// <summary>
    ///     The reason the canvas wins here, stated as a comparison rather than as an opinion.
    ///     <c>counter_strafe_good</c> is legal on <c>shot</c> and is not a facet of <c>kill</c> at all,
    ///     and the completion source offers it either way because it iterates every view.
    ///     <b>This asserts <c>WorkbenchCompletionSource</c>'s current behaviour; it is not a defect
    ///     filed against it</b>, and changing that file is not what fixes this.
    /// </summary>
    [Test]
    public async Task AFacetOfOneView_IsAbsentFromAnother_WhereCompletionOffersItAnyway()
    {
        CatalogRoot catalog = CatalogResource.Load();

        await Assert.That(MatchFacetForm.For(catalog, "shot", null).Rows.Any(r => r.Name == "counter_strafe_good"))
            .IsTrue();
        await Assert.That(MatchFacetForm.For(catalog, "kill", null).Rows.Any(r => r.Name == "counter_strafe_good"))
            .IsFalse()
            .Because("a counter-strafe is a property of a shot, and matching it on a kill is meaningless");

        // The completion vocabulary at a `count: kill` value position, which is exactly where an
        // author would be told this if the text pane could tell them.
        IReadOnlyList<WorkbenchCompletion> offered = WorkbenchCompletionSource.Build(
            catalog, null, new WorkbenchCompletionContext("count", false));
        string[] facets = [.. offered.Where(c => c.Category == "facet").Select(c => c.Text)];

        await Assert.That(facets).Contains("counter_strafe_good")
            .Because("WorkbenchCompletion.Build iterates every view, so the caret's view narrows nothing");
        int everyFacetName = catalog.Views.SelectMany(v => v.Facets)
            .Select(f => f.Name).Distinct(StringComparer.Ordinal).Count();
        await Assert.That(facets.Length).IsEqualTo(everyFacetName)
            .Because("the text pane offers every facet name in the catalog; the form offers 9 on kill");
    }

    /// <summary>
    ///     The comparator, which is the half of the value model a checkbox does not cover.
    ///     <c>aim_rating</c>'s <c>aimrx_shots</c> fires on <c>shot</c> and writes
    ///     <c>ticks_since_on_target: "&lt;= 320"</c>, so the row has to come back with the operator and
    ///     the number apart, not as the string the author typed.
    /// </summary>
    [Test]
    public async Task ANumericFacet_KeepsItsComparatorAndItsNumberApart()
    {
        MatchFacetForm form = FormFor("aim_rating", "aimrx_shots");

        await Assert.That(form.View).IsEqualTo("shot");
        await Assert.That(form.Rows.Count).IsEqualTo(16);
        await Assert.That(form.Set.Count()).IsEqualTo(3)
            .Because("aimrx_shots binds first_after_on_target, ticks_since_on_target and bullet");

        MatchFacetRow ticks = form.Rows.Single(r => r.Name == "ticks_since_on_target");
        await Assert.That(ticks.Editor).IsEqualTo(MatchFacetEditor.Number);
        await Assert.That(ticks.Comparator).IsEqualTo(ComparisonOperator.LessOrEqual);
        await Assert.That((ticks.Value as MatchComparison)?.Literal).IsEqualTo("320");
        await Assert.That(ticks.Position.Line).IsGreaterThan(0)
            .Because("an authored row can jump the caret to its own key, the way a node does");

        MatchFacetRow bullet = form.Rows.Single(r => r.Name == "bullet");
        await Assert.That(bullet.Editor).IsEqualTo(MatchFacetEditor.Toggle);
        await Assert.That(bullet.Toggle).IsEqualTo(true);

        MatchFacetRow weapon = form.Rows.Single(r => r.Name == "weapon");
        await Assert.That(weapon.Editor).IsEqualTo(MatchFacetEditor.Text);
        await Assert.That(weapon.IsSet).IsFalse();
        await Assert.That(weapon.Position).IsEqualTo(SourcePosition.None)
            .Because("an unbound facet is a row the form offers, not a place in the file");
    }

    /// <summary>
    ///     Every <c>match:</c> block in the corpus, against the view its own stat resolved to. A key
    ///     that landed in <see cref="MatchFacetForm.Unknown" /> would be a key the form cannot edit,
    ///     and the resolver would already have reported it as an unknown facet.
    /// </summary>
    [Test]
    public async Task EveryMatchBlockInTheCorpus_LandsOnARowOfItsView()
    {
        int stats = 0, withMatch = 0, bindings = 0, unknown = 0, unplaced = 0;

        foreach ((StatDef def, CheckedStat stat) in CorpusStats())
        {
            stats++;
            if ((def.Trigger?.Match.Count ?? 0) == 0)
            {
                continue;
            }

            withMatch++;
            bindings += def.Trigger!.Match.Count;
            MatchFacetForm form = MatchFacetForm.For(CatalogResource.Load(), stat.ResolvedView, def.Trigger.Match);
            unknown += form.Unknown.Count;
            unplaced += form.Set.Count(r => r.Position.Line <= 0);

            if (form.Unknown.Count > 0)
            {
                Console.WriteLine($"[unknown] {stat.Ruleset.Id}.{def.Id} view={stat.ResolvedView} "
                                  + string.Join(", ", form.Unknown.Select(b => b.Key)));
            }
        }

        Console.WriteLine($"[corpus] stats={stats} with-match={withMatch} bindings={bindings}");

        // design.md §6.7 quotes 88 of 167; the 88 reproduces exactly and the denominator measures
        // 172 stat entries here, so the share is 51% rather than 53%.
        await Assert.That(withMatch).IsEqualTo(88)
            .Because("88 stats in rules/ declare a match: block, and the editing panel can touch none of them");
        await Assert.That(bindings).IsEqualTo(122);
        await Assert.That(unknown).IsEqualTo(0)
            .Because("a key with no facet on the view is an UnknownFacet diagnostic, and the corpus has none");
        await Assert.That(unplaced).IsEqualTo(0)
            .Because("every authored binding carries the position of its own key");
    }

    /// <summary>
    ///     The form's values render back to text the engine's own parser reads as the same test, which
    ///     is the property a YAML writer needs before it can write anything at all. Asserted over every
    ///     authored binding in the corpus, so it is the corpus that decides, not a fixture.
    /// </summary>
    [Test]
    public async Task EveryAuthoredTest_RendersBackToWhatTheParserRead()
    {
        List<RulesetDiagnostic> diagnostics = [];
        int round = 0;

        foreach ((StatDef def, CheckedStat stat) in CorpusStats())
        {
            MatchFacetForm form = MatchFacetForm.For(CatalogResource.Load(), stat.ResolvedView, def.Trigger?.Match);
            foreach (MatchFacetRow row in form.Set)
            {
                string text = row.Value!.ToMatchValue();
                MatchFacetValue? reparsed =
                    MatchFacetValue.From(UnaryTestParser.Parse(text, SourcePosition.None, diagnostics));

                await Assert.That(reparsed?.ToMatchValue()).IsEqualTo(text)
                    .Because($"{stat.Ruleset.Id}.{def.Id} match {row.Name} rendered '{text}'");
                round++;
            }
        }

        Console.WriteLine($"[round-trip] values={round} diagnostics={diagnostics.Count}");
        await Assert.That(round).IsEqualTo(122);
        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because("a rendered value the parser rejects is a value the writer would corrupt");
    }

    /// <summary>
    ///     The three shapes the shipped corpus never writes. They are legal grammar, so a form that
    ///     dropped them on a save would delete an author's filter; they round-trip like the rest.
    /// </summary>
    [Test]
    public async Task TheShapesTheCorpusDoesNotUse_RoundTripToo()
    {
        List<RulesetDiagnostic> diagnostics = [];
        string[] shapes = ["[2..5]", "in rifles", "in [ak47, m4a1]", "!= 0", "> 1.5"];

        foreach (string text in shapes)
        {
            MatchFacetValue? value =
                MatchFacetValue.From(UnaryTestParser.Parse(text, SourcePosition.None, diagnostics));

            await Assert.That(value?.ToMatchValue()).IsEqualTo(text);
        }

        await Assert.That(diagnostics.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A view with no facets and a view that does not exist produce the same empty row list and
    ///     mean opposite things, so the form says which it is. Four of the catalog's 18 views really do
    ///     offer nothing to match on.
    /// </summary>
    [Test]
    public async Task AViewWithNoFacets_IsNotTheSameAsAViewTheCatalogDoesNotHave()
    {
        CatalogRoot catalog = CatalogResource.Load();

        MatchFacetForm grenade = MatchFacetForm.For(catalog, "he_grenade", null);
        await Assert.That(grenade.Rows.Count).IsEqualTo(0);
        await Assert.That(grenade.IsKnownView).IsTrue();

        MatchFacetForm nonesuch = MatchFacetForm.For(catalog, "no_such_view", null);
        await Assert.That(nonesuch.IsKnownView).IsFalse();
        await Assert.That(nonesuch.View).IsEqualTo("no_such_view");

        // A compute: resolves to no view at all, which is neither of the above.
        MatchFacetForm none = MatchFacetForm.For(catalog, null, null);
        await Assert.That(none.IsKnownView).IsFalse();
        await Assert.That(none.View).IsEqualTo("");
        await Assert.That(MatchFacetForm.None.Rows.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A key the view has no facet for is kept, not dropped. The form is an editing surface over a
    ///     block the resolver has already rejected, and a surface that hid the rejected half would
    ///     write it back out of existence.
    /// </summary>
    [Test]
    public async Task AKeyThatIsNotAFacetOfTheView_IsKeptRatherThanDropped()
    {
        List<RulesetDiagnostic> diagnostics = [];
        MatchBinding stray = new("counter_strafe_good",
            UnaryTestParser.Parse("true", SourcePosition.None, diagnostics)!, SourcePosition.None);

        MatchFacetForm form = MatchFacetForm.For(CatalogResource.Load(), "kill", [stray]);

        await Assert.That(form.Rows.Count).IsEqualTo(9);
        await Assert.That(form.Set.Count()).IsEqualTo(0);
        await Assert.That(form.Unknown.Count).IsEqualTo(1)
            .Because("the resolver reports this as 'not a facet of view kill', and the form has to show it");
        await Assert.That(form.Unknown[0].Key).IsEqualTo("counter_strafe_good");
    }

    /// <summary>
    ///     A repeated key resolves the way the engine resolves it: the first entry wins, after a
    ///     DuplicateMatchKey diagnostic. The form shows the binding the engine is going to use.
    /// </summary>
    [Test]
    public async Task ARepeatedKey_ShowsTheEntryTheEngineUses()
    {
        List<RulesetDiagnostic> diagnostics = [];
        MatchBinding first = new("enemy", UnaryTestParser.Parse("true", SourcePosition.None, diagnostics)!,
            new SourcePosition(null, 3, 5));
        MatchBinding second = new("enemy", UnaryTestParser.Parse("false", SourcePosition.None, diagnostics)!,
            new SourcePosition(null, 4, 5));

        MatchFacetForm form = MatchFacetForm.For(CatalogResource.Load(), "kill", [first, second]);

        await Assert.That(form.Rows.Single(r => r.Name == "enemy").Toggle).IsEqualTo(true);
        await Assert.That(form.Rows.Single(r => r.Name == "enemy").Position.Line).IsEqualTo(3);
    }

    // ── the corpus, composed the way the Workbench composes the open file ────────────────────────

    /// <summary>
    ///     Every shipped stat paired with its checked form, which is where <c>ResolvedView</c> comes
    ///     from: the engine's own answer for which view the stat fires on, already resolved through
    ///     <c>on:</c>, a kind argument such as <c>count: kill</c>, and any <c>define:</c> between them.
    /// </summary>
    private static IEnumerable<(StatDef Def, CheckedStat Stat)> CorpusStats()
    {
        foreach (string file in ShippedRulesets())
        {
            (RulesetDoc doc, CheckedRuleset ruleset) = Compose(file);
            Dictionary<string, CheckedStat> byId = new(StringComparer.Ordinal);
            foreach (CheckedStat stat in ruleset.Stats)
            {
                byId.TryAdd(stat.StatId, stat);
            }

            foreach (StatDef def in doc.Stats)
            {
                if (byId.TryGetValue(def.Id, out CheckedStat? stat))
                {
                    yield return (def, stat);
                }
            }
        }
    }

    /// <summary>The form for one named stat of one shipped ruleset.</summary>
    private static MatchFacetForm FormFor(string rulesetId, string statId)
    {
        (StatDef def, CheckedStat stat) = CorpusStats()
            .Single(pair => pair.Stat.Ruleset.Id == rulesetId && pair.Def.Id == statId);
        return MatchFacetForm.For(CatalogResource.Load(), stat.ResolvedView, def.Trigger?.Match);
    }

    /// <summary>
    ///     Composes one shipped ruleset with its <c>use:</c> dependencies, one document at a time, with
    ///     a demo-less tick rate and the fallback profile, which is how <c>RenderGraphForOpenFile</c>
    ///     does it.
    /// </summary>
    private static (RulesetDoc Doc, CheckedRuleset Ruleset) Compose(string path)
    {
        RulesetDoc open = RulesetDocumentLoader.Load(File.ReadAllText(path), path).Doc
                          ?? throw new InvalidOperationException($"{path} did not load as a ruleset document");

        List<RulesetDoc> docs = [open];
        Queue<string> pending = new(open.Use);
        HashSet<string> seen = new(StringComparer.Ordinal)
        {
            open.Id
        };
        while (pending.Count > 0)
        {
            string id = pending.Dequeue();
            string dependency = Path.Combine(Path.GetDirectoryName(path)!, id + ".rules.yaml");
            if (!seen.Add(id) || !File.Exists(dependency)
                || RulesetDocumentLoader.Load(File.ReadAllText(dependency), dependency).Doc is not { } doc)
            {
                continue;
            }

            docs.Add(doc);
            foreach (string next in doc.Use)
            {
                pending.Enqueue(next);
            }
        }

        RulesetComposition.Result composed = RulesetComposition.Compose(docs,
            CatalogScopeAdapter.From(CatalogResource.Load()), 64.0,
            DemoSourceProfileRegistry.DefaultFallback.GetType().Name);

        return (open, composed.Rulesets.FirstOrDefault(r => r.Id.Id == open.Id)
                      ?? throw new InvalidOperationException(
                          $"{Path.GetFileName(path)} composed to nothing: "
                          + string.Join("; ", composed.Diagnostics.Select(d => d.ToString()))));
    }

    private static IEnumerable<string> ShippedRulesets() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "rules"), "*.rules.yaml")
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
