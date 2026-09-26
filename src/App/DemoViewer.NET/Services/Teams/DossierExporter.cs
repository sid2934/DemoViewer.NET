#region

using System.Text;
using DemoViewer.NET.Controls;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>Which of a Dossier's two forms an export prints.</summary>
public enum DossierForm
{
    /// <summary>The one-pager: the summary and the starred findings. The default export.</summary>
    OnePager,

    /// <summary>The working document: every finding not left out, starred ones marked.</summary>
    LongForm
}

/// <summary>The file an export writes.</summary>
public enum DossierFormat
{
    /// <summary>Self-contained HTML with a print stylesheet, the LAN Print route.</summary>
    Html,

    /// <summary>Markdown, for Discord and a text diff.</summary>
    Markdown
}

/// <summary>
///     One line of a Dossier as it will print: its key, the section it sits under, the text (the user's
///     rewrite when there is one), and whether it is on the one-pager. A Setup Heatmap's line carries its
///     rendered picture, which the HTML form embeds.
/// </summary>
public sealed class DossierFinding
{
    public string Key { get; init; } = "";

    public string Section { get; init; } = "";

    public string Text { get; init; } = "";

    public bool IsStarred { get; init; }

    /// <summary>A PNG to print under the line in HTML, or null.</summary>
    public byte[]? ImagePng { get; init; }
}

/// <summary>A Dossier ready to export: the team, its sample size, the summary and the lines not left out, in order.</summary>
public sealed class DossierDocument
{
    public string TeamName { get; init; } = "";

    /// <summary>"18 demos across 4 maps", the Map Pool Record's own line.</summary>
    public string SampleLine { get; init; } = "";

    public string Summary { get; init; } = "";

    public IReadOnlyList<DossierFinding> Findings { get; init; } = [];
}

/// <summary>
///     The Dossier's export (plan.md §3, Dossier Editing And Export): the short form (the one-pager of
///     starred findings, the default) and the long form (the working document), each as self-contained
///     HTML like LAN Print or as Markdown. Pure string builders over <see cref="DossierDocument" />; the
///     writer below is the only part that touches disk.
///     <para>
///         <b>No verdicts.</b> The export prints what the demos counted and what the user wrote, nothing
///         else: no rating, no grade, no win probability. Every generated line is a count over a stated
///         sample, so the export adds no line of its own beyond the heading, the sample and the summary.
///     </para>
/// </summary>
public static class DossierExporter
{
    /// <summary>The short form's line when nothing is starred.</summary>
    public const string NothingStarredLine = "nothing starred yet: star findings in the long form to build the one-pager";

