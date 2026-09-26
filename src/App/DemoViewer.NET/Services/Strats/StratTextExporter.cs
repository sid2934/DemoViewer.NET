#region

using System.Globalization;
using System.Text;
using DemoViewer.NET.Controls;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The text export shapes of §3.13: the call sheet (Markdown, for Discord and a text diff) and the
///     role sheets (self-contained HTML, for LAN Print). Both are pure string builders over the model,
///     reading places through <see cref="StratStepPhrasing" /> so the two surfaces phrase a step the same
///     way; only <see cref="LanPrint" /> below touches disk.
/// </summary>
public static class StratTextExporter
{
    /// <summary>
    ///     One strat as text (§3.13): a title, the metadata line, one bullet per step in the shape
    ///     §3.13 gives verbatim (<c>**1:30** B throws smoke A ramp → A site (Stairs)</c>), a branch as an
    ///     indented <c>if … → …</c> under the step it follows, and the strat's notes at the end.
    /// </summary>
    /// <param name="doc">The strat.</param>
    /// <param name="callouts">The owner's callouts for the map; null prints canonical names split into words.</param>
    /// <param name="lookup">Resolves a branch's target strat when it is not <paramref name="doc" /> itself; null leaves it as a raw id.</param>
    /// <param name="lineupTitle">Resolves a step's lineup reference to its card title; null leaves it as a raw id.</param>
    public static string CallSheet(StratDocument doc, CalloutResolver? callouts = null, Func<Guid, StratDocument?>? lookup = null,
        Func<Guid, string?>? lineupTitle = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        StringBuilder md = new();
        md.Append("# ").Append(doc.Name).Append("\n\n");

        List<string> meta = [doc.Map, TypeLine(doc)];
        if (doc.Economy is { Length: > 0 } economy)
        {
            meta.Add(economy + " economy");
        }

        if (doc.Tempo is { Length: > 0 } tempo)
        {
            meta.Add(tempo + " tempo");
        }

        md.Append(string.Join(" · ", meta)).Append('\n');
        if (doc.Trigger?.Text is { Length: > 0 } trigger)
        {
            md.Append("Trigger: ").Append(trigger).Append('\n');
        }

        md.Append('\n');

        Dictionary<Guid, List<StratBranch>> branchesAfter = [];
        foreach (StratBranch branch in doc.Branches)
        {
            if (!branchesAfter.TryGetValue(branch.AfterStepId, out List<StratBranch>? list))
            {
                branchesAfter[branch.AfterStepId] = list = [];
            }

            list.Add(branch);
        }

        foreach (StratStep step in doc.Steps)
        {
            md.Append("- **").Append(StratClock.Format(step.AtSeconds)).Append("** ")
                .Append(StratStepPhrasing.Phrase(step, callouts, lineupTitle)).Append('\n');
            if (!branchesAfter.TryGetValue(step.Id, out List<StratBranch>? branches))
            {
                continue;
            }

            foreach (StratBranch branch in branches)
            {
                md.Append("  - if ").Append(branch.Condition.Text).Append(" → ")
                    .Append(StratBranchPhrasing.TargetText(branch.Target, doc, lookup)).Append('\n');
            }
        }

        if (doc.Notes is { Length: > 0 } notes)
        {
            md.Append('\n').Append(notes).Append('\n');
        }

        return md.ToString();
    }

    private static string TypeLine(StratDocument doc) =>
        doc.Side + " " + doc.Type + (doc.TargetSite is { Length: > 0 } site ? $" (site {site})" : "");
}

