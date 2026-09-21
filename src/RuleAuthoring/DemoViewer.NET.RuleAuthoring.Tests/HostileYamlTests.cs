#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.RuleAuthoring;

#endregion

namespace DemoViewer.NET.RuleAuthoring.Tests;

/// <summary>
///     The constructs the shipped corpus does NOT contain, which is exactly why they need their own
///     tests: the round-trip suite reads <c>rules/</c>, so anything absent from those fifteen files
///     is unguarded by it. A user's own ruleset under <c>%AppData%</c> is under no such restraint.
///     <para>
///         The rule these enforce is that this writer never produces a file that still parses and no
///         longer means what it did. Where a splice can do the job it does it; where it cannot, the
///         operation is REFUSED. A caller can show a refusal to the user. A silently changed
///         document is discovered later, by someone whose analysis has quietly gone wrong.
///     </para>
/// </summary>
public class HostileYamlTests
{
    // ── Block scalars: handled, not refused, except when their own text is edited ──────────

    /// <summary>
    ///     A literal scalar's end mark sits past its terminating line break, at the START of the
    ///     next line. Believed as-is, an insert after it lands inside the NEXT stat, and the
    ///     document still parses.
    /// </summary>
    [Test]
    public async Task AKeyAddedAfterABlockScalar_LandsInTheRightStat()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              a:
                                note: |
                                  hello
                              b:
                                per: round
                            """;

        RulesetDocumentEditor edited = RulesetDocumentEditor.Open(Yaml)
            .SetStatField("a", "label", "A");

        await Assert.That(edited.Scalar(RulesetDocumentEditor.StatPath("a").Key("label")))
            .IsEqualTo("A").Because("the key belongs to stat a");
        await Assert.That(edited.Scalar(RulesetDocumentEditor.StatPath("b").Key("label")))
            .IsNull().Because("and NOT to stat b");
    }

    /// <summary>Removing a block-scalar entry takes that entry and not the line after it.</summary>
    [Test]
    public async Task RemovingABlockScalarKey_DoesNotTakeTheNextLine()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              a:
                                note: |
                                  hello
                                label: A
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        string after = TextEditor.Apply(Yaml,
            [YamlEdits.RemoveKey(doc, RulesetDocumentEditor.StatPath("a"), "note")]);

        await Assert.That(after).DoesNotContain("note:");
        await Assert.That(after).Contains("label: A")
            .Because("the line after a block scalar is not part of it");
    }

    /// <summary>
    ///     Editing the block scalar's own text is refused rather than silently reformatted into a
    ///     quoted string, which would keep the value and lose the shape the author chose.
    /// </summary>
    [Test]
    public async Task EditingABlockScalarItself_IsRefused()
    {
        const string Yaml = """
                            ruleset: probe
                            summary: |
                              one
                              two
                            for: match
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        NotSupportedException thrown = Assert.Throws<NotSupportedException>(
            () => YamlEdits.SetScalar(doc, YamlPath.Root.Key("summary"), "replaced"));

