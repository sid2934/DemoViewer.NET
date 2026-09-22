#region

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Views.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Rule Workbench canvas as the author meets it: a pane beside the YAML rather than over it,
///     drawn from the DOCUMENT, marked where it cannot be edited, synced to the caret, and reconciled
///     instead of rebuilt.
///     <para>
///         <b>Each test here is a gate on a measured defect of the canvas this one replaces.</b> The
///         old canvas was a <c>ColumnSpan=3</c> overlay, drew <c>AuthoringGraph</c>'s four lifecycle
///         hubs and 108 out-degree-zero nodes, carried no back-reference to the YAML so no node could
///         be jumped to, and cleared its collection on every committed keystroke at 2.5 to 3.2 ms a
///         node. design.md §6.7 has the numbers; these are the assertions.
///     </para>
/// </summary>
// Drives the VM through the rules directories, which are environment-scoped.
[NotInParallel]
public class RuleWorkbenchCanvasTests
{
    /// <summary>
    ///     A three-stat ruleset with one value reference, small enough that a render is instant and
    ///     structured enough to have an edge and a chip.
    /// </summary>
    private const string Probe =
        "ruleset: canvas_probe\n"
        + "for: each_player\n"
        + "stats:\n"
        + "  kills:\n"
        + "    count: kill\n"
        + "    match: { enemy: true }\n"
        + "    per: match\n"
        + "  deaths:\n"
        + "    count: death\n"
        + "    per: match\n"
        + "  kd:\n"
        + "    compute: \"kills / max(deaths, 1)\"\n"
        + "    per: match\n"
        + "show:\n"
        + "  scoreboard:\n"
        + "    - { stat: kd, label: KD, group: game }\n";

    /// <summary>
    ///     The canvas draws the document's own entries and none of the engine scaffolding the
    ///     authoring graph anchored everything on.
    ///     <para>
    ///         In <c>aim_rating</c> the 17 <c>compute:</c> stats are the board columns the file exists
    ///         to produce, and <c>AuthoringGraph</c> drew all 17 of them with no edge at all while 30 of
    ///         its 36 wires went to gates every stat in the file shares.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheCanvas_DrawsTheDocument_AndNotTheEnginesScaffolding()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.SelectedFile = Shipped(vm, "aim_rating.rules.yaml");
            vm.ShowGraph = true;
            await Settled(vm);

            string[] titles = [.. vm.RulesetNodes.Select(n => n.Title)];
            Console.WriteLine($"[canvas] aim_rating nodes={titles.Length} wires={vm.RulesetConnections.Count}");

