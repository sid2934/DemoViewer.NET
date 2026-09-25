#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.Views.RuleWorkbench;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Gates for the Workbench's ruleset state-graph visualization: the shared
///     <see cref="RuleGraphSkeleton" /> conversion produces a non-empty graph from a real build; the VM
///     publishes a node count on Evaluate; and the toggled-on <see cref="RuleWorkbenchView" /> renders the
///     graph. The conversion is the same one the Analysis tab's progressive-reveal pre-render uses.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class RuleWorkbenchGraphTests
{
    /// <summary>The extracted skeleton helper turns a real BuildResult into a non-empty node/edge graph.</summary>
    [Test]
    public async Task Graph_Conversion_ProducesNodesAndEdges()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        List<RulesetDoc> docs = LoadShippedDocs();

        BuildResult build = DemoAnalysis.Build(demo, docs);
        RuleGraphSkeleton.Skeleton skeleton = RuleGraphSkeleton.Build(build);

        Console.WriteLine($"[authoring-graph] nodes={skeleton.Nodes.Count} edges={skeleton.Edges.Count} " +
                          $"groups={skeleton.Groups?.Count ?? 0}");

        await Assert.That(skeleton.Nodes.Count).IsGreaterThan(0)
            .Because("a built ruleset graph has state nodes");

        // `skeleton.Nodes.Count == build.Nodes.Count` used to stand here and is deleted. It could not
        // fail: Build walks build.Nodes and appends one view model per entry with no filter, so the
        // assertion restated its own loop. What it was reaching for is that the conversion is
        // LOSSLESS, and the half that can actually lose something is the edges: Build drops an edge
        // whose source or destination misses the node map on a bare `if`, with nothing counting it
        // (RuleGraphSkeleton.cs:76-83, one of the sites docs/rule-graph/design.md 1.1 tabulates).
        // Equality here is that property, and unlike the node count it can fail.
        //
        // Since 0.13, build.Edges also carries rows drawn to ExternalStateNode stand-ins
        // (player_context, entity_state), which the engine keeps out of build.Nodes by design
        // (BuildResult.ExternalNodes). The skeleton draws build.Nodes only, as it did on 0.12 when
        // those rows did not exist, so they are the one kind of row it leaves out; any other row
        // missing is still the silent loss this guards.
        int graphRows = build.Edges.Count(e => e.Source is not ExternalStateNode
                                               && e.Destination is not ExternalStateNode);
        await Assert.That(skeleton.Edges.Count).IsEqualTo(graphRows)
            .Because("an edge whose endpoint misses the node map is dropped silently, and a node the "
                     + "conversion fails to map takes its edges down with it");

        // Endpoint identity, not just arity: every edge has to be wired to the view models this same
        // conversion produced. A second view model minted for one node keeps both counts right and
        // still leaves the graph drawing edges onto boxes the canvas does not own, which is a real
        // enough shape that AnalysisViewModel.BuildGraphViewModels carries a dedup guard against it.
        HashSet<IGraphNode> converted = new(ReferenceEqualityComparer.Instance);
        foreach (IGraphNode node in skeleton.Nodes)
        {
            converted.Add(node);
        }

        await Assert.That(skeleton.Edges.All(e => converted.Contains(e.Source)
                                                  && converted.Contains(e.Destination))).IsTrue()
            .Because("an edge endpoint that is not one of these nodes is a duplicate view model");

        // The skeleton is the PRE-evaluation graph, which is the one thing separating it from the
        // live post-evaluation render that shares this file. A real snapshot column here means the
        // shared conversion started reading evaluation state, and the Analysis tab's progressive
        // reveal would have nothing left to reveal. The arity check first, or an OfType that matched
        // nothing would make the All below pass over an empty sequence.
        GraphNodeViewModel[] concrete = [.. skeleton.Nodes.OfType<GraphNodeViewModel>()];
        await Assert.That(concrete.Length).IsEqualTo(skeleton.Nodes.Count)
            .Because("these are this app's view models, and a partial match makes the next line vacuous");
        await Assert.That(concrete.All(n => n.TrackedIndex == -1)).IsTrue()
            .Because("TrackedIndex is wired by the post-evaluation path, never by the skeleton");

        // The Analysis tab's post-evaluation graph is a different path and carries per-player nodes
        // too; nothing in this file exercises that, by design.
        await Assert.That(skeleton.Nodes.Any(n => n.IsRoot)).IsTrue()
            .Because("the scaffolding is anchored on a root node");
        // round_facts is `for: each_team`, so the engine builds each of its states, and the per-team
        // aggregates it reads, once per side under the same name (the copies carry a CT / T subtitle,
        // except money_reliable, which carries none). Names stopped being unique when it shipped
        // (142 nodes, 107 names); what still holds is that a repeated name is exactly one copy per
        // side, and that round_facts is the only ruleset that does it.
        List<IGrouping<string, GraphNodeViewModel>> repeated = [.. concrete.GroupBy(n => n.Name).Where(g => g.Count() > 1)];
        HashSet<string> roundFacts = [.. DemoAnalysis.Build(demo, [.. docs.Where(d => d.Id == RoundFactsFingerprint.RulesetId)])
            .Nodes.Where(n => n is not CS2DemoKit.Analysis.Abstractions.RootNode).Select(n => n.Name)];
        await Assert.That(repeated.All(g => g.Count() == 2)).IsTrue()
            .Because("game-scope names are unique; it is only per-player copies, and a per-team state's one copy per side, that repeat a name");
        await Assert.That(repeated.All(g => roundFacts.Contains(g.Key))).IsTrue()
            .Because("every repeated name is one round_facts builds on its own");
        await Assert.That(skeleton.Edges.Count).IsGreaterThan(0)
            .Because("the rule chain connects nodes with edges");
        await Assert.That(skeleton.Nodes.All(n => !string.IsNullOrEmpty(n.Name))).IsTrue()
            .Because("every node carries its rule/state name");
    }

    /// <summary>
    ///     The graph builds structurally from the OPEN ruleset with NO demo and NO evaluation
    ///     (a review fix): toggling it on publishes the node count, and a simple 1-stat ruleset is far smaller than
    ///     the whole shipped corpus. It reflects the open selection, not "all rulesets".
    /// </summary>
    [Test]
    public async Task Graph_DemoLess_FromOpenRuleset_ReflectsSelection()
    {
        await WithTempRules(async (vm, _) =>
        {
            // NO OnActivated(demo), NO Evaluate, just open a simple ruleset and toggle the graph on.
            vm.NewFileCommand.Execute(null); // starter template: a single kill stat
            vm.ShowGraph = true;

            Console.WriteLine($"[authoring-graph-vm] {vm.GraphSummary}");
            await Assert.That(vm.GraphNodeCount).IsGreaterThan(0)
                .Because("the graph builds structurally from the open ruleset without a demo");
            await Assert.That(vm.GraphNodeCount).IsLessThanOrEqualTo(6)
                .Because("a bare kill stat reduces to its declared output + upstream inputs (a handful of "
                         + "nodes), NOT the 61 shared-scaffolding nodes of the full engine graph");
            await Assert.That(vm.GraphSupported).IsTrue();
        });
    }

    /// <summary>
    ///     An entity-reading ruleset graphs without a demo too: per-player nodes materialise during
    ///     evaluation, so the build needs no scanner. The summary says nothing about a demo.
    /// </summary>
    [Test]
    public async Task Graph_EntityRuleset_NoDemo_Renders()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.SelectedFile = RequireShipped(vm, "player_stats.rules.yaml");
            vm.ShowGraph = true;

            Console.WriteLine($"[graph-entity-nodemo] {vm.GraphSummary}");
            await Assert.That(vm.GraphNodeCount).IsGreaterThan(0);
            await Assert.That(vm.GraphSummary).DoesNotContain("with demo");
        });
    }

    /// <summary>With a demo bound, the same entity-reading ruleset graphs (the scanner exists).</summary>
    [Test]
    public async Task Graph_EntityRuleset_WithDemo_Renders()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.OnActivated(new FakeDemoContext(demo)); // bind the first-party demo source
            vm.SelectedFile = RequireShipped(vm, "player_stats.rules.yaml");
            vm.ShowGraph = true;

            Console.WriteLine($"[graph-entity-demo] {vm.GraphSummary}");
            await Assert.That(vm.GraphNodeCount).IsGreaterThan(0);
            await Assert.That(vm.GraphSummary).Contains("with demo")
                .Because("a bound demo supplies the tick rate and profile the graph resolves against");
        });
    }

    private static RulesetFileRef RequireShipped(RuleWorkbenchTabViewModel vm, string fileName) =>
        vm.OpenableFiles.FirstOrDefault(f => f.FullPath.EndsWith(fileName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"shipped ruleset {fileName} not found in OpenableFiles");

    /// <summary>With the graph on but nothing open, the graph is empty (no fallback to "all rulesets").</summary>
    [Test]
    public async Task Graph_NoOpenFile_IsEmpty()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.ShowGraph = true; // nothing selected
            await Assert.That(vm.GraphNodeCount).IsEqualTo(0);
            await Assert.That(vm.GraphSummary).Contains("Open a ruleset");
        });
    }

    /// <summary>The toggled-on graph overlay renders (non-blank) after the layout completes.</summary>
    [Test]
    public async Task Graph_View_RendersGraph_AfterEvaluate()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        List<RulesetDoc> docs = LoadShippedDocs();
        BuildResult build = DemoAnalysis.Build(demo, docs);
        RuleGraphSkeleton.Skeleton skeleton = RuleGraphSkeleton.Build(build);

        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null);

            await HeadlessSession.RunOnUi(async () =>
            {
                const int Width = 1100, Height = 620;
                RuleWorkbenchView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = Width,
                    Height = Height,
                    Content = view
                };
                window.Show();
                vm.ShowGraph = true;

                // Drive the graph, then render the overlay. MSAGL's layout runs off-thread and does not
                // reliably rasterize in the headless capture window, so we assert the graph PIPELINE
                // deterministically (the overlay renders without crashing + the skeleton built) rather than
                // the async geometry pixels. Visual fidelity is a desktop pass.
                await vm.GraphViewModel.SetGraphAsync(skeleton.Nodes, skeleton.Edges, skeleton.Groups);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                WriteableBitmap? bmp = window.CaptureRenderedFrame();
                await Assert.That(bmp).IsNotNull()
                    .Because("the graph overlay + GraphView render without crashing when toggled on");
                bmp!.Save(Path.Combine(HeadlessSession.ArtifactDir, "ruleworkbench-graph.png"), new PngBitmapEncoderOptions());

                await Assert.That(skeleton.Nodes.Count).IsGreaterThan(0);
                Console.WriteLine($"[authoring-graph-render] skeleton nodes={skeleton.Nodes.Count} edges={skeleton.Edges.Count}");

                window.Close();
            });
        });
    }

    // ── helpers (self-contained; mirror RuleWorkbenchModuleTests so this file is merge-independent) ──

    private static List<RulesetDoc> LoadShippedDocs()
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "rules");
        List<RulesetDoc> docs = [];
        foreach (string path in Directory.EnumerateFiles(rulesDir, "*.rules.yaml"))
        {
            if (RulesetDocumentLoader.Load(File.ReadAllText(path), path).Doc is { } doc)
            {
                docs.Add(doc);
            }
        }

        return docs;
    }

    private static async Task WithTempRules(Func<RuleWorkbenchTabViewModel, string, Task> body)
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "rules");
        string userDir = Path.Combine(Path.GetTempPath(), "dvwbg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        string? prevRules = Environment.GetEnvironmentVariable("DEMOVIEWER_RULES_DIR");
        string? prevUser = Environment.GetEnvironmentVariable("DEMOVIEWER_USER_RULES_DIR");
        Environment.SetEnvironmentVariable("DEMOVIEWER_RULES_DIR", rulesDir);
        Environment.SetEnvironmentVariable("DEMOVIEWER_USER_RULES_DIR", userDir);

        RuleWorkbenchTabViewModel vm = new();
        vm.Dispose(); // stop the file watcher: the test drives the VM directly, deterministically
        try
        {
            await body(vm, userDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEMOVIEWER_RULES_DIR", prevRules);
            Environment.SetEnvironmentVariable("DEMOVIEWER_USER_RULES_DIR", prevUser);
            try
            {
                Directory.Delete(userDir, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    /// <summary>
    ///     A minimal <see cref="IModuleContext" /> carrying a loaded demo via <see cref="ICurrentDemoSource" />,
    ///     only the members the Workbench touches are functional; the rest are unused stubs.
    /// </summary>
    private sealed class FakeDemoContext(ParsedDemo demo) : IModuleContext, ICurrentDemoSource
    {
        public ParsedDemo? CurrentDemo { get; } = demo;
        public IReadOnlyList<PlayerRosterEntry> Players => [];
        public IReadOnlyList<IPlayerState> CurrentPlayers => [];

        public event Action<IPlaybackSnapshot>? Advanced
        {
            add { }
            remove { }
        }

        public bool HasDemo => true;
        public string? DemoPath => null;
        public int TickRate => 64;
        public double CurtimeSeconds(int tick) => 0;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1;

        public void RequestSeekToFrame(int frameIndex)
        {
        }

        public void RequestSeekToTick(int tick)
        {
        }

        public void RequestPlay()
        {
        }

        public void RequestPause()
        {
        }

        public IReadOnlyEntityView Entities => throw new NotSupportedException("unused by the Workbench");
    }
}