        await Assert.That(thrown.Message).Contains("block scalar");
    }

    // ── Anchors, aliases, tags and merge keys: refused ────────────────────────────────────

    [Test]
    public async Task EditingAnAnchoredValue_IsRefused()
    {
        const string Yaml = "ruleset: probe\nfor: &scope match\n";

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        NotSupportedException thrown = Assert.Throws<NotSupportedException>(
            () => YamlEdits.SetScalar(doc, YamlPath.Root.Key("for"), "each_player"));

        await Assert.That(thrown.Message).Contains("anchor");
    }

    /// <summary>
    ///     An alias resolves to the anchored node, so the representation model hands back a span
    ///     pointing at the DEFINITION. Editing through an alias would rewrite the definition.
    /// </summary>
    [Test]
    public async Task EditingThroughAnAlias_IsRefused()
    {
        const string Yaml = "ruleset: probe\na: &anc hello\nb: *anc\n";

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        NotSupportedException thrown = Assert.Throws<NotSupportedException>(
            () => YamlEdits.SetScalar(doc, YamlPath.Root.Key("b"), "goodbye"));

        await Assert.That(thrown.Message).Contains("anchor");
    }

    [Test]
    public async Task EditingAnExplicitlyTaggedValue_IsRefused()
    {
        const string Yaml = "ruleset: probe\ncount: !!str 5\n";

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        NotSupportedException thrown = Assert.Throws<NotSupportedException>(
            () => YamlEdits.SetScalar(doc, YamlPath.Root.Key("count"), "6"));

        await Assert.That(thrown.Message).Contains("tag");
    }

    [Test]
    public async Task AppendingAfterAnAliasedSequenceItem_IsRefused()
    {
        const string Yaml = "ruleset: probe\nd: &d one\nlist:\n  - plain\n  - *d\n";

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);

        Assert.Throws<NotSupportedException>(
            () => YamlEdits.AddSequenceItem(doc, YamlPath.Root.Key("list"), "two"));
        await Task.CompletedTask;
    }

    // ── Quoting: a value that would parse as something else ───────────────────────────────

    /// <summary>
    ///     A label is a string in the ruleset schema. Writing <c>true</c> into it plain would make
    ///     it a boolean, and the file would fail the schema its own modeline points at.
    /// </summary>
    [Test]
    public async Task SettingAStringFieldToALiteralThatLooksLikeABool_IsQuoted()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              kills:
                                count: kill
                                label: Kills
                            """;

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(Yaml);

        await Assert.That(editor.SetStatField("kills", "label", "true").Text)
            .Contains("label: \"true\"");
        await Assert.That(editor.SetStatField("kills", "label", "12").Text)
            .Contains("label: \"12\"");
        await Assert.That(editor.SetStatField("kills", "label", "null").Text)
            .Contains("label: \"null\"");
    }

    /// <summary>
    ///     And the other way: a slot that already holds a boolean keeps holding one. The value being
    ///     replaced is the evidence for what the slot is for.
    /// </summary>
    [Test]
    public async Task SettingABooleanFieldToAnotherBoolean_StaysPlain()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              kills:
                                count: kill
                                match: { bullet: true }
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        string after = TextEditor.Apply(Yaml,
        [
            YamlEdits.SetScalar(doc,
                RulesetDocumentEditor.StatPath("kills").Key("match").Key("bullet"), "false")
        ]);

        await Assert.That(after).Contains("match: { bullet: false }");
    }

    /// <summary>
    ///     A comma is ordinary text in a block context and STRUCTURE inside a flow collection. The
    ///     corpus writes 95 flow mappings, so this is the live case: written plain, one value would
    ///     silently become several entries.
    /// </summary>
    [Test]
    public async Task AValueWithACommaWrittenIntoAFlowMapping_IsQuoted()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              kills:
                                match: { weapon: awp, bullet: true }
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        string after = TextEditor.Apply(Yaml,
        [
            YamlEdits.SetScalar(doc,
                RulesetDocumentEditor.StatPath("kills").Key("match").Key("weapon"), "awp, ssg08")
        ]);

        await Assert.That(after).Contains("{ weapon: \"awp, ssg08\", bullet: true }");
        YamlDocumentText reparsed = YamlDocumentText.Parse(after);
        await Assert.That(reparsed.Find(
                RulesetDocumentEditor.StatPath("kills").Key("match")))
            .IsNotNull();
    }

    /// <summary>The same value in a BLOCK context stays plain, which is how the corpus writes <c>summary:</c>.</summary>
    [Test]
    public async Task AValueWithACommaInABlockContext_StaysPlain()
    {
        const string Yaml = "ruleset: probe\nsummary: one thing\nfor: match\n";

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        string after = TextEditor.Apply(Yaml,
            [YamlEdits.SetScalar(doc, YamlPath.Root.Key("summary"), "per-round stats, per player")]);

        await Assert.That(after).Contains("summary: per-round stats, per player");
    }

    /// <summary>
    ///     A single-quoted scalar cannot hold a line break without folding it to a space, and every
    ///     <c>where:</c> expression in the corpus is single-quoted.
    /// </summary>
    [Test]
    public async Task AMultiLineValueIntoASingleQuotedScalar_ChangesStyleRatherThanFolding()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              kills:
                                where: 'x == 1'
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
        string after = TextEditor.Apply(Yaml,
        [
            YamlEdits.SetScalar(doc,
                RulesetDocumentEditor.StatPath("kills").Key("where"), "x == 1\ny == 2")
        ]);

        await Assert.That(YamlDocumentText.Parse(after)
                .Find(RulesetDocumentEditor.StatPath("kills").Key("where")) is
            YamlDotNet.RepresentationModel.YamlScalarNode { Value: "x == 1\ny == 2" }).IsTrue();
    }

    // ── Emptied sections and duplicate keys ───────────────────────────────────────────────

    /// <summary>
    ///     Removing the last stat leaves <c>stats:</c> holding nothing, which parses as a null
    ///     scalar rather than a mapping. Without handling it, the editor could delete a ruleset's
    ///     last stat and then not be able to add one, with no way back.
    /// </summary>
    [Test]
    public async Task AStatCanBeAddedBackToASectionEmptiedByRemoval()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              kills:
                                count: kill
                            """;

        RulesetDocumentEditor emptied = RulesetDocumentEditor.Open(Yaml).RemoveStat("kills");

        await Assert.That(emptied.StatIds).IsEmpty();

        RulesetDocumentEditor refilled = emptied.AddStat("deaths:\n  count: death\n");

        await Assert.That(string.Join(",", refilled.StatIds)).IsEqualTo("deaths");
        await Assert.That(RulesetDocumentLoader.Load(refilled.Text, "probe").Doc!.Stats.Count)
            .IsEqualTo(1).Because("the engine has to load it too");
    }

    /// <summary>
    ///     Setting a key that already holds a collection would append a SECOND entry with that key,
    ///     and YAML rejects the duplicate from a layer that cannot explain why. It is refused where
    ///     the caller can see it instead.
    /// </summary>
    [Test]
    public async Task SettingAKeyThatHoldsACollection_IsRefusedRatherThanDuplicated()
    {
        const string Yaml = "ruleset: probe\nexports: [ a ]\nfor: match\n";

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(Yaml);
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => editor.SetDocumentField("exports", "b"));

        await Assert.That(thrown.Message).Contains("already holds");
    }

    // ── What an editing gesture can hand the writer ────────────────────────────

    /// <summary>
    ///     A node editor's field box takes whatever an author types, so these are the values the
    ///     corpus will never contain and a user can produce in one keystroke. Each is checked by
    ///     re-reading the value back, which is the only question that matters: did the document
    ///     still mean what the author said.
    /// </summary>
    [Test]
    public async Task AValueTypedIntoAFieldBox_ReadsBackAsItself()
    {
        string[] hostile =
        [
            "true", "false", "null", "~", "12", "3.5", "-5", "0x1f", "y", "on", "off",
            "2026-09-20", "", " leading", "trailing ", "a: b", "a # b", "[bracketed]",
            "{braced}", "comma, separated", "quote\"inside", "apostrophe'inside",
            "back\\slash", "tab\there", "#leading-hash", "*star", "&amp;amp", "!bang",
            "very " + new string('x', 400)
        ];

        foreach (string value in hostile)
        {
            const string Yaml = """
                                ruleset: probe
                                for: match
                                stats:
                                  kills:
                                    count: kill
                                    label: Kills
                                """;

            RulesetDocumentEditor edited = RulesetDocumentEditor.Open(Yaml)
                .SetStatField("kills", "label", value);

            await Assert.That(edited.Scalar(RulesetDocumentEditor.StatPath("kills").Key("label")))
                .IsEqualTo(value).Because($"a label of [{value}] should read back as itself");
            await Assert.That(RulesetDocumentLoader.Load(edited.Text, "probe").Doc).IsNotNull()
                .Because($"a label of [{value}] should leave a loadable document");
        }
    }

    /// <summary>
    ///     The same inside a FLOW mapping, where a comma or a brace is structure rather than text.
    ///     This is the live case: the corpus writes 95 of them as <c>match: { … }</c>.
    /// </summary>
    [Test]
    public async Task AValueTypedIntoAFlowMapping_ReadsBackAsItself()
    {
        foreach (string value in new[] { "comma, separated", "{braced}", "[bracketed]", "a: b", "true", "42" })
        {
            const string Yaml = """
                                ruleset: probe
                                for: match
                                stats:
                                  kills:
                                    count: kill
                                    match: { weapon: awp, bullet: true }
                                """;

            YamlDocumentText doc = YamlDocumentText.Parse(Yaml);
            string after = TextEditor.Apply(Yaml,
            [
                YamlEdits.SetScalar(doc,
                    RulesetDocumentEditor.StatPath("kills").Key("match").Key("weapon"), value)
            ]);

            YamlDocumentText reparsed = YamlDocumentText.Parse(after);
            await Assert.That((reparsed.Find(
                    RulesetDocumentEditor.StatPath("kills").Key("match").Key("weapon"))
                as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value)
                .IsEqualTo(value).Because($"[{value}] in a flow mapping should read back as itself");
            await Assert.That((reparsed.Find(
                    RulesetDocumentEditor.StatPath("kills").Key("match").Key("bullet"))
                as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value)
                .IsEqualTo("true").Because($"[{value}] should not have eaten its sibling");
        }
    }

    /// <summary>
    ///     An id an author could type for a new stat. The WRITER does not validate ids, and it
    ///     should not: a malformed one has to reach the checker, which is what reports it. What it
    ///     must not do is produce a document that parses as something else.
    /// </summary>
    [Test]
    public async Task AnAddedEntryWithAnAwkwardId_StillParsesAsOneEntry()
    {
        foreach (string id in new[] { "with_underscore", "with-hyphen", "CamelCase", "n123" })
        {
            const string Yaml = """
                                ruleset: probe
                                for: match
                                stats:
                                  kills:
                                    count: kill
                                """;

            RulesetDocumentEditor added = RulesetDocumentEditor.Open(Yaml)
                .AddStat($"{id}:\n  count: death\n");

            await Assert.That(added.StatIds.Count).IsEqualTo(2).Because($"[{id}] should add one stat");
            await Assert.That(added.StatIds).Contains(id);
        }
    }

    // ── Batch ordering ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Thirty inserts at one offset come back in the order they were submitted. The sort used to
    ///     be <c>List&lt;T&gt;.Sort</c>, which is introsort and unstable above sixteen elements, so
    ///     a batch adding many entries at one point produced a different document each run.
    /// </summary>
    [Test]
    public async Task ManyInsertionsAtOneOffset_KeepTheirSubmittedOrder()
    {
        List<TextEdit> edits = [];
        for (int i = 0; i < 30; i++)
        {
            edits.Add(new TextEdit(1, 0, ((char)('a' + i % 26)).ToString(), $"insert {i}"));
        }

        string result = TextEditor.Apply("[]", edits);

        await Assert.That(result).IsEqualTo("[" + string.Concat(edits.Select(e => e.Replacement)) + "]");
    }

    /// <summary>A blank line inside an added block survives, because the author put it there.</summary>
    [Test]
    public async Task ABlankLineInsideAnAddedBlock_Survives()
    {
        const string Yaml = """
                            ruleset: probe
                            for: match
                            stats:
                              kills:
                                count: kill
                            """;

        RulesetDocumentEditor edited = RulesetDocumentEditor.Open(Yaml)
            .AddStat("deaths:\n  count: death\n\nassists:\n  count: assist\n");

        await Assert.That(edited.Text).Contains("count: death\n\n  assists:");
        await Assert.That(string.Join(",", edited.StatIds)).IsEqualTo("kills,deaths,assists");
    }
}