    /// <summary>The document's title for a form: "Falcons one-pager" or "Falcons dossier".</summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form.</param>
    public static string Title(DossierDocument doc, DossierForm form)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string team = doc.TeamName.Length == 0 ? "Opponent" : doc.TeamName;
        return form == DossierForm.OnePager ? $"{team} one-pager" : $"{team} dossier";
    }

    /// <summary>The document as Markdown.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form; the short form is the default export.</param>
    public static string Markdown(DossierDocument doc, DossierForm form = DossierForm.OnePager)
    {
        ArgumentNullException.ThrowIfNull(doc);
        StringBuilder md = new();
        md.Append("# ").Append(Title(doc, form)).Append("\n\n");
        if (doc.SampleLine.Length > 0)
        {
            md.Append("Sample: ").Append(doc.SampleLine).Append("\n\n");
        }

        if (doc.Summary.Length > 0)
        {
            md.Append(doc.Summary).Append("\n\n");
        }

        List<IGrouping<string, DossierFinding>> sections = Sections(doc, form);
        if (form == DossierForm.OnePager && sections.Count == 0)
        {
            md.Append('_').Append(NothingStarredLine).Append("_\n");
            return md.ToString();
        }

        foreach (IGrouping<string, DossierFinding> section in sections)
        {
            md.Append("## ").Append(section.Key).Append("\n\n");
            foreach (DossierFinding finding in section)
            {
                md.Append("- ");
                if (form == DossierForm.LongForm && finding.IsStarred)
                {
                    md.Append("★ ");
                }

                md.Append(finding.Text.ReplaceLineEndings(" ")).Append('\n');
            }

            md.Append('\n');
        }

        return md.ToString();
    }

    /// <summary>
    ///     The document as one self-contained HTML page: no external reference, pictures inlined as data
    ///     URIs, so the file a coach emails renders with nothing else attached (the LAN Print rule).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form; the short form is the default export.</param>
    public static string Html(DossierDocument doc, DossierForm form = DossierForm.OnePager)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string title = Title(doc, form);
        StringBuilder html = new();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(Escape(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(Escape(title)).Append("</h1>\n");
        if (doc.SampleLine.Length > 0)
        {
            html.Append("<div class=\"sample\">Sample: ").Append(Escape(doc.SampleLine)).Append("</div>\n");
        }

        if (doc.Summary.Length > 0)
        {
            foreach (string paragraph in doc.Summary.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                html.Append("<p class=\"summary\">").Append(Escape(paragraph).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>\n");
            }
        }

        List<IGrouping<string, DossierFinding>> sections = Sections(doc, form);
        if (form == DossierForm.OnePager && sections.Count == 0)
        {
            html.Append("<p class=\"empty\">").Append(Escape(NothingStarredLine)).Append("</p>\n");
        }

        foreach (IGrouping<string, DossierFinding> section in sections)
        {
            html.Append("<section>\n<h2>").Append(Escape(section.Key)).Append("</h2>\n<ul>\n");
            foreach (DossierFinding finding in section)
            {
                html.Append("<li").Append(form == DossierForm.LongForm && finding.IsStarred ? " class=\"starred\"" : "").Append('>')
                    .Append(Escape(finding.Text));
                if (finding.ImagePng is { Length: > 0 } png)
                {
                    html.Append("<br><img alt=\"").Append(Escape(finding.Text)).Append("\" src=\"data:image/png;base64,")
                        .Append(Convert.ToBase64String(png)).Append("\">");
                }

                html.Append("</li>\n");
            }

            html.Append("</ul>\n</section>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    // The sections in first-appearance order; the short form keeps only starred lines and drops empty sections.
    private static List<IGrouping<string, DossierFinding>> Sections(DossierDocument doc, DossierForm form) =>
    [
        .. doc.Findings
            .Where(f => form == DossierForm.LongForm || f.IsStarred)
            .GroupBy(f => f.Section, StringComparer.Ordinal)
    ];

    private const string Css = """
                               * { box-sizing: border-box; }
                               body { font-family: Arial, Helvetica, sans-serif; color: #1a1a1a; margin: 0 auto; padding: 24px 32px; max-width: 900px; }
                               h1 { margin: 0 0 4px; font-size: 22px; }
                               h2 { margin: 18px 0 6px; font-size: 15px; color: #333; }
                               .sample { font-size: 13px; color: #555; margin: 0 0 10px; }
                               .summary { font-size: 14px; margin: 8px 0; }
                               .empty { font-size: 13px; color: #777; font-style: italic; }
                               ul { padding-left: 20px; margin: 4px 0; }
                               li { margin: 3px 0; font-size: 13px; }
                               li.starred { font-weight: bold; }
                               li.starred::before { content: "\2605  "; }
                               img { max-width: 320px; margin: 6px 0; border: 1px solid #ccc; }
                               section { page-break-inside: avoid; }
                               @media print { body { padding: 0; } }
                               """
                               + "\n";

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>
///     Writing an export to a fresh file under the temp directory and handing it to the OS, the LAN Print
///     route: an exported page is for a coach to read, print or email, and the browser prints it.
/// </summary>
public static class DossierExportWriter
{
    /// <summary>Writes the text to a fresh temp file and returns its path.</summary>
    /// <param name="text">The export.</param>
    /// <param name="fileNameHint">A short, filesystem-safe stem; a unique suffix is always added.</param>
    /// <param name="extension">The extension, with its dot.</param>
    public static string WriteTempFile(string text, string fileNameHint, string extension)
    {
        ArgumentNullException.ThrowIfNull(text);
        string path = Path.Combine(Path.GetTempPath(), $"{fileNameHint}-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes the export and opens it; the path, or null when the OS launched no handler.</summary>
    /// <param name="text">The export.</param>
    /// <param name="fileNameHint">A short, filesystem-safe stem.</param>
    /// <param name="extension">The extension, with its dot.</param>
    public static string? WriteAndOpen(string text, string fileNameHint, string extension)
    {
        string path = WriteTempFile(text, fileNameHint, extension);
        return OpenExternal.OpenUri(path) ? path : null;
    }
}
