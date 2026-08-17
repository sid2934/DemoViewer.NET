#region

using System.Globalization;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Visualization.Sample.SampleGraphs;

/// <summary>Demo state graph sample.</summary>
public static class DemoStateGraphSample
{
    /// <summary>Builds the canonical demo-state graph fixture: nodes, edges, groups, and a stats table.</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups, INodeTable Table) Build()
    {
        // ── Lifecycle nodes ──────────────────────────────────────────────
        NodeStyle rootStyle = new()
        {
            ActiveBackground = Color.Parse("#141428"),
            ActiveBorder = Color.Parse("#303050"),
            ActiveForeground = Color.Parse("#505068")
        };
        SampleNode root = new("Root", true, true, style: rootStyle);
        SampleNode currentGameTick = new("CurrentGameTick", isActive: false, displayValue: "48320");
        SampleNode mapName = new("MapName", isActive: true, displayValue: "de_mirage");
        SampleNode roundActive = new("RoundActive", isActive: true);
        SampleNode roundNumber = new("RoundNumber", isActive: true, displayValue: "3");
        SampleNode roundLive = new("RoundLive", isActive: true, subtitle: "ConjunctionNode");
        SampleNode isOddRoundOnMirage = new("IsOddRoundOnMirage", isActive: true, subtitle: "ConjunctionNode");

        IReadOnlyList<IGraphNode> nodes =
            [root, currentGameTick, mapName, roundActive, roundNumber, roundLive, isOddRoundOnMirage];

        // ── Lifecycle edges ──────────────────────────────────────────────
        IReadOnlyList<IGraphEdge> edges =
        [
            new SampleEdge(root, mapName, "DEM_FileHeader", VisualEdgeEffect.SetValue),
            new SampleEdge(root, roundActive, "round_freeze_end", VisualEdgeEffect.Activate),
            new SampleEdge(roundActive, roundActive, "round_officially_ended", VisualEdgeEffect.Deactivate),
            new SampleEdge(roundActive, roundNumber, "round_freeze_end", VisualEdgeEffect.SetValue),
            // Conjunction inputs (using per-edge style override)
            new ConjunctionSampleEdge(roundActive, roundLive, "", "active"),
            new ConjunctionSampleEdge(mapName, isOddRoundOnMirage, "", "== \"de_mirage\""),
            new ConjunctionSampleEdge(roundActive, isOddRoundOnMirage, "", "active"),
            new ConjunctionSampleEdge(roundNumber, isOddRoundOnMirage, "", "% 2 == 1")
        ];

        // ── Groups ───────────────────────────────────────────────────────
        IReadOnlyList<INodeGroup> groups =
        [
            new SampleGroup("Lifecycle",
                [root, mapName, roundActive, roundNumber, roundLive, isOddRoundOnMirage]),
            new SampleGroup("Temporal", [currentGameTick])
        ];

        // ── Per-player table (10 players × 9 columns) ────────────────────
        string[] columnNames =
        [
            "Kills", "Assists", "Deaths", "Alive", "Survived",
            "TradeWindow", "Traded", "HasKAST", "KAST%"
        ];

        string[] playerNames =
        [
            "Boss", "aNig Shot", "experts", "Murphypoo", "Tacoman nooo",
            "KKS", "Bazooki", "Little Michael Jackson", "renaissance", "bad Light"
        ];

        List<ITableRow> tableRows = new();
        for (int p = 0; p < playerNames.Length; p++)
        {
            List<ITableCell> cells = new();
            for (int c = 0; c < columnNames.Length; c++)
            {
                bool isActive = c < 4; // First 4 columns active for demo
                string? value = c switch
                {
                    0 => (p % 3).ToString(CultureInfo.InvariantCulture),
                    1 => (p % 2).ToString(CultureInfo.InvariantCulture),
                    2 => (p % 4).ToString(CultureInfo.InvariantCulture),
                    3 => null, // "Alive" — bool, show ACTIVE
                    8 => $"{50 + p * 5}%",
                    _ => null
                };
                cells.Add(new SampleTableCell
                {
                    IsActive = isActive,
                    DisplayValue = value
                });
            }

            tableRows.Add(new SampleTableRow
            {
                Label = playerNames[p],
                FilterAnnotation = $"slot == {p}",
                Cells = cells
            });
        }

        IReadOnlyList<ITableColumnEdge> columnEdges =
        [
            new SampleColumnEdge(roundActive, 0, "player_death", VisualEdgeEffect.SetValue, "attacker == player"),
            new SampleColumnEdge(roundActive, 1, "player_death", VisualEdgeEffect.SetValue, "assister == player"),
            new SampleColumnEdge(roundActive, 2, "player_death", VisualEdgeEffect.SetValue, "victim == player"),
            new SampleColumnEdge(root, 3, "round_freeze_end", VisualEdgeEffect.Activate),
            new SampleColumnEdge(root, 3, "player_death", VisualEdgeEffect.Deactivate, "victim == player")
        ];

        SampleTable table = new()
        {
            ColumnNames = columnNames,
            Rows = tableRows,
            ColumnEdges = columnEdges
        };

        return (nodes, edges, groups, table);
    }
}
