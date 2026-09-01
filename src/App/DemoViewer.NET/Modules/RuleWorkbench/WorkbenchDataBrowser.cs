#region

using System.Collections.ObjectModel;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One node of the data-browser vocabulary TREE: a dotted path segment. Intermediate
///     nodes (<c>match</c>, <c>player.entity</c>) expand; a node with a non-null <see cref="FullPath" />
///     is an insertable leaf (double-click inserts it). A node can be both: e.g. <c>match</c> may be a
///     complete path AND have children.
/// </summary>
public sealed class WorkbenchPathNode
{
    public required string Segment { get; init; }

    /// <summary>The complete path this node represents, or null when it is only an intermediate prefix.</summary>
    public string? FullPath { get; set; }

    public ObservableCollection<WorkbenchPathNode> Children { get; } = [];

    /// <summary>Category of the leaf (context / entity), or empty for intermediates, shown dimmed.</summary>
    public string Category { get; set; } = "";
}

/// <summary>
///     A ruleset file selectable in the Authoring dropdown: a shipped (read-only unless
///     DeveloperMode) or user (editable) <c>*.rules.yaml</c>.
/// </summary>
/// <param name="FullPath">Absolute path.</param>
/// <param name="FileName">Bare file name.</param>
/// <param name="Display">Dropdown label (shipped files are tagged).</param>
/// <param name="IsShipped">True for a shipped baseline ruleset.</param>
public sealed record RulesetFileRef(string FullPath, string FileName, string Display, bool IsShipped);

/// <summary>Builds the data-browser vocabulary tree from the flat catalog paths.</summary>
public static class WorkbenchPathTree
{
    /// <summary>Groups dotted paths into a prefix tree; the final segment of each path carries its full path.</summary>
    public static IReadOnlyList<WorkbenchPathNode> Build(IEnumerable<WorkbenchPath> paths)
    {
        List<WorkbenchPathNode> roots = [];
        Dictionary<string, WorkbenchPathNode> byPrefix = new(StringComparer.Ordinal);

        foreach (WorkbenchPath path in paths.OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            string[] segments = path.Path.Split('.');
            string prefix = "";
            WorkbenchPathNode? parent = null;
            for (int i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[0] : $"{prefix}.{segments[i]}";
                if (!byPrefix.TryGetValue(prefix, out WorkbenchPathNode? node))
                {
                    node = new WorkbenchPathNode
                    {
                        Segment = segments[i]
                    };
                    byPrefix[prefix] = node;
                    if (parent is null)
                    {
                        roots.Add(node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }
                }

                parent = node;
                if (i == segments.Length - 1)
                {
                    node.FullPath = path.Path;
                    node.Category = path.Category;
                }
            }
        }

        return roots;
    }
}

/// <summary>
///     A draggable authoring-vocabulary path in the data-browser palette: a catalog
///     context or entity-read path the author drags/double-clicks into the editor.
/// </summary>
/// <param name="Path">The path text inserted (e.g. <c>player.entity.pawn.health</c>, <c>round.number</c>).</param>
/// <param name="Category">The kind of path (context / entity).</param>
public sealed record WorkbenchPath(string Path, string Category);

/// <summary>One live player row in the data browser: real values at the current frame.</summary>
/// <param name="Name">Roster display name.</param>
/// <param name="Team">Live team number.</param>
/// <param name="Position">Reconstructed world position, or an em-dash when unavailable.</param>
public sealed record LivePlayerRow(string Name, int Team, string Position);

/// <summary>
///     A rendered evaluation output as a real grid: the per-player scoreboard, or a declared
///     <c>tables:</c> / keyed table. Rendered as a titled header row + aligned data rows.
/// </summary>
/// <param name="Title">The table name (e.g. "Player Game Stats", "kast_game_totals", "kills_by_weapon").</param>
/// <param name="Columns">Ordered value-column headers.</param>
/// <param name="Rows">Per-entity rows, cells aligned to <paramref name="Columns" />.</param>
public sealed record WorkbenchScoreboard(string Title, IReadOnlyList<string> Columns, IReadOnlyList<WorkbenchScoreRow> Rows);

/// <summary>One row of a <see cref="WorkbenchScoreboard" />: an entity label + its column cells.</summary>
public sealed record WorkbenchScoreRow(string Label, IReadOnlyList<string> Cells);
