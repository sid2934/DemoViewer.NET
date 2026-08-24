#region

using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using DemoViewer.NET.Views.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Workbench highlighting is DSL-aware — it colours a token by its ROLE
///     (section / kind / modifier / literal / event / path / user-id), not one flat "key" blue. These gates
///     highlight a real ruleset and assert each token lands in the right colour class.
/// </summary>
/// <remarks>
///     Runs on the UI thread even though nothing here is visual: the highlighting definition is built from
///     XAML and its <c>SolidColorBrush</c>es are <c>AvaloniaObject</c>s, which verify dispatcher access in
///     their constructor. Off-thread these passed only while no dispatch had yet bound the UI thread — a
///     race the assembly warm-up now settles, and the "Call from invalid thread" half of issue #6.
/// </remarks>
public class WorkbenchHighlightingTests
{
    private const string Sample =
        "ruleset: demo\n" +
        "for: each_player\n" +
        "stats:\n" +
        "  kills:\n" +
        "    count: kill\n" +
        "    per: round\n" +
        "    while: round.active\n" +
        "    when: enemy\n";

    [Test]
    public async Task Definition_Loads_WithoutThrowing() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            IHighlightingDefinition def = WorkbenchYamlHighlighting.Definition;
            await Assert.That(def).IsNotNull();
            await Assert.That(def.Name).IsEqualTo("YAML-RulesV2");
        });

    [Test]
    public async Task Tokens_AreColouredByRole() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Dictionary<string, string> colours = ColourMap(Sample);

            await Assert.That(colours.GetValueOrDefault("ruleset")).IsEqualTo("Section");
            await Assert.That(colours.GetValueOrDefault("stats")).IsEqualTo("Section");
            await Assert.That(colours.GetValueOrDefault("for")).IsEqualTo("Section");
            await Assert.That(colours.GetValueOrDefault("count")).IsEqualTo("Kind");
            await Assert.That(colours.GetValueOrDefault("per")).IsEqualTo("Modifier");
            await Assert.That(colours.GetValueOrDefault("while")).IsEqualTo("Modifier");
            await Assert.That(colours.GetValueOrDefault("when")).IsEqualTo("Modifier");
            await Assert.That(colours.GetValueOrDefault("each_player")).IsEqualTo("Literal");
            await Assert.That(colours.GetValueOrDefault("kill")).IsEqualTo("Event").Because("catalog event names are yellow");
            await Assert.That(colours.GetValueOrDefault("enemy")).IsEqualTo("Facet").Because("catalog facets are light-blue");
            await Assert.That(colours.GetValueOrDefault("round.active")).IsEqualTo("Path").Because("dotted read-paths are light-blue");
            await Assert.That(colours.GetValueOrDefault("kills")).IsEqualTo("Identifier").Because("a user's own stat id is gold, not a keyword colour");
        });

    /// <summary>
    ///     Highlights <paramref name="text" /> and returns, for each interesting token, the NAME of the
    ///     highlighting colour covering it (or absent if uncoloured). Tokens are matched by first occurrence.
    /// </summary>
    private static Dictionary<string, string> ColourMap(string text)
    {
        TextDocument doc = new(text);
        DocumentHighlighter highlighter = new(doc, WorkbenchYamlHighlighting.Definition);

        // token -> the (line, columnOffsetIntoLine) of its first occurrence
        string[] tokens =
        [
            "ruleset", "for", "stats", "kills", "count", "kill", "per", "round", "while", "round.active",
            "when", "enemy", "each_player"
        ];

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int lineNo = 1; lineNo <= doc.LineCount; lineNo++)
        {
            DocumentLine line = doc.GetLineByNumber(lineNo);
            string lineText = doc.GetText(line);
            HighlightedLine hl = highlighter.HighlightLine(lineNo);

            foreach (string token in tokens)
            {
                if (result.ContainsKey(token))
                {
                    continue;
                }

                // Whole-token match so "kill" doesn't match inside "kills".
                Match m = Regex.Match(
                    lineText, "\\b" + Regex.Escape(token) + "\\b");
                if (!m.Success)
                {
                    continue;
                }

                int idx = m.Index;

                int abs = line.Offset + idx;
                foreach (HighlightedSection section in hl.Sections)
                {
                    if (abs >= section.Offset && abs < section.Offset + section.Length && section.Color.Name is { } name)
                    {
                        result[token] = name;
                        break;
                    }
                }
            }
        }

        return result;
    }
}
