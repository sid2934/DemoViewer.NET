#region

using CS2DemoKit.Analysis.Yaml;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     One row in the Analysis tab's rule-diagnostics panel: a load error or semantic warning about
///     the rule configuration, attributed to its source file / chain / rule where known. Serves both
///     audiences: it is the rule author's build log and the user's "why is my stat empty".
/// </summary>
/// <param name="Severity">"error" (file failed to load), "warning" (likely authoring bug), or "info".</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="FilePath">Absolute path of the source file, when known: enables click-to-open.</param>
/// <param name="ChainId">The chain involved, when identifiable.</param>
/// <param name="RuleId">The rule involved, when identifiable.</param>
/// <param name="Line">1-based line in the source file, when the loader supplied one (YAML syntax / unknown-key errors).</param>
/// <param name="Column">1-based column in the source file, when the loader supplied one.</param>
public sealed record RuleDiagnostic(
    string Severity,
    string Message,
    string? FilePath = null,
    string? ChainId = null,
    string? RuleId = null,
    int? Line = null,
    int? Column = null)
{
    /// <summary>Severity glyph for the panel row.</summary>
    public string Glyph => Severity switch
    {
        "error" => "✕",
        "warning" => "⚠",
        _ => "ℹ"
    };

    /// <summary>True when the row carries a file to open.</summary>
    public bool CanOpen => FilePath is not null;

    /// <summary>
    ///     Compact "file(line,col) · chain/rule" locator line under the message. The position
    ///     suffix mirrors <see cref="RuleConfigError" />'s formatting and appears only when the
    ///     loader captured one: positionless rows render exactly as before.
    /// </summary>
    public string Location
    {
        get
        {
            string file = FilePath is null ? "" : Path.GetFileName(FilePath);
            if (file.Length > 0 && Line is not null)
            {
                file += $"({Line},{Column ?? 0})";
            }

            string scope = (ChainId, RuleId) switch
            {
                (null, null) => "",
                (not null, null) => $"chain '{ChainId}'",
                (null, not null) => $"rule '{RuleId}'",
                _ => $"chain '{ChainId}' · rule '{RuleId}'"
            };

            return (file, scope) switch
            {
                ("", "") => "",
                (_, "") => file,
                ("", _) => scope,
                _ => $"{file} · {scope}"
            };
        }
    }

    /// <summary>
    ///     Projects a loader error into an "error"-severity diagnostic row, preserving
    ///     file/chain/rule/line/column attribution. The single mapping both the tolerant
    ///     (user-tier) and hard-fail (shipped-tier) paths use: work item 0.3 restored the
    ///     position fields this mapping previously dropped.
    /// </summary>
    public static RuleDiagnostic FromError(RuleConfigError error) =>
        new("error", error.Message, error.FilePath, error.ChainId, error.RuleId, error.Line, error.Column);
}
