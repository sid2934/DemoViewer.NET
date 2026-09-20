#region

using System.Globalization;
using System.Reflection;

#endregion

namespace DemoViewer.NET.Visualization.Sample.SampleGraphs;

/// <summary>
///     The graph the Analysis tab actually draws, captured rather than modelled: 434 nodes and 474
///     edges, with the real wiring, the real event labels and the real predicate text.
///     <para>
///         Every other fixture in this corpus is synthetic, and that is fine for the shapes they
///         test. This one exists because the readability pass (issue #4) tunes layout against
///         measurements, and a topology invented to have roughly the right node count measures the
///         invention. The first cut of it did exactly that: a modelled two-tier fan reported
///         <c>EdgeCrossings = 0</c> where the capture reports a five-figure number, so crossings and
///         separations could not have been tuned against it.
///     </para>
///     <para>
///         <b>How <c>shipped-scale.tsv</c> was captured.</b> A throwaway console project referencing
///         <c>CS2DemoKit.Parser</c> and <c>CS2DemoKit.Analysis</c> 0.12.0 loads <c>rules/</c> with
///         <c>YamlConfigLoader.TryLoadDirectory</c>, builds with
///         <c>DemoAnalysis.Build(demo, rulesets, new AnalysisOptions { VisibilityEngine })</c> over
///         <c>demos/pro/furia-vs-vitality-m1-mirage.dem</c> with the <c>de_mirage</c> collision bake
///         loaded, evaluates with <c>DemoAnalysis.Evaluate</c>, then reproduces
///         <c>AnalysisViewModel.BuildGraphViewModels</c> exactly: <c>build.Nodes</c> plus the lowest
///         materialised slot's <c>MaterializedPlayer.Nodes</c>, then <c>build.Edges</c> plus that
///         player's <c>EdgeDescriptors</c>, then the runtime <c>StateEdge</c> pass that recovers the
///         enrichment wiring descriptors never carry. Re-run it against a newer engine or a different
///         demo and the numbers move; that is the point of writing down how.
///     </para>
///     <para>
///         <b>474 edges, not the 297 in the design doc.</b> 43 + 254 = 297 counts graph edge
///         DESCRIPTORS, which cover trigger-backed rule edges only. The drawn graph adds 177 more
///         from the runtime <c>StateEdge</c> pass, which is what took the orphan count from 187 to
///         54. 54 orphans is also what this capture holds, so the reproduction is faithful.
///     </para>
/// </summary>
public static class ShippedScaleGraph
{
    private const string ResourceName =
        "DemoViewer.NET.Visualization.Sample.SampleGraphs.shipped-scale.tsv";

    /// <summary>Materialises the captured graph into the sample node/edge/group types.</summary>
    public static (IReadOnlyList<IGraphNode> Nodes, IReadOnlyList<IGraphEdge> Edges,
        IReadOnlyList<INodeGroup> Groups) Build()
    {
        List<string> labels = new();
        List<string> conditions = new();
        List<SampleNode> nodes = new();
        List<bool> isPerPlayer = new();
        List<IGraphEdge> edges = new();

        foreach (string line in ReadLines())
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t');
            switch (f[0])
            {
                case "L":
                    labels.Add(f[1]);
                    break;
                case "C":
                    conditions.Add(f[1]);
                    break;
                case "N":
                {
                    int flags = int.Parse(f[1], CultureInfo.InvariantCulture);
                    // Subtitle and display value are empty strings in the capture when the node had
                    // none; the interfaces want null, and the renderer draws the two differently.
                    nodes.Add(new SampleNode(f[2],
                        (flags & 1) != 0,
                        // IsActive is not captured: it is a per-tick evaluation state, not topology,
                        // and no layout metric reads it. Nodes carrying a value read as active, which
                        // is what the graph shows after a run.
                        f[4].Length > 0,
                        f[4].Length > 0 ? f[4] : null,
                        f[3].Length > 0 ? f[3] : null));
                    isPerPlayer.Add((flags & 2) != 0);
                    break;
                }

                case "E":
                {
                    int src = int.Parse(f[1], CultureInfo.InvariantCulture);
                    int dst = int.Parse(f[2], CultureInfo.InvariantCulture);
                    int effect = int.Parse(f[3], CultureInfo.InvariantCulture);
                    int label = int.Parse(f[4], CultureInfo.InvariantCulture);
                    int condition = int.Parse(f[5], CultureInfo.InvariantCulture);
                    edges.Add(new SampleEdge(nodes[src], nodes[dst], labels[label],
                        (VisualEdgeEffect)effect,
                        condition >= 0 ? conditions[condition] : null));
                    break;
                }
            }
        }

        List<IGraphNode> gameScope = new();
        List<IGraphNode> playerScope = new();
        for (int i = 0; i < nodes.Count; i++)
        {
            (isPerPlayer[i] ? playerScope : gameScope).Add(nodes[i]);
        }

        List<INodeGroup> groups =
        [
            new SampleGroup("Game scope", gameScope),
            new SampleGroup("Player slot", playerScope)
        ];

        return ([.. nodes.Cast<IGraphNode>()], edges, groups);
    }

    private static IEnumerable<string> ReadLines()
    {
        using Stream stream = typeof(ShippedScaleGraph).GetTypeInfo().Assembly
                                  .GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException(
                                  $"embedded resource missing: {ResourceName}");
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