            await Assert.That(titles).Contains("accuracy_pct")
                .Because("a compute: stat is a board column and is one of the document's own entries");
            await Assert.That(titles.Intersect(["Root", "MatchLive", "RoundActive", "BombWasPlanted"]).Count())
                .IsEqualTo(0)
                .Because("57% of AuthoringGraph's edges left these four, and no stats: entry owns any of them");
            await Assert.That(titles.Any(t => t.StartsWith("enrich.", StringComparison.Ordinal))).IsFalse()
                .Because("an enrichment is engine scaffolding with nowhere to send an edit");
            await Assert.That(vm.RulesetConnections.Count).IsGreaterThan(0)
                .Because("the value references between sibling stats are what this canvas draws");
            await Assert.That(vm.GraphSummary).Contains("value reference(s)");
        });
    }

    /// <summary>
    ///     A node the open file does not declare is marked read-only, and by more than a colour: it is
    ///     titled with the ruleset that owns it and says so on its second line.
    /// </summary>
    [Test]
    public async Task ADependencysNodes_AreMarkedReadOnly_WithoutRelyingOnColour()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.SelectedFile = Shipped(vm, "player_stats.rules.yaml");
            vm.ShowGraph = true;
            await Settled(vm);

            RulesetGraphNode[] projected = [.. vm.RulesetNodes.Where(n => !n.IsEditable)];
            Console.WriteLine($"[canvas] player_stats own={vm.RulesetNodes.Count - projected.Length} "
                              + $"projected={projected.Length}");

            await Assert.That(vm.RulesetNodes.Any(n => n.IsEditable)).IsTrue()
                .Because("player_stats declares 58 stats of its own");
            await Assert.That(projected.Length).IsGreaterThan(0)
                .Because("player_stats declares use: [ kast ], whose stats compose in read-only");
            await Assert.That(projected.All(n => n.Title.Contains('.', StringComparison.Ordinal))).IsTrue()
                .Because("a node another ruleset declares is titled ruleset.id, which is what an author "
                         + "would have to write to read it and is the marking a greyscale shot keeps");
            await Assert.That(projected.All(n => n.Subtitle?.Contains("declared in", StringComparison.Ordinal) == true))
                .IsTrue()
                .Because("the node face names the ruleset it came from in words, not only in a tint");
        });
    }

    /// <summary>
    ///     A trigger's wire events are chips on the node, never wires between nodes. Promoting them is
    ///     what made <c>while: round.active</c> a 21-way hub in the graph this replaces.
    /// </summary>
    [Test]
    public async Task TriggerEvents_AreChipsOnTheNode_AndNeverConnections()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.SelectedFile = Shipped(vm, "aim_rating.rules.yaml");
            vm.ShowGraph = true;
            await Settled(vm);

            HashSet<string> chips = [];
            foreach (RulesetGraphNode node in vm.RulesetNodes)
            {
                foreach (string chip in Chips(node))
                {
                    chips.Add(chip);
                }
            }

            Console.WriteLine($"[canvas] distinct event chips={chips.Count}");

            await Assert.That(chips.Count).IsGreaterThan(0)
                .Because("an event-triggered stat fires on a concrete wire event and the chip is where it goes");
            await Assert.That(vm.RulesetConnections.Any(c => chips.Contains(c.Label))).IsFalse()
                .Because("an event drawn as a wire is a hub, and every connection here is a sibling read");
        });
    }

    /// <summary>
    ///     Selecting a node reveals the YAML that declares it, through the same event the diagnostics
    ///     list already jumps with. A node the old canvas drew could not do this at all: an
    ///     <c>AuthoringGraphNode</c> carries no back-reference to the document.
    /// </summary>
    [Test]
    public async Task SelectingANode_RevealsTheLineThatDeclaresIt()
    {
        await WithTempRules(async (vm, _) =>
        {
            await OpenProbe(vm);

            int line = 0;
            vm.JumpRequested += (l, _) => line = l;
            vm.SelectedRulesetNode = Node(vm, "kd");

            await Assert.That(line).IsGreaterThan(0).Because("every document node carries a source position");
            string[] text = vm.DocumentText.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            await Assert.That(text[line - 1]).Contains("kd:")
                .Because($"line {line} should be the stats: key the node was built from");
        });
    }

    /// <summary>
    ///     Moving the caret selects the node whose entry it is inside, and that selection does not
    ///     bounce back out as a second caret move.
    /// </summary>
    [Test]
    public async Task MovingTheCaret_SelectsTheEnclosingNode_AndDoesNotBounceBack()
    {
        await WithTempRules(async (vm, _) =>
        {
            await OpenProbe(vm);

            int declaredAt = 0;
            vm.JumpRequested += (l, _) => declaredAt = l;
            vm.SelectedRulesetNode = Node(vm, "deaths");
            await Assert.That(declaredAt).IsGreaterThan(0);

            vm.SelectedRulesetNode = null;
            int jumps = 0;
            vm.JumpRequested += (_, _) => jumps++;

            // One line into the entry, which is where a caret sits while its body is being edited.
            vm.SelectNodeAtLine(declaredAt + 1);

            await Assert.That(vm.SelectedRulesetNode?.Title).IsEqualTo("deaths")
                .Because("the nearest stats: key at or above the caret is the entry the caret is inside");
            await Assert.That(jumps).IsEqualTo(0)
                .Because("a selection the caret asked for must not answer by moving the caret again");
        });
    }

    /// <summary>
    ///     A caret above every entry selects nothing rather than the first one. A node carries the
    ///     position its key starts at and not the span it covers, so the prose at the top of a ruleset
    ///     would otherwise read as belonging to whichever stat happens to be declared first.
    /// </summary>
    [Test]
    public async Task ACaretAboveEveryEntry_SelectsNothing()
    {
        await WithTempRules(async (vm, _) =>
        {
            await OpenProbe(vm);
            vm.SelectedRulesetNode = Node(vm, "kills");

            vm.SelectNodeAtLine(1); // `ruleset: canvas_probe`, above every stats: key

            await Assert.That(vm.SelectedRulesetNode?.Title).IsEqualTo("kills")
                .Because("nothing encloses line 1, so the selection is left where the author put it");
        });
    }

    /// <summary>
    ///     A render that changes nothing keeps every node instance, and therefore every realized
    ///     container.
    ///     <para>
    ///         This is the keyed reconcile. The previous code did <c>Clear()</c> then re-add, so a
    ///         committed keystroke tore down and re-realised every container at 2.5 to 3.2 ms each
    ///         (design.md §6.5), which on a 100-node ruleset is about 280 ms of blocked UI thread per
    ///         render.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARenderThatChangesNothing_KeepsEveryNodeInstance()
    {
        await WithTempRules(async (vm, _) =>
        {
            await OpenProbe(vm);
            RulesetGraphNode[] before = [.. vm.RulesetNodes];

            vm.RenderGraphForOpenFile();
            await Settled(vm, before.Length);

            await Assert.That(vm.RulesetNodes.Count).IsEqualTo(before.Length);
            for (int i = 0; i < before.Length; i++)
            {
                await Assert.That(ReferenceEquals(vm.RulesetNodes[i], before[i])).IsTrue()
                    .Because($"{before[i].Title} did not change, so its container should not be re-realized");
            }
        });
    }

    /// <summary>
    ///     A field edit keeps the node it edited selected, and re-points the selection at the record
    ///     the edit produced.
    ///     <para>
    ///         design.md §6.7 records this as the one review finding it could not verify: the path from
    ///         <c>ObservableCollection.Clear()</c> to an Avalonia selection reset to a two-way push of
    ///         <c>null</c> was read from the code and never observed, and no test existed. This is that
    ///         test.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AFieldEdit_KeepsTheEditedNodeSelected_AndRefreshesIt()
    {
        await WithTempRules(async (vm, _) =>
        {
            await OpenProbe(vm);
            vm.SelectedRulesetNode = Node(vm, "kd");

            vm.SetNodeFieldCommand.Execute(new RulesetNodeField("label", "Ratio", true));

            for (int i = 0; i < 200 && Node(vm, "kd")?.Subtitle?.Contains("Ratio", StringComparison.Ordinal) != true;
                 i++)
            {
                await Task.Delay(25);
            }

            await Assert.That(vm.DocumentText).Contains("label: Ratio")
                .Because("the edit goes out as a splice into the buffer");
            await Assert.That(vm.SelectedRulesetNode?.Title).IsEqualTo("kd")
                .Because("an author editing a field must not have the node deselected underneath them");
            await Assert.That(vm.SelectedRulesetNode?.Subtitle).Contains("Ratio")
                .Because("the selection follows the record the edit produced, not the one it replaced");
        });
    }

    /// <summary>
    ///     A committed keystroke does not re-render the canvas; the render happens once the typing
    ///     stops.
    ///     <para>
    ///         Only the RENDER is debounced. The check stays synchronous so inline diagnostics track
    ///         the edit, which <c>RuleWorkbench_InvalidBuffer_SurfacesInlineDiagnostic</c> holds.
    ///     </para>
    /// </summary>
    // Needs a dispatcher loop for the deferred render to land on.
    [Category("Render")]
    [Test]
    public async Task ACommittedKeystroke_DefersTheRender_AndTheCanvasCatchesUp()
    {
        await WithTempRules(async (vm, _) =>
        {
            await OpenProbe(vm);
            int before = vm.GraphNodeCount;

            await HeadlessSession.RunOnUi(async () =>
            {
                vm.DocumentText = Probe.Replace(
                    "show:\n", "  deaths_again:\n    count: death\n    per: match\nshow:\n",
                    StringComparison.Ordinal);

                await Assert.That(vm.GraphNodeCount).IsEqualTo(before)
                    .Because("re-realizing every container per keystroke costs 2.5 to 3.2 ms a node");

                for (int i = 0; i < 120 && vm.GraphNodeCount == before; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(25);
                }

                Console.WriteLine($"[canvas-debounce] {before} -> {vm.GraphNodeCount}");
                await Assert.That(vm.GraphNodeCount).IsEqualTo(before + 1)
                    .Because("the canvas follows the buffer, one idle beat behind it");
            });
        });
    }

    /// <summary>
    ///     The canvas and the editor share the row. Switching the canvas on takes width from the
    ///     editor; it does not cover it.
    ///     <para>
    ///         The canvas used to be a <c>Border</c> at <c>Grid.Column="0" Grid.ColumnSpan="3"</c> with
    ///         an opaque background, so the two surfaces were alternating full-screen modes, which is
    ///         the opposite of the co-equal panes §9 decision 8 asks for.
    ///     </para>
    /// </summary>
    // Boots a window: out of the fast tier, which TestTierContractTests holds for the whole suite.
    [Category("Render")]
    [Test]
    public async Task BothPanes_ShareTheRow_RatherThanOneCoveringTheOther()
    {
        await WithTempRules(async (vm, _) =>
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                RuleWorkbenchView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 1200,
                    Height = 800,
                    Content = view
                };

                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    Border editor = Pane(view, "EditorPane");
                    Border canvas = Pane(view, "CanvasPane");

                    await Assert.That(canvas.IsVisible).IsFalse().Because("the toggle starts off");
                    double alone = editor.Bounds.Width;
                    Console.WriteLine($"[canvas-layout] editor alone={alone:F0}");
                    await Assert.That(alone).IsGreaterThan(600)
                        .Because("with the canvas off its column collapses instead of holding space");

                    vm.ShowGraph = true;
                    Dispatcher.UIThread.RunJobs();

                    Console.WriteLine($"[canvas-layout] editor={editor.Bounds.Width:F0} "
                                      + $"canvas={canvas.Bounds.Width:F0}");
                    await Assert.That(canvas.Bounds.Width).IsGreaterThan(0)
                        .Because("the canvas is a pane of its own");
                    await Assert.That(editor.Bounds.Width).IsGreaterThan(0)
                        .Because("the YAML pane stays on screen, which an overlay did not allow");
                    await Assert.That(editor.Bounds.Width).IsLessThan(alone)
                        .Because("the canvas takes width from the editor rather than covering it");
                    await Assert.That(editor.Bounds.Right).IsLessThanOrEqualTo(canvas.Bounds.Left + 1)
                        .Because("side by side, not stacked");
                }
                finally
                {
                    window.Close();
                }
            });
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string[] Chips(RulesetGraphNode node) =>
        (string[])EventChipsConverter.Instance.Convert(node.Value, typeof(string[]), null,
            CultureInfo.InvariantCulture);

    private static RulesetGraphNode? Node(RuleWorkbenchTabViewModel vm, string title) =>
        vm.RulesetNodes.FirstOrDefault(n => string.Equals(n.Title, title, StringComparison.Ordinal));

    private static Border Pane(RuleWorkbenchView view, string name) =>
        view.FindControl<Border>(name)
        ?? throw new InvalidOperationException($"the Workbench XAML has no {name}");

    /// <summary>Writes the probe ruleset into the open draft and waits for its canvas.</summary>
    private static async Task OpenProbe(RuleWorkbenchTabViewModel vm)
    {
        vm.NewFileCommand.Execute(null);
        vm.DocumentText = Probe;
        vm.SaveCommand.Execute(null);
        vm.ShowGraph = true;
        await Settled(vm, 3);
    }

    /// <summary>
    ///     Waits for the MSAGL layout the canvas is projected from. It runs off-thread inside
    ///     <c>SetGraphAsync</c> and the collections are filled on the continuation, so there is nothing
    ///     synchronous to await from here.
    /// </summary>
    private static async Task Settled(RuleWorkbenchTabViewModel vm, int expected = 1)
    {
        for (int i = 0; i < 400 && vm.RulesetNodes.Count < expected; i++)
        {
            await Task.Delay(25);
        }

        if (vm.RulesetNodes.Count < expected)
        {
            throw new InvalidOperationException(
                $"the canvas drew {vm.RulesetNodes.Count} node(s), expected at least {expected}: {vm.GraphSummary}");
        }
    }

    private static RulesetFileRef Shipped(RuleWorkbenchTabViewModel vm, string fileName) =>
        vm.OpenableFiles.FirstOrDefault(f => f.FullPath.EndsWith(fileName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"shipped ruleset {fileName} not found in OpenableFiles");

    private static async Task WithTempRules(Func<RuleWorkbenchTabViewModel, string, Task> body)
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "rules");
        string userDir = Path.Combine(Path.GetTempPath(), "dvwbc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        string? prevRules = Environment.GetEnvironmentVariable("DEMOVIEWER_RULES_DIR");
        string? prevUser = Environment.GetEnvironmentVariable("DEMOVIEWER_USER_RULES_DIR");
        Environment.SetEnvironmentVariable("DEMOVIEWER_RULES_DIR", rulesDir);
        Environment.SetEnvironmentVariable("DEMOVIEWER_USER_RULES_DIR", userDir);

        RuleWorkbenchTabViewModel vm = new();
        try
        {
            await body(vm, userDir);
        }
        finally
        {
            vm.Dispose();
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
}
