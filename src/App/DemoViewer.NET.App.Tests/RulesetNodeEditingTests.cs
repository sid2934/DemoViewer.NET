#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Modules.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The node editor's write path (docs/rule-graph/design.md §6.4 item 4). Pure, so no window.
///     <para>
///         The property every one of these holds is that an edit made by clicking a node produces
///         the same bytes as the same edit typed into the text pane, because both go through the one
///         splicing writer. §9 decision 4 is only worth anything if that stays true, and a second
///         write path is the obvious way to lose it.
///     </para>
/// </summary>
public class RulesetNodeEditingTests
{
    private const string Sample = """
                                  # the header comment, which has to survive everything below
                                  ruleset: probe
                                  title: Probe
                                  for: each_player

                                  stats:
                                    # a section header, describing the group rather than the stat
                                    kills:
                                      count: kill          # a trailing comment on an edited line
                                      per: round
                                      label: Kills
                                    deaths:
                                      count: death
                                      per: round

                                  highlights:
                                    big_round:
                                      when: kills >= 3
                                      title: "{player.name} popped off"
                                  """;

    /// <summary>Every shipped ruleset, so the binding join is measured rather than assumed.</summary>
    public static IEnumerable<Func<(string Name, string Text)>> ShippedRulesets()
    {
        string dir = Path.Combine(RepoRoot(), "rules");
        foreach (string path in Directory.GetFiles(dir, "*.rules.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            yield return () => (name, text);
        }
    }

    /// <summary>
    ///     Every stat the open document declares binds to a node name, across the whole shipped
    ///     corpus. That is the join the editor stands on: <c>AuthoringGraphNode</c> carries no
    ///     back-reference to the YAML (§6.1), so a node is addressable only because a stat's node is
    ///     named exactly its id.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task EveryStatAndHighlightBinds((string Name, string Text) file)
    {
        RulesetDoc? doc = RulesetDocumentLoader.Load(file.Text, file.Name).Doc;
        if (doc is null)
        {
            return;
        }

        IReadOnlyDictionary<string, RulesetNodeBinding> map = RulesetNodeBinding.Map(doc);

        foreach (StatDef stat in doc.Stats)
        {
            await Assert.That(map.TryGetValue(stat.Id, out RulesetNodeBinding binding)).IsTrue()
                .Because($"{file.Name}: stat {stat.Id} should bind");
            await Assert.That(binding.Kind).IsEqualTo(RulesetNodeKind.Stat);
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            await Assert.That(map.ContainsKey("_chain_" + highlight.Id)).IsTrue()
                .Because($"{file.Name}: highlight {highlight.Id}'s chain node should bind");
        }
    }

    /// <summary>A node nothing declares is not editable, and the editor is told so rather than guessing.</summary>
    [Test]
    public async Task ScaffoldingDoesNotBind()
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(Sample, "probe").Doc!;
        IReadOnlyDictionary<string, RulesetNodeBinding> map = RulesetNodeBinding.Map(doc);

        foreach (string name in new[] { "Root", "MatchLive", "RoundActive", "enrich.kill.was_enemy_kill", "rounds_3k" })
        {
            await Assert.That(map.ContainsKey(name)).IsFalse().Because($"{name} is not declared here");
        }

        await Assert.That(RulesetNodeBinding.None.IsEditable).IsFalse();
    }

    /// <summary>Editing a field keeps every comment, including the one on the line it rewrites.</summary>
    [Test]
    public async Task SettingAField_KeepsTheComments()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        string after = RulesetNodeEditing.SetField(Sample, kills, "label", "Frags");

