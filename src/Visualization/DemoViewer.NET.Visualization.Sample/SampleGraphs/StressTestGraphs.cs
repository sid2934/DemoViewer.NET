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

    /// <summary>
    ///     The graph the Analysis tab actually draws: <b>434 nodes, 297 edges</b>, being the 61
    ///     game-scope scaffolding nodes plus ONE materialised player's 373. Those counts are measured,
    ///     not invented, and reproduce on the reference demo with the fifteen shipped rulesets
    ///     (docs/rule-graph/design.md §0.3).
    ///     <para>
    ///         <b>Why this exists when BuildBigStandard already stresses layout.</b> BigStandard is 120
    ///         nodes of random forward wiring carrying the condition label <c>"cond"</c>. The shipped
    ///         graph is three and a half times larger, its bulk is ONE repeated per-player subtree rather
    ///         than noise, laid out wide and shallow rather than as a chain, and its predicates read like
    ///         <c>event.KillerSlot == player.slot &amp;&amp; event.Weapon.IsRifle</c>. A four-character
    ///         label cannot overlap anything, so the label-overlap gate has been passing on a corpus that
    ///         does not contain the problem issue #4 is about.
    ///     </para>
    ///     <para>
    ///         <b>The counts are measured; the topology is a model.</b> 434/297, the 61/373 split and
    ///         the predicate text all come from the real graph. How those nodes are WIRED is not
    ///         recovered from it: the per-player half is drawn as one template root over a broad,
    ///         shallow fan because that is what "373 nodes of identical shape" means, and the
    ///         scaffolding as a root over enrichments and contexts. So read EdgeCrossings and
    ///         AspectRatio here as properties of that model, not as a measurement of the shipped
    ///         graph, and do not tune against them without grounding the wiring first.
    ///     </para>
    /// </summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) BuildShippedScale()
    {
        const int ScaffoldingCount = 61;
        const int PerPlayerCount = 373;
        const int TargetEdges = 297;

        // Deterministic, like every other fixture here: a baseline that moves on its own measures
        // nothing. The seed is arbitrary; the SHAPE below is not.
        Random rng = new(4340297);

        // Predicates sampled from the shipped rulesets. Length is the point: these are what crowds an
        // edge, and the shortest of them is still six times "cond".
        string[] predicates =
        [
            "event.KillerSlot == player.slot",
            "event.KillerSlot == player.slot && event.Weapon.IsRifle",
            "event.VictimSlot == player.slot && round.IsLive",
            "player.entity.health > 0 && player.entity.armor > 0",
            "event.Headshot && event.Distance > 1500",
            "round.Phase == live && player.IsAlive && enemy.VisibleCount >= 2"
        ];

        List<SampleNode> scaffolding = new();
        for (int i = 0; i < ScaffoldingCount; i++)
        {
            scaffolding.Add(new SampleNode($"game.{i:D2}", i == 0, i % 5 != 0,
                i % 4 == 0 ? (i % 13).ToString(CultureInfo.InvariantCulture) : null,
                i % 9 == 0 ? "ConjunctionNode" : null));
        }

        // The per-player subtree: one template materialised once. Wide and shallow, which is what makes
        // it unreadable at ten copies and why §5 wants it drawn as a template with a slot selector.
        List<SampleNode> perPlayer = new();
        for (int i = 0; i < PerPlayerCount; i++)
        {
            perPlayer.Add(new SampleNode($"p0.{i:D3}", false, i % 3 != 0,
                i % 6 == 0 ? (i % 7).ToString(CultureInfo.InvariantCulture) : null,
                i % 12 == 0 ? "PerPlayerTemplate" : null));
        }

        List<SampleNode> all = [.. scaffolding, .. perPlayer];
        HashSet<(int, int)> seen = new();
        List<IGraphEdge> edges = new();

        void Connect(int s, int d, string label)
        {
            if (s == d || !seen.Add((s, d)))
            {
                return;
            }

            // A quarter of the shipped graph's edges carry a predicate. The rest are plain transitions.
            string? condition = edges.Count % 4 == 0 ? predicates[edges.Count / 4 % predicates.Length] : null;
            edges.Add(new SampleEdge(all[s], all[d], label,
                (VisualEdgeEffect)(edges.Count % 5), condition));
        }

        // LAYERED, not chained. Both halves are wide and shallow: the scaffolding is a root over
        // enrichments and contexts, and the per-player template is one root over a broad fan of stat
        // nodes. Chaining them instead lays 434 nodes into 434 Sugiyama layers and reports an aspect
        // ratio of 360 against 5.92 for BigStandard, which measures the fixture rather than the graph.
        const int ScaffoldingLayers = 6;
        const int PerPlayerLayers = 5;

        static void Layer(int offset, int count, int layers, Action<int, int> connect)
        {
            int width = (count + layers - 1) / layers;
            for (int i = width; i < count; i++)
            {
                // Each node hangs off one in the layer above, fanning out rather than queueing up.
                connect(offset + ((i - width) / 2), offset + i);
            }
        }

        Layer(0, ScaffoldingCount, ScaffoldingLayers, (s, d) => Connect(s, d, "scope"));
        Layer(ScaffoldingCount, PerPlayerCount, PerPlayerLayers, (s, d) => Connect(s, d, "stat"));

        // The join the graph fix added: scaffolding feeds the player's nodes. This is the crossing
        // pressure that the 120-node corpus has no equivalent of.
        int guard = 0;
        while (edges.Count < TargetEdges && guard++ < 40_000)
        {
            int s = rng.Next(ScaffoldingCount);
            int d = ScaffoldingCount + rng.Next(PerPlayerCount);
            Connect(s, d, $"bind{s}");
        }

        List<INodeGroup> groups =
        [
            new SampleGroup("Game scope", [.. scaffolding.Cast<IGraphNode>()]),
            new SampleGroup("Player slot 0", [.. perPlayer.Cast<IGraphNode>()])
        ];

        return ([.. all.Cast<IGraphNode>()], edges, groups);
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
