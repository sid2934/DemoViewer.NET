#region

using DemoViewer.NET.RuleAuthoring;

#endregion

namespace DemoViewer.NET.RuleAuthoring.Tests;

/// <summary>
///     The gate on the whole node editor (design.md §9 decision 4, §6.1). A ruleset is hand-authored
///     and its comments carry the reasoning, so the editor may only be built on a writer that keeps
///     them. These tests are what "keeps them" means, stated so it can fail.
/// </summary>
public class RulesetRoundTripTests
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
    ///     Open and save with no edit, and the bytes are the ones that were there. This is the
    ///     weakest possible statement of the round trip and the one a regenerating writer fails
    ///     outright, so it is worth its own test.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task OpenAndSave_IsByteIdentical((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);

        await Assert.That(editor.Text).IsEqualTo(file.Text)
            .Because($"{file.Name}: an unedited save must not change one byte");
    }

    /// <summary>
    ///     A no-op edit batch is also byte-identical, so the round trip survives the edit machinery
    ///     rather than only the parse.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task ApplyingNoEdits_IsByteIdentical((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text).Apply([]);

        await Assert.That(editor.Text).IsEqualTo(file.Text).Because(file.Name);
    }

    /// <summary>
    ///     Rewrite EVERY scalar in the file with the value it already has, and the bytes come back
    ///     unchanged. This is the sharpest statement of the round trip in the suite: it exercises
    ///     every scalar span and every quoting style the corpus contains, so a span that ran one
    ///     character wide, or a re-quote that reflowed a plain expression, fails here rather than
    ///     in front of a user.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task RewritingEveryScalarWithItsOwnValue_IsByteIdentical((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);

        List<TextEdit> edits = [];
        foreach (YamlPath path in ScalarPaths(editor.Document, YamlPath.Root))
        {
            edits.Add(YamlEdits.SetScalar(editor.Document, path, editor.Scalar(path)!));
        }

        RulesetDocumentEditor rewritten = editor.Apply(edits);

        await Assert.That(edits.Count).IsGreaterThan(5)
            .Because($"{file.Name}: the smallest shipped ruleset has 17 scalars; the walk is broken");
        await Assert.That(rewritten.Text).IsEqualTo(file.Text)
            .Because($"{file.Name}: rewriting {edits.Count} scalars with their own values changed bytes");
    }

    /// <summary>
    ///     Every value in the file reads back as what it was. Byte equality above already implies
    ///     it, but stated separately because this is the property a caller depends on and the one
    ///     that would survive a future change to how the document is stored.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task ReparsingAfterAnEdit_KeepsEveryOtherValue((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        List<YamlPath> paths = [.. ScalarPaths(editor.Document, YamlPath.Root)];
        if (editor.StatIds.Count == 0)
        {
            return;
        }

        YamlPath edited = RulesetDocumentEditor.StatPath(editor.StatIds[0]).Key("label");
        RulesetDocumentEditor after = editor.SetStatField(editor.StatIds[0], "label", "Edited");

        foreach (YamlPath path in paths)
        {
            if (path.ToString() == edited.ToString())
            {
                continue;
            }

            await Assert.That(after.Scalar(path)).IsEqualTo(editor.Scalar(path))
                .Because($"{file.Name}: {path} changed and should not have");
        }

        await Assert.That(after.Scalar(edited)).IsEqualTo("Edited").Because(file.Name);
    }

    /// <summary>
    ///     Every comment line in the file is still there after an edit somewhere else in it. Stated
    ///     as a count rather than a diff so the failure says how many went missing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task EditingOneStat_KeepsEveryCommentLine((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        if (editor.StatIds.Count == 0)
        {
            return;
        }

        RulesetDocumentEditor edited =
            editor.SetStatField(editor.StatIds[0], "label", "Renamed By The Editor");

        await Assert.That(CommentLines(edited.Text)).IsEqualTo(CommentLines(file.Text))
            .Because($"{file.Name}: editing one stat's label dropped a comment");
    }

    /// <summary>
    ///     A targeted edit changes ONE line. The second half of the gate: preserving the file is
    ///     worth nothing if an edit rewrites half of it in passing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task EditingOneStat_ChangesExactlyOneLine((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        if (editor.StatIds.Count == 0)
        {
            return;
        }

        string id = editor.StatIds[0];
        RulesetDocumentEditor edited = editor.SetStatField(id, "label", "Renamed By The Editor");

        (int changed, int added, int removed) = LineDelta(file.Text, edited.Text);

        // A stat that already had a label has its line rewritten; one that did not gains a line.
        bool hadLabel = editor.Scalar(RulesetDocumentEditor.StatPath(id).Key("label")) is not null;
        await Assert.That(removed).IsEqualTo(0).Because($"{file.Name}: an edit removed lines");
        await Assert.That(added).IsEqualTo(hadLabel ? 0 : 1).Because(file.Name);
        await Assert.That(changed).IsEqualTo(hadLabel ? 1 : 0)
            .Because($"{file.Name}: expected one changed line, got {changed}");
    }

    /// <summary>Key order is the author's, and an edit does not get to reorder it.</summary>
    [Test]
    [MethodDataSource(nameof(ShippedRulesets))]
    public async Task EditingOneStat_KeepsStatOrder((string Name, string Text) file)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(file.Text);
        if (editor.StatIds.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> before = editor.StatIds;
        RulesetDocumentEditor edited = editor.SetStatField(before[0], "label", "Renamed");

        await Assert.That(string.Join(",", edited.StatIds))
            .IsEqualTo(string.Join(",", before)).Because(file.Name);
    }

    // Every scalar leaf, as a path, depth first in document order.
    private static IEnumerable<YamlPath> ScalarPaths(YamlDocumentText doc, YamlPath at)
    {
        switch (doc.Find(at))
        {
            case YamlDotNet.RepresentationModel.YamlScalarNode:
                yield return at;
                break;

            case YamlDotNet.RepresentationModel.YamlMappingNode map:
                foreach (YamlDotNet.RepresentationModel.YamlNode key in map.Children.Keys)
                {
                    if (key is YamlDotNet.RepresentationModel.YamlScalarNode { Value: { } name })
                    {
                        foreach (YamlPath child in ScalarPaths(doc, at.Key(name)))
                        {
                            yield return child;
                        }
                    }
                }

                break;

            case YamlDotNet.RepresentationModel.YamlSequenceNode sequence:
                for (int i = 0; i < sequence.Children.Count; i++)
                {
                    foreach (YamlPath child in ScalarPaths(doc, at.Index(i)))
                    {
                        yield return child;
                    }
                }

                break;
        }
    }

    private static int CommentLines(string text) =>
        text.Split('\n').Count(line => line.TrimStart().StartsWith('#'));

    // Line-level delta: how many lines were rewritten in place, added, and removed. A simple
    // prefix/suffix trim is enough here because every case under test is one localised change.
    private static (int Changed, int Added, int Removed) LineDelta(string before, string after)
    {
        string[] a = before.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] b = after.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
        {
            head++;
        }

        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head
               && a[a.Length - 1 - tail] == b[b.Length - 1 - tail])
        {
            tail++;
        }

        int aMiddle = a.Length - head - tail;
        int bMiddle = b.Length - head - tail;
        int changed = Math.Min(aMiddle, bMiddle);
        return (changed, Math.Max(0, bMiddle - aMiddle), Math.Max(0, aMiddle - bMiddle));
    }
}