        await Assert.That(after).Contains("label: Frags");
        await Assert.That(after).Contains("# the header comment, which has to survive everything below");
        await Assert.That(after).Contains("# a section header, describing the group rather than the stat");
        await Assert.That(after).Contains("count: kill          # a trailing comment on an edited line");
    }

    /// <summary>A key the stat does not have is added; the document still loads and the value reads back.</summary>
    [Test]
    public async Task SettingAnAbsentField_AddsIt()
    {
        RulesetNodeBinding deaths = new(RulesetNodeKind.Stat, "deaths");

        string after = RulesetNodeEditing.SetField(Sample, deaths, "label", "Deaths");

        RulesetDoc doc = RulesetDocumentLoader.Load(after, "probe").Doc!;
        await Assert.That(doc.Stats.Single(s => s.Id == "deaths").Label).IsEqualTo("Deaths");
        await Assert.That(doc.Stats.Count).IsEqualTo(2).Because("adding a key is not adding a stat");
    }

    /// <summary>
    ///     A blank value removes the key rather than writing an empty one. There is no other gesture
    ///     for taking an optional key back off, and <c>label: ""</c> is a different document from no
    ///     label at all.
    /// </summary>
    [Test]
    public async Task BlankingAField_RemovesTheKey()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        string after = RulesetNodeEditing.SetField(Sample, kills, "label", "   ");

        await Assert.That(after).DoesNotContain("label: Kills");
        RulesetDoc doc = RulesetDocumentLoader.Load(after, "probe").Doc!;
        await Assert.That(doc.Stats.Single(s => s.Id == "kills").Label).IsNull();
    }

    /// <summary>Blanking a key that is already absent changes nothing at all.</summary>
    [Test]
    public async Task BlankingAnAbsentField_IsANoOp()
    {
        RulesetNodeBinding deaths = new(RulesetNodeKind.Stat, "deaths");

        await Assert.That(RulesetNodeEditing.SetField(Sample, deaths, "label", "")).IsEqualTo(Sample);
    }

    /// <summary>Add then delete returns the original bytes, which pins the insertion point and the extent together.</summary>
    [Test]
    public async Task AddingAStatAndDeletingIt_IsByteIdentical()
    {
        (string added, string id) = RulesetNodeEditing.AddStat(Sample);

        await Assert.That(id).IsEqualTo("new_stat");
        await Assert.That(RulesetDocumentLoader.Load(added, "probe").Doc!.Stats.Count).IsEqualTo(3);

        string back = RulesetNodeEditing.Delete(added, new RulesetNodeBinding(RulesetNodeKind.Stat, id));

        await Assert.That(back).IsEqualTo(Sample);
    }

    /// <summary>
    ///     A second add does not collide with the first. Stats and highlights share one id
    ///     namespace, so the check has to clear both.
    /// </summary>
    [Test]
    public async Task AddingTwice_PicksAFreeId()
    {
        (string once, string first) = RulesetNodeEditing.AddStat(Sample);
        (string twice, string second) = RulesetNodeEditing.AddStat(once);

        await Assert.That(first).IsEqualTo("new_stat");
        await Assert.That(second).IsEqualTo("new_stat_2");
        await Assert.That(RulesetDocumentLoader.Load(twice, "probe").Doc!.Stats.Count).IsEqualTo(4);
    }

    /// <summary>A new stat is one the resolver accepts, not a placeholder that fails the check.</summary>
    [Test]
    public async Task AnAddedStat_Loads()
    {
        (string added, string id) = RulesetNodeEditing.AddStat(Sample);

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(added, "probe");

        await Assert.That(outcome.Doc).IsNotNull();
        await Assert.That(outcome.Doc!.Stats.Any(s => s.Id == id)).IsTrue();
    }

    /// <summary>Deleting a highlight takes the highlight and leaves the stats alone.</summary>
    [Test]
    public async Task DeletingAHighlight_LeavesTheStats()
    {
        string after = RulesetNodeEditing.Delete(Sample,
            new RulesetNodeBinding(RulesetNodeKind.Highlight, "big_round"));

        RulesetDoc doc = RulesetDocumentLoader.Load(after, "probe").Doc!;
        await Assert.That(doc.Highlights).IsEmpty();
        await Assert.That(doc.Stats.Count).IsEqualTo(2);
    }

    /// <summary>Reading the fields of a node reports both what is there and what is not.</summary>
    [Test]
    public async Task ReadFields_ReportsAbsentKeysAsAbsent()
    {
        IReadOnlyList<RulesetNodeField> fields =
            RulesetNodeEditing.ReadFields(Sample, new RulesetNodeBinding(RulesetNodeKind.Stat, "kills"));

        RulesetNodeField label = fields.Single(f => f.Key == "label");
        RulesetNodeField where = fields.Single(f => f.Key == "where");

        await Assert.That(label.IsPresent).IsTrue();
        await Assert.That(label.Value).IsEqualTo("Kills");
        await Assert.That(where.IsPresent).IsFalse();
        await Assert.That(where.Value).IsEqualTo("");
    }

    /// <summary>An edit aimed at a node nothing declares is refused, not attempted.</summary>
    [Test]
    public async Task EditingSomethingThatDoesNotBind_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => RulesetNodeEditing.SetField(Sample, RulesetNodeBinding.None, "label", "x"));
        Assert.Throws<InvalidOperationException>(
            () => RulesetNodeEditing.Delete(Sample, RulesetNodeBinding.None));
        await Assert.That(RulesetNodeEditing.ReadFields(Sample, RulesetNodeBinding.None)).IsEmpty();
    }

    /// <summary>
    ///     A value an author could type that would change what the document MEANS if written plain.
    ///     The writer's quoting rules carry this, and the editing layer is a new way to reach them,
    ///     which is the whole reason it is re-tested here rather than assumed.
    /// </summary>
    [Test]
    public async Task AFieldValueThatLooksLikeABool_StaysAString()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        string after = RulesetNodeEditing.SetField(Sample, kills, "label", "true");

        await Assert.That(after).Contains("label: \"true\"");
        await Assert.That(RulesetDocumentLoader.Load(after, "probe").Doc!
            .Stats.Single(s => s.Id == "kills").Label).IsEqualTo("true");
    }

    /// <summary>The same for an expression with a colon in it, which a plain scalar cannot hold.</summary>
    [Test]
    public async Task AFieldValueWithAColon_IsQuoted()
    {
        RulesetNodeBinding kills = new(RulesetNodeKind.Stat, "kills");

        string after = RulesetNodeEditing.SetField(Sample, kills, "where", "event.Weapon == \"ak47\": true");

        RulesetDoc doc = RulesetDocumentLoader.Load(after, "probe").Doc!;
        await Assert.That(doc.Stats.Single(s => s.Id == "kills").Trigger?.Where)
            .IsEqualTo("event.Weapon == \"ak47\": true");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "rules"))
                && Directory.GetFiles(Path.Combine(dir.FullName, "rules"), "*.rules.yaml").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"no rules/ above {AppContext.BaseDirectory}");
    }
}
