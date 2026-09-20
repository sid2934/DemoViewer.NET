#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Visualization.Sample.SampleGraphs;

/// <summary>Stress test graphs.</summary>
public static class StressTestGraphs
{
    /// <summary>
    ///     ~120 nodes / ~200 edges, grouped into bands, plus one player table — the
    ///     everything-at-once A/B regression graph. Deterministic via a fixed seed.
    ///     This is the primary iteration target for v1-vs-v2 comparison.
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups, IReadOnlyList<INodeTable> Tables) BuildBigStandard()
    {
        const int NodeCount = 120;
        Random rng = new(424242);

        List<SampleNode> nodes = new();
        for (int i = 0; i < NodeCount; i++)
        {
            nodes.Add(new SampleNode($"S{i:D3}", i == 0, i % 4 != 0,
                i % 3 == 0 ? (i % 17).ToString(CultureInfo.InvariantCulture) : null,
                i % 11 == 0 ? "ConjunctionNode" : null));
        }

        HashSet<(int, int)> seen = new();
        List<IGraphEdge> edges = new();
        // Connected spine.
        for (int i = 0; i < NodeCount - 1; i++)
        {
            edges.Add(new SampleEdge(nodes[i], nodes[i + 1], "next", VisualEdgeEffect.Activate));
            seen.Add((i, i + 1));
        }

        // Random forward edges up to ~200 total.
        int attempts = 0;
        while (edges.Count < 200 && attempts < 8000)
        {
            attempts++;
            int s = rng.Next(NodeCount - 1);
            int d = s + 1 + rng.Next(NodeCount - 1 - s);
            if (!seen.Add((s, d)))
            {
                continue;
            }

            VisualEdgeEffect effect = (VisualEdgeEffect)(edges.Count % 5);
            edges.Add(new SampleEdge(nodes[s], nodes[d],
                $"r{s}_{d}", effect, edges.Count % 4 == 0 ? "cond" : null));
        }

        // Group nodes into contiguous bands of ~20.
        List<INodeGroup> groups = new();
        for (int g = 0; g * 20 < NodeCount; g++)
        {
            IGraphNode[] members = nodes.Skip(g * 20).Take(20).Cast<IGraphNode>().ToArray();
            if (members.Length > 0)
            {
                groups.Add(new SampleGroup($"Band {g}", members));
            }
        }

        // One player table with column edges from early-band nodes.
        string[] columns = ["Kills", "Assists", "Deaths", "KAST%"];
        List<ITableRow> rows = new();
        for (int p = 0; p < 10; p++)
        {
            List<ITableCell> cells = new();
            for (int c = 0; c < columns.Length; c++)
            {
                cells.Add(new SampleTableCell
                {
                    IsActive = c < 3,
                    DisplayValue = c == 3 ? $"{40 + p * 4}%" : ((p + c) % 6).ToString(CultureInfo.InvariantCulture)
                });
            }

            rows.Add(new SampleTableRow
            {
                Label = $"Player_{p:D2}",
                FilterAnnotation = $"slot == {p}",
                Cells = cells
            });
        }

        IReadOnlyList<ITableColumnEdge> columnEdges =
        [
            new SampleColumnEdge(nodes[1], 0, "player_death", VisualEdgeEffect.SetValue, "attacker == player"),
            new SampleColumnEdge(nodes[2], 1, "player_death", VisualEdgeEffect.SetValue, "assister == player"),
            new SampleColumnEdge(nodes[3], 2, "player_death", VisualEdgeEffect.SetValue, "victim == player"),
            new SampleColumnEdge(nodes[0], 3, "round_freeze_end", VisualEdgeEffect.Activate)
        ];
        IReadOnlyList<INodeTable> tables =
        [
            new SampleTable
            {
                ColumnNames = columns,
                Rows = rows,
                ColumnEdges = columnEdges
            }
        ];

        return (nodes.Cast<IGraphNode>().ToList(), edges, groups, tables);
    }

    /// <summary>10 sources → 1 sink through 2 intermediate layers. Tests convergence.</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildConvergence()
    {
        List<SampleNode> sources = new();
        for (int i = 0; i < 10; i++)
        {
            sources.Add(new SampleNode($"Src_{i}", isActive: true));
        }

        List<SampleNode> intermediates = new();
        for (int i = 0; i < 3; i++)
        {
            intermediates.Add(new SampleNode($"Mid_{i}", isActive: true));
        }

        SampleNode sink = new("Sink", isActive: true, displayValue: "converged");

        List<IGraphNode> nodes = new();
        nodes.AddRange(sources);
        nodes.AddRange(intermediates);
        nodes.Add(sink);

        List<IGraphEdge> edges = new();
        for (int i = 0; i < sources.Count; i++)
        {
            edges.Add(new SampleEdge(sources[i], intermediates[i % 3],
                "feed", VisualEdgeEffect.SetValue));
        }

        foreach (SampleNode mid in intermediates)
        {
            edges.Add(new SampleEdge(mid, sink, "merge", VisualEdgeEffect.Conjunction));
        }

        IReadOnlyList<INodeGroup> groups =
        [
            new SampleGroup("Sources", sources.ToArray()),
            new SampleGroup("Intermediates", intermediates.ToArray())
        ];

        return (nodes, edges, groups);
    }

    /// <summary>
    ///     ~30 nodes / ~80 edges of deterministic random forward-wiring. Stresses edge
    ///     crossings and channel congestion (routing Pass 4, label placement Pass 6).
    ///     Deterministic via a fixed seed so baseline metrics are reproducible.
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildDenseCluster()
    {
        const int NodeCount = 30;
        Random rng = new(20260528);

        List<SampleNode> nodes = new();
        for (int i = 0; i < NodeCount; i++)
        {
            nodes.Add(new SampleNode($"N{i:D2}", i == 0, i % 3 != 0,
                (i % 5).ToString(CultureInfo.InvariantCulture)));
        }

        HashSet<(int, int)> seen = new();
        List<IGraphEdge> edges = new();
        // Spine to keep the graph connected and layered.
        for (int i = 0; i < NodeCount - 1; i++)
        {
            edges.Add(new SampleEdge(nodes[i], nodes[i + 1], $"e{i}", VisualEdgeEffect.Activate));
            seen.Add((i, i + 1));
        }

        // Random forward edges (src < dst) up to ~80 total.
        int attempts = 0;
        while (edges.Count < 80 && attempts < 2000)
        {
            attempts++;
            int s = rng.Next(NodeCount - 1);
            int d = s + 1 + rng.Next(NodeCount - 1 - s);
            if (!seen.Add((s, d)))
            {
                continue;
            }

            edges.Add(new SampleEdge(nodes[s], nodes[d],
                $"f{s}_{d}", VisualEdgeEffect.SetValue));
        }

        return (nodes.Cast<IGraphNode>().ToList(), edges, []);
    }

    /// <summary>Diamond pattern: A→B, A→C, B→D, C→D. Tests crossing minimization.</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildDiamond()
    {
        SampleNode a = new("A", true, true);
        SampleNode b = new("B", isActive: true, displayValue: "left");
        SampleNode c = new("C", isActive: true, displayValue: "right");
        SampleNode d = new("D", isActive: true, displayValue: "merge");

        IReadOnlyList<IGraphNode> nodes = [a, b, c, d];
        IReadOnlyList<IGraphEdge> edges =
        [
            new SampleEdge(a, b, "left_path", VisualEdgeEffect.Activate),
            new SampleEdge(a, c, "right_path", VisualEdgeEffect.Activate),
            new SampleEdge(b, d, "converge", VisualEdgeEffect.Conjunction, "left done"),
            new SampleEdge(c, d, "converge", VisualEdgeEffect.Conjunction, "right done")
        ];

        return (nodes, edges, []);
    }

    /// <summary>
    ///     Three fully disconnected components. Stresses component packing and the
    ///     content bounding box (containment Pass 7).
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildDisconnectedComponents()
    {
        List<SampleNode> nodes = new();
        List<IGraphEdge> edges = new();
        List<INodeGroup> groups = new();

        for (int comp = 0; comp < 3; comp++)
        {
            List<SampleNode> members = new();
            // Each component is a small chain with a fan-out tail.
            SampleNode head = new($"C{comp}_Head", true, true);
            members.Add(head);
            SampleNode prev = head;
            for (int i = 0; i < 2 + comp; i++)
            {
                SampleNode n = new($"C{comp}_N{i}", isActive: i % 2 == 0,
                    displayValue: i.ToString(CultureInfo.InvariantCulture));
                members.Add(n);
                edges.Add(new SampleEdge(prev, n, $"step{i}", VisualEdgeEffect.Activate));
                prev = n;
            }

            nodes.AddRange(members);
            groups.Add(new SampleGroup($"Component {comp}", members.ToArray()));
        }

        return (nodes.Cast<IGraphNode>().ToList(), edges, groups);
    }

    /// <summary>10 sources → 1 sink. High in-degree single node. Stresses port assignment (Pass 3).</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildFanIn()
    {
        SampleNode sink = new("Sink", isActive: true, displayValue: "merged");
        List<SampleNode> sources = new();
        for (int i = 0; i < 10; i++)
        {
            sources.Add(new SampleNode($"Src_{i}", true, i % 2 == 0,
                i.ToString(CultureInfo.InvariantCulture)));
        }

        List<IGraphNode> nodes = new();
        nodes.AddRange(sources);
        nodes.Add(sink);

        List<IGraphEdge> edges = new();
        foreach (SampleNode src in sources)
        {
            edges.Add(new SampleEdge(src, sink, "feed", VisualEdgeEffect.SetValue));
        }

        IReadOnlyList<INodeGroup> groups =
            [new SampleGroup("Fan-In Test", nodes.ToArray())];

        return (nodes, edges, groups);
    }

    /// <summary>1 root → 10 children. Tests port distribution and fan-out routing.</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildFanOut()
    {
        SampleNode root = new("Source", true, true);
        List<SampleNode> children = new();
        for (int i = 0; i < 10; i++)
        {
            children.Add(new SampleNode($"Child_{i}", isActive: i % 2 == 0,
                displayValue: i.ToString(CultureInfo.InvariantCulture)));
        }

        List<IGraphNode> nodes = new()
        {
            root
        };
        nodes.AddRange(children);

        List<IGraphEdge> edges = new();
        foreach (SampleNode child in children)
        {
            edges.Add(new SampleEdge(root, child, "spawn", VisualEdgeEffect.Activate));
        }

        IReadOnlyList<INodeGroup> groups =
            [new SampleGroup("Fan-Out Test", nodes.ToArray())];

        return (nodes, edges, groups);
    }

    /// <summary>
    ///     One hub node with out-degree ~20, each spoke carrying a label. Stresses port
    ///     crowding (Pass 3) and label collisions (Pass 6).
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildHighDegreeHub()
    {
        const int SpokeCount = 20;
        SampleNode hub = new("Hub", true, true, "fanout");
        List<SampleNode> spokes = new();
        for (int i = 0; i < SpokeCount; i++)
        {
            spokes.Add(new SampleNode($"Spoke_{i:D2}", isActive: i % 2 == 0,
                displayValue: i.ToString(CultureInfo.InvariantCulture)));
        }

        List<IGraphNode> nodes = new()
        {
            hub
        };
        nodes.AddRange(spokes);

        List<IGraphEdge> edges = new();
        for (int i = 0; i < spokes.Count; i++)
        {
            edges.Add(new SampleEdge(hub, spokes[i],
                $"emit_{i:D2}", VisualEdgeEffect.Activate, $"idx == {i}"));
        }

        return (nodes, edges, [new SampleGroup("Hub Test", nodes.ToArray())]);
    }

    /// <summary>A→B→C→...→J linear chain. Tests multi-layer routing.</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildLongChain()
    {
        List<SampleNode> chainNodes = new();
        for (int i = 0; i < 10; i++)
        {
            chainNodes.Add(new SampleNode($"Step_{(char)('A' + i)}",
                i == 0, i <= 5,
                i <= 5 ? "done" : null));
        }

        List<IGraphEdge> edges = new();
        for (int i = 0; i < chainNodes.Count - 1; i++)
        {
            edges.Add(new SampleEdge(chainNodes[i], chainNodes[i + 1],
                "next", VisualEdgeEffect.Activate));
        }

        // Add a cross-chain edge to create a multi-layer skip
        edges.Add(new SampleEdge(chainNodes[0], chainNodes[^1],
            "shortcut", VisualEdgeEffect.SetValue, "skip all"));

        return (chainNodes.Cast<IGraphNode>().ToList(), edges, []);
    }

    /// <summary>
    ///     A small lifecycle graph plus three stacked tables that share column targets.
    ///     Stresses table placement against graph bounds and column-edge channel stacking
    ///     (the TablePlacementPass anchors tables to the real graph content bbox).
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups, IReadOnlyList<INodeTable> Tables) BuildMultiTableStack()
    {
        SampleNode root = new("Root", true, true);
        SampleNode roundActive = new("RoundActive", isActive: true);
        SampleNode roundNumber = new("RoundNumber", isActive: true, displayValue: "5");

        IReadOnlyList<IGraphNode> nodes = [root, roundActive, roundNumber];
        IReadOnlyList<IGraphEdge> edges =
        [
            new SampleEdge(root, roundActive, "round_freeze_end", VisualEdgeEffect.Activate),
            new SampleEdge(roundActive, roundNumber, "round_freeze_end", VisualEdgeEffect.SetValue)
        ];

        IReadOnlyList<INodeGroup> groups =
            [new SampleGroup("Lifecycle", [root, roundActive, roundNumber])];

        List<INodeTable> tables = new();
        string[] tableNames = ["Per-Round", "Per-Game", "Per-Half"];
        for (int t = 0; t < 3; t++)
        {
            string[] columns = ["Kills", "Deaths", "Damage", "Score"];
            List<ITableRow> rows = new();
            for (int p = 0; p < 5; p++)
            {
                List<ITableCell> cells = new();
                for (int c = 0; c < columns.Length; c++)
                {
                    cells.Add(new SampleTableCell
                    {
                        IsActive = c < 2,
                        DisplayValue = ((p + c + t) % 7).ToString(CultureInfo.InvariantCulture)
                    });
                }

                rows.Add(new SampleTableRow
                {
                    Label = $"{tableNames[t]}_P{p}",
                    FilterAnnotation = $"slot == {p}",
                    Cells = cells
                });
            }

            // Shared column targets: every table fans edges from roundActive into
            // columns 0 and 2, so the channel router has to deconflict overlaps.
            IReadOnlyList<ITableColumnEdge> columnEdges =
            [
                new SampleColumnEdge(roundActive, 0, "player_death", VisualEdgeEffect.SetValue, "attacker == player"),
                new SampleColumnEdge(roundActive, 2, "player_hurt", VisualEdgeEffect.SetValue, "attacker == player"),
                new SampleColumnEdge(root, 3, "round_freeze_end", VisualEdgeEffect.Activate)
            ];
            tables.Add(new SampleTable
            {
                ColumnNames = columns,
                Rows = rows,
                ColumnEdges = columnEdges
            });
        }

        return (nodes, edges, groups, tables);
    }

    /// <summary>
    ///     Several nodes each carrying 1–2 self-loops. Stresses self-loop overlap:
    ///     the v1 renderer only ever emits one loop per node and never checks collisions.
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildSelfLoopHeavy()
    {
        SampleNode a = new("Idle", true, true);
        SampleNode b = new("Running", isActive: true, displayValue: "tick");
        SampleNode c = new("Paused", isActive: false);
        SampleNode d = new("Stopped", isActive: false, displayValue: "end");

        IReadOnlyList<IGraphNode> nodes = [a, b, c, d];
        IReadOnlyList<IGraphEdge> edges =
        [
            new SampleEdge(a, b, "start", VisualEdgeEffect.Activate),
            new SampleEdge(b, c, "pause", VisualEdgeEffect.Deactivate),
            new SampleEdge(c, d, "stop", VisualEdgeEffect.Deactivate),
            // Self-loops — two on the same node to exercise the dropped-second-loop bug.
            new SampleEdge(a, a, "wait", VisualEdgeEffect.SetValue, "no input"),
            new SampleEdge(b, b, "tick", VisualEdgeEffect.SetValue, "frame"),
            new SampleEdge(b, b, "poll", VisualEdgeEffect.Disjunction, "events"),
            new SampleEdge(c, c, "hold", VisualEdgeEffect.Deactivate)
        ];

        return (nodes, edges, []);
    }

    /// <summary>
    ///     A→B→…→J linear chain + a Step_A→Step_J skip edge, with NO subtitles or
    ///     displayValues so every node shares one y-band. The skip edge then provably
    ///     clips the intermediate node boxes — the unambiguous routing-obstacle fixture
    ///     (LongChain only "misses by luck" because subtitles shift the y-bands).
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildSkipChainNoSubtitle()
    {
        List<SampleNode> chainNodes = new();
        for (int i = 0; i < 10; i++)
        {
            chainNodes.Add(new SampleNode($"Step_{(char)('A' + i)}",
                i == 0, true));
        }

        List<IGraphEdge> edges = new();
        for (int i = 0; i < chainNodes.Count - 1; i++)
        {
            edges.Add(new SampleEdge(chainNodes[i], chainNodes[i + 1],
                "next", VisualEdgeEffect.Activate));
        }

        edges.Add(new SampleEdge(chainNodes[0], chainNodes[^1],
            "shortcut", VisualEdgeEffect.SetValue, "skip all"));

        return (chainNodes.Cast<IGraphNode>().ToList(), edges, []);
    }
}
