#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One authoring diagnostic row surfaced in the Workbench: a display projection of a
///     <see cref="RulesetDiagnostic" /> from the demo-less v2 checker. <see cref="Location" /> is the
///     spec's <c>file(line,col)</c> prefix; <see cref="File" />/<see cref="Line" />/<see cref="Column" />
///     are kept separate for a future click-to-open (M2, reusing <c>OpenExternal.OpenLocalFile</c>).
/// </summary>
/// <param name="Location">The <c>file(line,col)</c> prefix.</param>
/// <param name="Message">The human-readable message (what was written, what was expected).</param>
/// <param name="Code">The diagnostic code (e.g. <c>resolve.unknown-name</c>).</param>
/// <param name="File">Absolute source path, or null for inline YAML.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
public sealed record WorkbenchDiagnostic(
    string Location,
    string Message,
    string Code,
    string? File,
    int Line,
    int Column)
{
    /// <summary>Projects a checker diagnostic to a display row.</summary>
    public static WorkbenchDiagnostic From(RulesetDiagnostic diagnostic)
    {
        SourcePosition p = diagnostic.Position;
        return new WorkbenchDiagnostic(
            p.ToString(), diagnostic.Message, diagnostic.Code, p.File, p.Line, p.Column);
    }

    /// <summary>
    ///     Projects a zones-overlay diagnostic to a display row. The overlay reader keeps no line
    ///     positions (one bad entry is skipped as a whole), so the location is the file alone, and the
    ///     entry name the diagnostic carries is folded into the message.
    /// </summary>
    /// <param name="diagnostic">The loader's diagnostic.</param>
    /// <param name="overlayPath">The overlay file, or null.</param>
    public static WorkbenchDiagnostic FromZones(ZoneDiagnostic diagnostic, string? overlayPath)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        string location = overlayPath is null ? "zones overlay" : Path.GetFileName(overlayPath);
        return new WorkbenchDiagnostic(location, diagnostic.Message, diagnostic.Code, overlayPath, 0, 0);
    }
}
