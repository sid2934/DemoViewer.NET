#region

using DemoViewer.NET.RuleAuthoring;

#endregion

namespace DemoViewer.NET.RuleAuthoring.Tests;

/// <summary>
///     The splice primitive. Everything above it addresses the ORIGINAL text, so the one thing this
///     has to get right is applying a batch without any edit having to know what the others did.
/// </summary>
public class TextEditorTests
{
    [Test]
    public async Task EditsAreAddressedAgainstTheOriginal_WhateverOrderTheyArriveIn()
    {
        const string Source = "one two three";

        string forwards = TextEditor.Apply(Source,
        [
            new TextEdit(0, 3, "ONE", "a"),
            new TextEdit(8, 5, "THREE", "c")
        ]);
        string backwards = TextEditor.Apply(Source,
        [
            new TextEdit(8, 5, "THREE", "c"),
            new TextEdit(0, 3, "ONE", "a")
        ]);

        await Assert.That(forwards).IsEqualTo("ONE two THREE");
        await Assert.That(backwards).IsEqualTo(forwards)
            .Because("the batch is order-independent by construction");
    }

    [Test]
    public async Task ReplacementsOfDifferentLengthsDoNotShiftEachOther()
    {
        string result = TextEditor.Apply("aaa bbb ccc",
        [
            new TextEdit(0, 3, "a", "shrink"),
            new TextEdit(4, 3, "bbbbbbbb", "grow"),
            new TextEdit(8, 3, "ccc", "same")
        ]);

        await Assert.That(result).IsEqualTo("a bbbbbbbb ccc");
    }

    /// <summary>
    ///     Two operations fighting over one span is a caller bug. Resolving it silently is how an
    ///     editor corrupts a file, so it throws.
    /// </summary>
    [Test]
    public async Task OverlappingEditsAreRejected()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => TextEditor.Apply("abcdef",
            [
                new TextEdit(0, 4, "x", "first"),
                new TextEdit(2, 2, "y", "second")
            ]));

        await Assert.That(thrown.Message).Contains("overlapping edits");
    }

    [Test]
    public async Task TwoInsertionsAtOnePointAreNotAnOverlap()
    {
        string result = TextEditor.Apply("ab",
        [
            new TextEdit(1, 0, "X", "first"),
            new TextEdit(1, 0, "Y", "second")
        ]);

        await Assert.That(result).IsEqualTo("aXYb");
    }

    [Test]
    public async Task AnEditPastTheEndIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TextEditor.Apply("abc", [new TextEdit(2, 5, "x", "too long")]));
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnEmptyBatchReturnsTheSourceUnchanged()
    {
        const string Source = "unchanged";

        await Assert.That(TextEditor.Apply(Source, [])).IsEqualTo(Source);
    }

    /// <summary>
    ///     A collection's own end mark is not usable, so the span is derived. Checked on a flow
    ///     mapping, a flow sequence and a block mapping, which is every collection style the shipped
    ///     corpus writes.
    /// </summary>
    [Test]
    public async Task CollectionSpansCoverTheWholeCollection()
    {
        const string Yaml = """
                            ruleset: probe
                            exports: [ a, b ]
                            stats:
                              kills:
                                count: kill
                                match: { enemy: true, bullet: true }
                            """;

        YamlDocumentText doc = YamlDocumentText.Parse(Yaml);

        await Assert.That(Slice(doc, YamlPath.Root.Key("exports"))).IsEqualTo("[ a, b ]");
        await Assert.That(Slice(doc,
                YamlPath.Root.Key("stats").Key("kills").Key("match")))
            .IsEqualTo("{ enemy: true, bullet: true }");
        await Assert.That(Slice(doc, YamlPath.Root.Key("stats").Key("kills")))
            .IsEqualTo("count: kill\n    match: { enemy: true, bullet: true }");
    }

    private static string Slice(YamlDocumentText doc, YamlPath path)
    {
        (int start, int length) = doc.SpanOf(doc.Find(path)!);
        return doc.Text.Substring(start, length);
    }
}
