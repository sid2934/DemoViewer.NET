#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.RuleAuthoring;

#endregion

namespace DemoViewer.NET.RuleAuthoring.Tests;

/// <summary>
///     Adding and removing whole entries, which is what the node editor does when a node is created
///     or deleted. Byte preservation is only half the gate: an edit also has to leave a document the
///     engine still loads, so these run the real <see cref="RulesetDocumentLoader" /> over the
///     result rather than only re-parsing the YAML.
/// </summary>
public class RulesetStructuralEditTests
{
    /// <summary>Every shipped ruleset, as (name, text) so a failure names the file.</summary>
    public static IEnumerable<Func<(string Name, string Text)>> ShippedRulesets()
    {
        foreach (string path in RuleCorpus.ShippedFiles())
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            yield return () => (name, text);
        }
    }

    /// <summary>
    ///     Add a stat and remove it again, and the file is back to the byte. The strongest
    ///     structural property there is: it pins the insertion point, the indentation, the line
    ///     ending and the deletion extent all at once, and none of them can be off by a character
    ///     without this failing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task AddingAStatAndRemovingIt_IsByteIdentical((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        if (editor.StatIds.Count == 0)
        {
            return;
        }

        RulesetDocumentEditor added = editor.AddStat(
            "editor_probe:\n  count: kill\n  per: round\n  label: Probe\n");

        await Assert.That(added.StatIds).Contains("editor_probe").Because(file.Name);

        RulesetDocumentEditor removed = added.RemoveStat("editor_probe");

        await Assert.That(removed.Text).IsEqualTo(file.Text)
            .Because($"{file.Name}: add-then-remove did not return the original bytes");
    }

    /// <summary>A file the editor has written is a file the engine still loads.</summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task AfterAddingAStat_TheEngineStillLoadsIt((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        if (editor.StatIds.Count == 0)
        {
            return;
        }

        int before = LoadedStatCount(file.Text, file.Name);

        RulesetDocumentEditor added = editor.AddStat(
            "editor_probe:\n  count: kill\n  per: round\n  label: Probe\n");

        await Assert.That(LoadedStatCount(added.Text, file.Name)).IsEqualTo(before + 1)
            .Because($"{file.Name}: the engine did not see the added stat");
    }

    /// <summary>
    ///     Removing a stat takes its body and the comments that describe it, and leaves every other
    ///     stat in place and in order.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task RemovingAStat_LeavesTheRestInOrder((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        if (editor.StatIds.Count < 2)
        {
            return;
        }

        // The middle one, so the test covers a removal with a sibling on each side.
        string victim = editor.StatIds[editor.StatIds.Count / 2];
        List<string> expected = [.. editor.StatIds.Where(id => id != victim)];

        RulesetDocumentEditor removed = editor.RemoveStat(victim);

        await Assert.That(string.Join(",", removed.StatIds)).IsEqualTo(string.Join(",", expected))
            .Because($"{file.Name}: removing {victim} disturbed its siblings");
        await Assert.That(LoadedStatCount(removed.Text, file.Name))
            .IsEqualTo(LoadedStatCount(file.Text, file.Name) - 1)
            .Because($"{file.Name}: the engine does not agree that exactly one stat went");
    }

    /// <summary>
    ///     A trailing comment on the edited line survives, which is the case the whole
    ///     span-precision argument turns on: the corpus writes
    ///     <c>count: kill                       # kill view: …</c> and the comment is not part of
    ///     the scalar.
    /// </summary>
    [Test]
    public async Task EditingAValueWithATrailingComment_KeepsTheComment()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              # what this counts
                              kills:
                                count: kill          # the kill view, actor = killer
                                per: round
                            """;

        RulesetDocumentEditor edited = RulesetDocumentEditor.Open(Yaml)
            .SetStatField("kills", "count", "death");

        await Assert.That(edited.Text).Contains("# the kill view, actor = killer");
        await Assert.That(edited.Text).Contains("# what this counts");
        await Assert.That(edited.Text).Contains("count: death          #");
    }

    /// <summary>
    ///     A CRLF document stays CRLF. The repository normalises to LF, but a user's own ruleset
    ///     under <c>%AppData%</c> came off their disk and an editor that silently converted the
    ///     whole file would show as a full-file diff in their version control.
    /// </summary>
    [Test]
    public async Task ACrlfDocument_StaysCrlf()
    {
        string yaml = "ruleset: probe\r\nfor: match\r\nstats:\r\n  kills:\r\n    count: kill\r\n";

        RulesetDocumentEditor edited = RulesetDocumentEditor.Open(yaml)
            .SetStatField("kills", "label", "Kills");

        await Assert.That(edited.Text.Replace("\r\n", "", StringComparison.Ordinal))
            .DoesNotContain("\n").Because("a lone LF means the editor mixed line endings in");
        await Assert.That(edited.Text).Contains("    label: Kills\r\n");
    }

    /// <summary>
    ///     A file that does not end in a newline still gets its new entry on its own line. The
    ///     offset after the last line IS the end of such a document, so an insert there would land
    ///     on the last line instead: <c>count: kill</c> silently becoming
    ///     <c>count: kill  label: X</c>, which parses as one scalar. A corrupted file rather than an
    ///     error, which is why it is worth its own test.
    /// </summary>
    [Test]
    public async Task ADocumentWithNoTrailingNewline_StillGetsItsEntryOnItsOwnLine()
    {
        string yaml = "ruleset: probe\nfor: match\nstats:\n  kills:\n    count: kill";

        RulesetDocumentEditor edited = RulesetDocumentEditor.Open(yaml)
            .SetStatField("kills", "label", "Kills");

        await Assert.That(edited.Text).Contains("    count: kill\n    label: Kills");
        await Assert.That(LoadedStatCount(edited.Text, "probe")).IsEqualTo(1)
            .Because("the edited document has to still load");
    }

    /// <summary>The same for a whole added entry, which takes a different path through the code.</summary>
    [Test]
    public async Task AddingAStatToADocumentWithNoTrailingNewline_StartsANewLine()
    {
        string yaml = "ruleset: probe\nfor: match\nstats:\n  kills:\n    count: kill";

        RulesetDocumentEditor edited = RulesetDocumentEditor.Open(yaml)
            .AddStat("deaths:\n  count: death\n");

        await Assert.That(edited.Text).Contains("    count: kill\n  deaths:");
        await Assert.That(LoadedStatCount(edited.Text, "probe")).IsEqualTo(2);
    }

    /// <summary>Appending to a flow sequence stays on its line, which is how the corpus writes <c>exports:</c>.</summary>
    [Test]
    public async Task AppendingToAFlowSequence_StaysInline()
    {
        const string Yaml = """
                            ruleset: probe
                            exports: [ kast_pct ]     # the one stat other rulesets read
                            for: match
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        string after = TextEditor.Apply(Yaml,
            [YamlEdits.AddSequenceItem(doc, YamlPath.Root.Key("exports"), "adr")]);

        await Assert.That(after).Contains("exports: [ kast_pct, adr ]     # the one stat");
    }

    private static int LoadedStatCount(string yaml, string name)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, name);
        if (outcome.Doc is not { } doc)
        {
            throw new InvalidOperationException(
                $"{name}: the edited document does not load: "
                + string.Join("; ", outcome.Diagnostics.Select(d => d.ToString())));
        }

        return doc.Stats.Count;
    }
}