/// <summary>
///     The Role View And LAN Print export (§3.14, decision 8): one self-contained HTML page with a
///     print stylesheet, one page per slot. Avalonia has no printing API, so this is the route: the file
///     opens in the system browser, which prints it, and it is also what a coach emails.
/// </summary>
public static class RoleSheetHtmlWriter
{
    /// <summary>
    ///     The page: one <c>section</c> per sheet, in the order given, each starting a new printed page
    ///     except the last. No external reference (font, image, stylesheet) is made: "self-contained" means
    ///     the file a coach emails still renders with nothing else attached, which is also why the
    ///     mini-map is a bare polyline rather than an overlay on the baked radar art.
    /// </summary>
    /// <param name="doc">The strat every sheet belongs to.</param>
    /// <param name="sheets">The sheets to print, in print order.</param>
    public static string Html(StratDocument doc, IReadOnlyList<RoleSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sheets);
        StringBuilder html = new();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(Escape(doc.Name)).Append(" role sheets</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
        foreach (RoleSheet sheet in sheets)
        {
            html.Append(SheetHtml(sheet));
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private const string Css = """
                                * { box-sizing: border-box; }
                                body { font-family: Arial, Helvetica, sans-serif; color: #1a1a1a; margin: 0; }
                                .sheet { padding: 24px 32px; page-break-after: always; }
                                .sheet:last-of-type { page-break-after: auto; }
                                .sheet h1 { margin: 0 0 4px; font-size: 22px; }
                                .sheet h2 { margin: 0 0 12px; font-size: 16px; color: #333; font-weight: normal; }
                                .meta, .trigger { font-size: 13px; color: #444; margin: 0 0 4px; }
                                ol.lines { padding-left: 20px; margin: 12px 0; }
                                ol.lines li { margin: 4px 0; }
                                ol.lines li.context { color: #888; font-style: italic; }
                                ul.branches { padding-left: 20px; margin: 8px 0; font-size: 13px; color: #333; }
                                .minimap { margin: 12px 0; }
                                .status { font-size: 12px; color: #777; margin-top: 16px; }
                                @media print { body { margin: 0; } }
                                """
                                + "\n";

    private static string SheetHtml(RoleSheet sheet)
    {
        RoleSheetHeader header = sheet.Header;
        StringBuilder section = new();
        section.Append("<section class=\"sheet\">\n");
        section.Append("<h1>").Append(Escape(header.StratName)).Append("</h1>\n");
        section.Append("<h2>Slot ").Append(Escape(header.Slot));
        if (header.SlotName is { Length: > 0 } name)
        {
            section.Append(": ").Append(Escape(name));
        }

        if (header.Role is { Length: > 0 } role)
        {
            section.Append(" (").Append(Escape(role)).Append(')');
        }

        section.Append("</h2>\n");

        List<string> meta = [Escape(header.Map), Escape(header.Side + " " + header.Type + (header.TargetSite is { Length: > 0 } s ? $" (site {s})" : ""))];
        if (header.Economy is { Length: > 0 } economy)
        {
            meta.Add(Escape(economy) + " economy");
        }

        if (header.Tempo is { Length: > 0 } tempo)
        {
            meta.Add(Escape(tempo) + " tempo");
        }

        section.Append("<div class=\"meta\">").Append(string.Join(" · ", meta)).Append("</div>\n");
        if (header.TriggerText is { Length: > 0 } trigger)
        {
            section.Append("<div class=\"trigger\">Trigger: ").Append(Escape(trigger)).Append("</div>\n");
        }

        section.Append("<ol class=\"lines\">\n");
        foreach (RoleSheetLine line in sheet.Lines)
        {
            section.Append("<li").Append(line.IsContext ? " class=\"context\"" : "").Append('>')
                .Append(StratClock.Format(line.AtSeconds)).Append(" · ").Append(Escape(line.Text)).Append("</li>\n");
        }

        section.Append("</ol>\n");

        if (sheet.Branches.Count > 0)
        {
            section.Append("<ul class=\"branches\">\n");
            foreach (RoleSheetBranchLine branch in sheet.Branches)
            {
                section.Append("<li>").Append(Escape(branch.Text)).Append("</li>\n");
            }

            section.Append("</ul>\n");
        }

        if (sheet.Positions.Count > 0)
        {
            section.Append(MiniMapSvg(sheet.Positions));
        }

        section.Append("<div class=\"status\">Status: ").Append(Escape(header.Status)).Append(" · revision ")
            .Append(header.Revision.ToString(CultureInfo.InvariantCulture)).Append("</div>\n");
        section.Append("</section>\n");
        return section.ToString();
    }

    /// <summary>
    ///     A plain polyline of the slot's tracked positions, normalised into a small fixed viewBox. Not an
    ///     overlay on the map: a self-contained page carries no basemap image, so this shows the route's
    ///     shape, not its place on the radar. Role View, which does have the baked art, is free to draw the
    ///     same points over it; this writer only needs to stay email-safe.
    /// </summary>
    /// <param name="points">The slot's positions, in step order.</param>
    private static string MiniMapSvg(IReadOnlyList<RoleSheetPoint> points)
    {
        const double size = 160;
        const double pad = 10;
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double spanX = Math.Max(maxX - minX, 1);
        double spanY = Math.Max(maxY - minY, 1);

        (double X, double Y) Project(RoleSheetPoint p) =>
            (pad + (p.X - minX) / spanX * (size - 2 * pad), pad + (p.Y - minY) / spanY * (size - 2 * pad));

        StringBuilder svg = new();
        svg.Append("<svg class=\"minimap\" width=\"").Append(size.ToString(CultureInfo.InvariantCulture))
            .Append("\" height=\"").Append(size.ToString(CultureInfo.InvariantCulture))
            .Append("\" viewBox=\"0 0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(size.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"none\" stroke=\"#ccc\"/>\n");
        if (points.Count > 1)
        {
            svg.Append("<polyline fill=\"none\" stroke=\"#1a73e8\" stroke-width=\"2\" points=\"");
            svg.Append(string.Join(" ", points.Select(p =>
            {
                (double x, double y) = Project(p);
                return x.ToString("0.#", CultureInfo.InvariantCulture) + "," + y.ToString("0.#", CultureInfo.InvariantCulture);
            })));
            svg.Append("\"/>\n");
        }

        foreach (RoleSheetPoint point in points)
        {
            (double x, double y) = Project(point);
            svg.Append("<circle cx=\"").Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append("\" cy=\"")
                .Append(y.ToString("0.#", CultureInfo.InvariantCulture)).Append("\" r=\"3\" fill=\"#1a73e8\"/>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>
///     Writing the role sheets' HTML to disk and handing it to the system browser (§3.14). The write is
///     the testable half; the launch is a thin call onto <see cref="OpenExternal" />, deliberately not its
///     <c>OpenLocalFile</c> (which tries VS Code first): an exported page is content for a coach to read
///     and print, never source to edit.
/// </summary>
public static class LanPrint
{
    /// <summary>Writes self-contained HTML to a fresh file under the temp directory and returns its path.</summary>
    /// <param name="html">The page, as built by <see cref="RoleSheetHtmlWriter.Html" />.</param>
    /// <param name="fileNameHint">A short, filesystem-safe stem; a unique suffix is always added.</param>
    public static string WriteTempFile(string html, string fileNameHint = "strat-roles")
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(fileNameHint);
        string path = Path.Combine(Path.GetTempPath(), $"{fileNameHint}-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, html);
        return path;
    }

    /// <summary>
    ///     Writes the page and opens it in the OS default handler for <c>.html</c>, the system browser on
    ///     every desktop this app ships to. Returns the path opened, or null when the OS could not launch a
    ///     handler (no registered handler, a locked-down shell): the caller reports that rather than the
    ///     page silently never appearing.
    /// </summary>
    /// <param name="html">The page.</param>
    /// <param name="fileNameHint">A short, filesystem-safe stem for the temp file.</param>
    public static string? WriteAndOpen(string html, string fileNameHint = "strat-roles")
    {
        string path = WriteTempFile(html, fileNameHint);
        return OpenExternal.OpenUri(path) ? path : null;
    }
}
