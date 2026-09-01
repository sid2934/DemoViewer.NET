#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Phase 3 gates for the Rulesets v2 authoring Workbench
///     : M0 the module registers a Main-strip
///     "Authoring" tab and its View renders; M1 the in-process demo-less checker reports the shipped
///     rulesets clean; M2 the editor's file lifecycle (New/Save), buffer-aware inline diagnostics, and
///     caret-jump. Filesystem tests pin the rules dirs via the RuleSetLocator env overrides and dispose
///     the VM's watcher for determinism.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class RuleWorkbenchModuleTests
{
    // ── M0 ───────────────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task RuleWorkbenchModule_RegistersAuthoringTab_OnMainStrip()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ModuleRegistry registry = new();
            registry.Register(new RuleWorkbenchModule());
            MainViewModel vm = new(null, registry, TestLibraries.Empty());

            WorkspaceTabDescriptor? tab = vm.Tabs.FirstOrDefault(t => t.TabId == "ruleworkbench.editor");
            await Assert.That(tab).IsNotNull()
                .Because("the Workbench module must contribute its Authoring tab through the registry");
            await Assert.That(tab!.Header).IsEqualTo("Authoring");
            await Assert.That(tab.Placement).IsEqualTo(TabPlacement.Main);
        });
    }

    [Test]
    public async Task RuleWorkbenchModule_ViewModelFactory_BuildsLifecycleVm()
    {
        WorkspaceTabDescriptor tab = new RuleWorkbenchModule().CreateTabs(new FakeHost()).Single();

        // ViewModelFactory (lazy + retained), NOT DataContext, so Activate() drives the lifecycle.
        await Assert.That(tab.DataContext).IsNull();
        await Assert.That(tab.ViewModelFactory is not null).IsTrue();
        IWorkspaceTabViewModel vm = tab.ViewModelFactory!.Invoke();
        await Assert.That(vm).IsTypeOf<RuleWorkbenchTabViewModel>();
        (vm as IDisposable)?.Dispose();
    }

    // ── M1 ───────────────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task RuleWorkbench_Check_ShippedV2Rules_ReportsClean()
    {
        await WithTempRules(async (vm, _) =>
        {
            Console.WriteLine($"[ruleworkbench-m1] {vm.Summary}");
            await Assert.That(vm.Diagnostics.Count).IsEqualTo(0)
                .Because("the shipped v2 rulesets compose demo-less with no diagnostics");
            await Assert.That(vm.IsClean).IsTrue();
            await Assert.That(vm.Summary).Contains("no problems");
        });
    }

    // ── M2 ───────────────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task RuleWorkbench_NewFile_CreatesAndOpensDraft()
    {
        await WithTempRules(async (vm, userDir) =>
        {
            vm.NewFileCommand.Execute(null);

            await Assert.That(vm.SelectedFile).IsNotNull();
            await Assert.That(vm.OpenableFiles.Count(fr => !fr.IsShipped)).IsEqualTo(1);
            await Assert.That(vm.DocumentText).Contains("ruleset:");
            await Assert.That(File.Exists(vm.SelectedFile!.FullPath)).IsTrue();
            await Assert.That(vm.IsClean).IsTrue().Because("the starter template composes clean");
        });
    }

    [Test]
    public async Task RuleWorkbench_Save_WritesBufferAndClearsDirty()
    {
        await WithTempRules(async (vm, userDir) =>
        {
            vm.NewFileCommand.Execute(null);
            string file = vm.SelectedFile!.FullPath;

            vm.DocumentText = "ruleset: saved_probe\nfor: each_player\nstats:\n  k:\n    count: kill\n    per: match\n";
            await Assert.That(vm.IsDirty).IsTrue();

            vm.SaveCommand.Execute(null);
            await Assert.That(vm.IsDirty).IsFalse();
            string onDisk = File.ReadAllText(file);
            await Assert.That(onDisk).Contains("ruleset: saved_probe");
        });
    }

    [Test]
    public async Task RuleWorkbench_Save_StampsCatalogVersion()
    {
        await WithTempRules(async (vm, userDir) =>
        {
            vm.NewFileCommand.Execute(null);
            string file = vm.SelectedFile!.FullPath;
            await Assert.That(vm.DocumentText.Contains("catalog_version")).IsFalse()
                .Because("the starter template is unstamped");

            vm.SaveCommand.Execute(null);

            await Assert.That(vm.DocumentText).Contains("catalog_version:")
                .Because("Save stamps the current catalog version (M7 provenance)");
            string onDisk = File.ReadAllText(file);
            await Assert.That(onDisk).Contains("catalog_version:");
            await Assert.That(vm.IsDirty).IsFalse().Because("stamping does not leave the buffer dirty");
            await Assert.That(vm.IsClean).IsTrue().Because("a stamped starter template still composes clean");
        });
    }

    [Test]
    public async Task ShippedRuleset_IsReadOnly_SaveAsForksToUser()
    {
        await WithTempRules(async (vm, userDir) =>
        {
            RulesetFileRef? shipped = vm.OpenableFiles.FirstOrDefault(f => f.IsShipped);
            await Assert.That(shipped).IsNotNull().Because("shipped rulesets appear in the dropdown");

            vm.SelectedFile = shipped;
            await Assert.That(vm.IsReadOnlyFile).IsTrue().Because("shipped rulesets are read-only without DeveloperMode");
            await Assert.That(vm.CanSave).IsFalse();

            // Save-in-place is a no-op on a read-only file. It must NOT create a user copy.
            vm.DocumentText += "\n# a tweak\n";
            vm.SaveCommand.Execute(null);
            await Assert.That(vm.OpenableFiles.Count(f => !f.IsShipped)).IsEqualTo(0)
                .Because("Save on a read-only shipped file must not write");

            // Save-As forks the buffer to the user overlay and opens the (editable) copy.
            vm.SaveAsName = "my-fork";
            vm.SaveAsCommand.Execute(null);
            await Assert.That(File.Exists(Path.Combine(userDir, "my-fork.rules.yaml"))).IsTrue();
            await Assert.That(vm.SelectedFile!.IsShipped).IsFalse();
            await Assert.That(vm.IsReadOnlyFile).IsFalse().Because("the forked copy is an editable user file");
        });
    }

    [Test]
    public async Task RuleWorkbench_InvalidBuffer_SurfacesInlineDiagnostic()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null);

            // A bogus view name: the checker must reject it, and it must land on THIS file (inline).
            vm.DocumentText = "ruleset: broken_probe\nfor: each_player\nstats:\n  x:\n    count: not_a_real_view\n    per: match\n";

            await Assert.That(vm.IsClean).IsFalse();
            await Assert.That(vm.OpenFileDiagnostics.Count).IsGreaterThan(0)
                .Because("the open file's diagnostics are the inline set");
        });
    }

    [Test]
    public async Task RuleWorkbench_RequestJump_RaisesEventWithPosition()
    {
        await WithTempRules(async (vm, _) =>
        {
            int line = 0, col = 0;
            vm.JumpRequested += (l, c) =>
            {
                line = l;
                col = c;
            };
            vm.RequestJump(new WorkbenchDiagnostic("f(7,4)", "msg", "code", "/f", 7, 4));

            await Assert.That(line).IsEqualTo(7);
            await Assert.That(col).IsEqualTo(4);
        });
    }

    // ── M3 ───────────────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task Completion_Vocabulary_IncludesCatalogTermsAndBufferStats()
    {
        CatalogRoot catalog = CatalogResource.Load();
        const string Buffer = "ruleset: t\nfor: each_player\nstats:\n  my_kills:\n    count: kill\n  my_deaths:\n    count: death\n";

        IReadOnlyList<WorkbenchCompletion> candidates = WorkbenchCompletionSource.Build(catalog, Buffer);
        HashSet<string> texts = candidates.Select(c => c.Text).ToHashSet(StringComparer.Ordinal);

        await Assert.That(texts).Contains("kill").Because("view names are completion candidates");
        await Assert.That(texts).Contains("headshot").Because("facet names are candidates");
        await Assert.That(texts).Contains("floor").Because("the closed function set is offered");
        await Assert.That(texts).Contains("count").Because("stat kinds are offered");
        await Assert.That(texts.Any(t => t.StartsWith("round.", StringComparison.Ordinal))).IsTrue()
            .Because("context v2 paths (round.*) are offered");
        await Assert.That(texts).Contains("my_kills").Because("sibling stats parsed from the buffer are offered");
        await Assert.That(texts).Contains("my_deaths");

        // "count" appears once (as a kind): the 2-space-indent regex must not also capture the
        // 4-space-indented `count:` kind key as a stat.
        await Assert.That(candidates.Count(c => c.Text == "count")).IsEqualTo(1);
        await Assert.That(candidates.First(c => c.Text == "my_kills").Category).IsEqualTo("stat");
    }

    /// <summary>Completion narrows to the RIGHT term type for where the caret sits.</summary>
    [Test]
    public async Task Completion_NarrowsByCursorContext()
    {
        CatalogRoot catalog = CatalogResource.Load();
        const string Buffer = "ruleset: t\nfor: each_player\nstats:\n  my_kills:\n    count: kill\n";

        HashSet<string> Texts(string lineBeforeCaret)
        {
            return WorkbenchCompletionSource
                .Build(catalog, Buffer, WorkbenchCompletionSource.ContextFor(lineBeforeCaret))
                .Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
        }

        // After `per:`, only the closed enum, nothing else.
        HashSet<string> afterPer = Texts("    per: ");
        await Assert.That(afterPer).Contains("round");
        await Assert.That(afterPer).Contains("match");
        await Assert.That(afterPer.Contains("kill")).IsFalse().Because("an event is not a valid `per:` value");
        await Assert.That(afterPer.Contains("count")).IsFalse().Because("a stat kind is not a valid `per:` value");

        // After `count:`, events/facets (the triggers), not contexts or kinds.
        HashSet<string> afterCount = Texts("    count: ");
        await Assert.That(afterCount).Contains("kill").Because("count takes an event/view");
        await Assert.That(afterCount.Any(t => t.StartsWith("round.", StringComparison.Ordinal))).IsFalse()
            .Because("a context path is not a countable event");
        await Assert.That(afterCount.Contains("per")).IsFalse().Because("a modifier is not a count value");

        // After `when:`, the read vocabulary (contexts, functions, facets, sibling stats), not kinds.
        HashSet<string> afterWhen = Texts("    when: ");
        await Assert.That(afterWhen.Any(t => t.StartsWith("round.", StringComparison.Ordinal))).IsTrue()
            .Because("a condition can read a context path");
        await Assert.That(afterWhen).Contains("floor").Because("a condition can call a function");
        await Assert.That(afterWhen).Contains("my_kills").Because("a condition can reference a sibling stat");
        await Assert.That(afterWhen.Contains("count")).IsFalse().Because("a stat kind is not a condition term");

        // At a key position (fresh indented line), kinds + modifiers, not values.
        HashSet<string> atKey = Texts("    ");
        await Assert.That(atKey).Contains("count").Because("a stat body starts with a kind");
        await Assert.That(atKey).Contains("per").Because("or a modifier");
        await Assert.That(atKey.Contains("kill")).IsFalse().Because("an event is a value, not a key");
        await Assert.That(atKey.Any(t => t.StartsWith("round.", StringComparison.Ordinal))).IsFalse();

        // ── The brittle caret shapes that used to fall through to Any (whole vocabulary) ──

        // Trailing text after a completed value token: still the key's value context.
        HashSet<string> trailing = Texts("    count: kill ");
        await Assert.That(trailing.Contains("count")).IsFalse()
            .Because("a trailing space after the value must not widen back to the whole vocabulary");
        await Assert.That(trailing).Contains("kill").Because("still an event-valued position");

        // A value containing spaces: still narrowed to the key.
        HashSet<string> spacedValue = Texts("    when: round.is_pistol and ");
        await Assert.That(spacedValue.Any(t => t.StartsWith("round.", StringComparison.Ordinal))).IsTrue()
            .Because("a compound condition keeps reading the when-vocabulary");
        await Assert.That(spacedValue.Contains("count")).IsFalse()
            .Because("a stat kind is not a condition term, even mid-expression");

        // An inline map narrows on the INNER key, not the outer one and not Any.
        HashSet<string> inlineMap = Texts("    when: { per: ");
        await Assert.That(inlineMap).Contains("round").Because("the inner `per:` owns the caret");
        await Assert.That(inlineMap.Contains("kill")).IsFalse()
            .Because("the outer `when:` vocabulary must not leak into the inner key's enum");
    }

    /// <summary>
    ///     v0.6.0 block-scope: the enclosing top-level section picks the KEY
    ///     vocabulary: show-block keys inside <c>show:</c>, section keys at column 0, stat
    ///     kinds/modifiers inside <c>stats:</c>, while value positions stay line-local.
    /// </summary>
    [Test]
    public async Task Completion_NarrowsByBlockScope()
    {
        CatalogRoot catalog = CatalogResource.Load();
        const string Buffer = "ruleset: t\nfor: each_player\nstats:\n  my_kills:\n    count: kill\nshow:\n  ";

        HashSet<string> Texts(string lineBeforeCaret, string textBeforeCaret)
        {
            return WorkbenchCompletionSource
                .Build(catalog, Buffer, WorkbenchCompletionSource.ContextFor(lineBeforeCaret, textBeforeCaret))
                .Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
        }

        // Key position inside `show:`, containers + entry keys, not stat kinds or events.
        HashSet<string> inShow = Texts("  ", Buffer);
        await Assert.That(inShow).Contains("scoreboard").Because("show: hosts the scoreboard container");
        await Assert.That(inShow).Contains("tables");
        await Assert.That(inShow).Contains("label").Because("entry keys belong to show rows");
        await Assert.That(inShow.Contains("count")).IsFalse().Because("a stat kind is not a show key");
        await Assert.That(inShow.Contains("kill")).IsFalse().Because("an event is not a show key");

        // Key position inside `stats:`, kinds/modifiers, exactly as before block scope existed.
        const string StatsPrefix = "ruleset: t\nstats:\n  my_kills:\n    ";
        HashSet<string> inStats = Texts("    ", StatsPrefix);
        await Assert.That(inStats).Contains("count");
        await Assert.That(inStats.Contains("scoreboard")).IsFalse()
            .Because("show containers must not leak into a stat body");

        // A partial word at COLUMN 0: the top-level section keys, not the whole vocabulary.
        HashSet<string> topLevel = Texts("sh", "ruleset: t\nsh");
        await Assert.That(topLevel).Contains("show");
        await Assert.That(topLevel).Contains("stats");
        await Assert.That(topLevel.Contains("kill")).IsFalse()
            .Because("events are values, never top-level sections");

        // Value positions stay LINE-LOCAL regardless of block: `count:` inside stats still narrows
        // to events even with the full buffer supplied.
        HashSet<string> valueInStats = Texts("    count: ", StatsPrefix + "count: ");
        await Assert.That(valueInStats).Contains("kill");
        await Assert.That(valueInStats.Contains("scoreboard")).IsFalse();
    }

    // ── M4 ───────────────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task DataBrowser_Paths_FromCatalog()
    {
        await WithTempRules(async (vm, _) =>
        {
            await Assert.That(vm.Paths.Count).IsGreaterThan(0);
            await Assert.That(vm.Paths.Any(p => p.Category == "context")).IsTrue()
                .Because("context paths (round.*, match.*) are draggable authoring vocabulary");
            await Assert.That(vm.Paths.Any(p => p.Category == "entity")).IsTrue()
                .Because("entity-read paths are in the palette");
            await Assert.That(vm.Paths.Any(p => p.Path.StartsWith("round.", StringComparison.Ordinal))).IsTrue();
            // No live demo in this VM-only context → the live table is empty (graceful).
            await Assert.That(vm.LivePlayers.Count).IsEqualTo(0);
        });
    }

    // ── M5 ───────────────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task Evaluate_OnLoadedDemo_ProducesGameBoard()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null); // a valid draft with a group: game scoreboard column
            vm.OnActivated(new FakeDemoContext(demo)); // wires the first-party demo source
            await vm.EvaluateCommand.ExecuteAsync(null);

            Console.WriteLine($"[ruleworkbench-m5] {vm.EvalSummary}");
            await Assert.That(vm.Boards.Count).IsGreaterThan(0)
                .Because("evaluating on a loaded demo renders at least the game scoreboard");
            await Assert.That(vm.EvalSummary).Contains("Evaluated");
            WorkbenchScoreboard scoreboard = vm.Boards[0];
            await Assert.That(scoreboard.Rows.Count).IsGreaterThan(0);
            // Regression (multi-ruleset materialization dedup): ONE row per player, not one per ruleset.
            await Assert.That(scoreboard.Rows.Select(r => r.Label).Distinct().Count()).IsEqualTo(scoreboard.Rows.Count)
                .Because("per-player rows must be merged across the shipped rulesets' templates, not duplicated");

            vm.OnDeactivated();
        });
    }

    [Test]
    public async Task Evaluate_DefaultIsOpenRulesetOnly_AdvancedIsMultiselect()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null); // opens a 1-game-column draft
            vm.OnActivated(new FakeDemoContext(demo));

            await vm.EvaluateCommand.ExecuteAsync(null); // DEFAULT: open ruleset only
            await Assert.That(vm.EvalSummary).Contains("the open ruleset");
            await Assert.That(vm.Boards.Count).IsGreaterThan(0);

            // The multiselect lists the shipped + user rulesets.
            await Assert.That(vm.EvaluableFiles.Count).IsGreaterThan(1)
                .Because("Advanced Evaluate offers the shipped + user rulesets");
            foreach (EvaluableFile f in vm.EvaluableFiles)
            {
                f.IsSelected = true;
            }

            await vm.EvaluateAdvancedCommand.ExecuteAsync(null); // ADVANCED: the whole selected set
            await Assert.That(vm.EvalSummary).Contains("selected ruleset");
            await Assert.That(vm.Boards.Count).IsGreaterThan(0);

            vm.OnDeactivated();
        });
    }

    [Test]
    public async Task Evaluate_RendersScoreboardAndTables()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.OnActivated(new FakeDemoContext(demo)); // no file open → EvaluableFiles = the shipped set
            foreach (EvaluableFile f in vm.EvaluableFiles)
            {
                f.IsSelected = true;
            }

            await vm.EvaluateAdvancedCommand.ExecuteAsync(null); // evaluate all shipped rulesets

            // Beyond the scoreboard, the shipped rulesets declare tables: (kast_game_totals) and keyed
            // buckets (weapon), so more than one board renders.
            await Assert.That(vm.Boards.Count).IsGreaterThan(1)
                .Because("scoreboard + declared/keyed tables all render");
            WorkbenchScoreboard board = vm.Boards[0];
            await Assert.That(board.Columns.Count).IsGreaterThan(0);
            await Assert.That(board.Rows.Count).IsGreaterThan(0);
            await Assert.That(board.Rows.All(r => r.Cells.Count == board.Columns.Count)).IsTrue()
                .Because("each row's cells must align to the header columns");

            vm.OnDeactivated();
        });
    }

    [Test]
    public async Task Evaluate_NoDemo_ReportsGracefully()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null);
            // No OnActivated with a demo source → CurrentDemo null.
            await vm.EvaluateCommand.ExecuteAsync(null);

            await Assert.That(vm.Boards.Count).IsEqualTo(0);
            await Assert.That(vm.EvalSummary).Contains("No demo");
        });
    }

    /// <summary>
    ///     Only a single evaluation runs at a time: while one is in flight (
    ///     <see cref="RuleWorkbenchTabViewModel.IsEvaluating" />),
    ///     BOTH Evaluate and Advanced Evaluate are disabled, per the shared single-flight guard.
    /// </summary>
    [Test]
    public async Task Evaluate_SingleFlight_DisablesBothCommandsWhileRunning()
    {
        await WithTempRules(async (vm, _) =>
        {
            await Assert.That(vm.EvaluateCommand.CanExecute(null)).IsTrue();
            await Assert.That(vm.EvaluateAdvancedCommand.CanExecute(null)).IsTrue();

            vm.IsEvaluating = true; // simulate an evaluation in flight
            await Assert.That(vm.EvaluateCommand.CanExecute(null)).IsFalse()
                .Because("a running evaluation must block re-triggering Evaluate");
            await Assert.That(vm.EvaluateAdvancedCommand.CanExecute(null)).IsFalse()
                .Because("...and Advanced Evaluate too — one evaluation at a time across both buttons");

            vm.IsEvaluating = false;
            await Assert.That(vm.EvaluateCommand.CanExecute(null)).IsTrue()
                .Because("the commands re-enable once the run completes");
            await Assert.That(vm.EvaluateAdvancedCommand.CanExecute(null)).IsTrue();
        });
    }

    /// <summary>The evaluate body itself short-circuits a re-entrant call while a run is in flight.</summary>
    [Test]
    public async Task Evaluate_ReentryGuard_SkipsWhenAlreadyEvaluating()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null);
            vm.OnActivated(new FakeDemoContext(demo));
            vm.IsEvaluating = true; // pretend a run is already in progress

            await vm.EvaluateCommand.ExecuteAsync(null);

            await Assert.That(vm.Boards.Count).IsEqualTo(0)
                .Because("the in-flight guard short-circuits a second evaluate before it touches the boards");

            vm.OnDeactivated();
        });
    }

    [Test]
    public async Task PathTree_GroupsDottedPathsHierarchically()
    {
        WorkbenchPath[] paths =
        [
            new("round.number", "context"),
            new("round.bomb.was_planted", "context"),
            new("match.map", "context"),
            new("player.entity.pawn.health", "entity")
        ];

        IReadOnlyList<WorkbenchPathNode> tree = WorkbenchPathTree.Build(paths);

        WorkbenchPathNode round = tree.Single(n => n.Segment == "round");
        await Assert.That(round.Children.Any(c => c.Segment == "number" && c.FullPath == "round.number")).IsTrue()
            .Because("round.number is a leaf under the round node");
        WorkbenchPathNode bomb = round.Children.Single(c => c.Segment == "bomb");
        await Assert.That(bomb.FullPath).IsNull().Because("round.bomb is only an intermediate prefix here");
        await Assert.That(bomb.Children.Single().FullPath).IsEqualTo("round.bomb.was_planted");

        // Deep nesting: player → entity → pawn → health.
        WorkbenchPathNode health = tree.Single(n => n.Segment == "player")
            .Children.Single(c => c.Segment == "entity")
            .Children.Single(c => c.Segment == "pawn")
            .Children.Single(c => c.Segment == "health");
        await Assert.That(health.FullPath).IsEqualTo("player.entity.pawn.health");
    }

    [Test]
    public async Task RuleWorkbenchView_RendersNonBlank_ToolbarAndEditorText()
    {
        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null); // load the starter template so the editor has text to render

            await HeadlessSession.RunOnUi(async () =>
            {
                const int Width = 900, Height = 560;
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
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? bmp = window.CaptureRenderedFrame();
                await Assert.That(bmp).IsNotNull();
                bmp!.Save(Path.Combine(HeadlessSession.ArtifactDir, "ruleworkbench-editor.png"));

                int toolbar = CountDifferentFromCorner(bmp, 12, 520, 12, 44);
                Console.WriteLine($"[ruleworkbench] toolbar non-background pixels: {toolbar}");
                await Assert.That(toolbar).IsGreaterThan(100).Because("the toolbar controls render");

                // The editor content area (left of the 300px data panel, below the toolbar) must render
                // the document text, proving the AvaloniaEdit control theme is registered (App.axaml
                // StyleInclude). Without the theme the TextEditor has no template and this band is blank.
                int editor = CountDifferentFromCorner(bmp, 40, 480, 56, 220);
                Console.WriteLine($"[ruleworkbench] editor-text non-background pixels: {editor}");
                await Assert.That(editor).IsGreaterThan(200)
                    .Because("the AvaloniaEdit editor must render its document text (control theme present)");

                window.Close();
            });
        });
    }

    // ── M6 (trace: applied-fire slice) ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     The data model reconciles with a real evaluation of the shipped rulesets on the reference demo:
    ///     every declared stat/highlight is a target, at least one fired, and a fired target's fires match
    ///     its count and carry real ticks. This is the correctness anchor (the applied-fire slice is
    ///     ground-truth from the same run the results board projects from).
    /// </summary>
    [Test]
    public async Task Trace_OnReferenceDemo_ReconcilesWithEvaluation()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        List<RulesetDoc> docs = LoadShippedDocs();

        BuildResult build = DemoAnalysis.Build(demo, docs);
        EvaluationResult? result = DemoAnalysis.Evaluate(demo, build).Snapshots;
        WorkbenchTraceReport report = WorkbenchTraceModel.Build(result, docs, demo);

        foreach (WorkbenchTraceTarget t in report.Targets.OrderByDescending(t => t.FireCount).Take(25))
        {
            Console.WriteLine($"[trace-m6] {t.Kind,-9} {t.Label,-28} fires={t.FireCount}");
        }

        await Assert.That(report.Targets.Count).IsGreaterThan(0)
            .Because("the shipped rulesets declare stats and highlights");
        await Assert.That(report.Targets.Any(t => t.FireCount > 0)).IsTrue()
            .Because("something fires on a full match demo");

        WorkbenchTraceTarget fired = report.Targets.First(t => t.FireCount > 0);
        IReadOnlyList<WorkbenchTraceFire> fires = report.FiresFor(fired.Id);
        await Assert.That(fires.Count).IsEqualTo(fired.FireCount)
            .Because("FireCount is the length of the fire list");
        await Assert.That(fires.All(f => f.Tick > 0)).IsTrue()
            .Because("every applied fire carries the server tick it happened at");
        // Fires are tick-ordered.
        await Assert.That(fires.Zip(fires.Skip(1)).All(p => p.First.Tick <= p.Second.Tick)).IsTrue();

        // Non-tautological guard: kill/death conservation, every kill is exactly one death, so the
        // total applied fires of the kills stat must equal the deaths stat's. This proves the
        // applied-edge extraction reflects real gameplay counts (the eyeballed KAST-golden match above),
        // not just internal list-length consistency.
        WorkbenchTraceTarget? kills = report.Targets.FirstOrDefault(t => t is { Kind: "stat", Label: "kills" });
        WorkbenchTraceTarget? deaths = report.Targets.FirstOrDefault(t => t is { Kind: "stat", Label: "deaths" });
        await Assert.That(kills).IsNotNull();
        await Assert.That(deaths).IsNotNull();
        await Assert.That(kills!.FireCount).IsGreaterThan(0);
        await Assert.That(kills.FireCount).IsEqualTo(deaths!.FireCount)
            .Because("kill/death conservation: each kill applied-fire is exactly one death applied-fire");
    }

    /// <summary>Value-node stats (e.g. count:) are traceable via AppliedMessagesByEdge, not just highlights.</summary>
    [Test]
    public async Task Trace_CoversValueNodeStats_NotJustHighlights()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        List<RulesetDoc> docs = LoadShippedDocs();

        BuildResult build = DemoAnalysis.Build(demo, docs);
        EvaluationResult? result = DemoAnalysis.Evaluate(demo, build).Snapshots;
        WorkbenchTraceReport report = WorkbenchTraceModel.Build(result, docs, demo);

        WorkbenchTraceTarget[] firedStats =
            report.Targets.Where(t => t.Kind == "stat" && t.FireCount > 0).ToArray();
        await Assert.That(firedStats.Length).IsGreaterThan(0)
            .Because("count/sum stats fire through the applied-edge source, not the timeline");

        // A per-player stat's fires carry player attribution (slot/name), not just game-scoped.
        bool anyPerPlayer = firedStats
            .SelectMany(t => report.FiresFor(t.Id))
            .Any(f => f.PlayerSlot is not null);
        await Assert.That(anyPerPlayer).IsTrue()
            .Because("the shipped rulesets are for: each_player, so fires attribute to a player");
    }

    /// <summary>The VM captures the trace on Evaluate and repopulates fires when a target is picked (M6).</summary>
    [Test]
    public async Task Trace_VmEvaluate_PopulatesTargetsAndFires()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null);
            vm.OnActivated(new FakeDemoContext(demo));
            await vm.EvaluateCommand.ExecuteAsync(null);

            await Assert.That(vm.TraceTargets.Count).IsGreaterThan(0)
                .Because("the shipped rulesets contribute traceable stats/highlights");

            WorkbenchTraceTarget? fired = vm.TraceTargets.FirstOrDefault(t => t.FireCount > 0);
            await Assert.That(fired).IsNotNull();

            vm.SelectedTraceTarget = fired;
            await Assert.That(vm.TraceFires.Count).IsEqualTo(fired!.FireCount)
                .Because("picking a target loads its fires");
            Console.WriteLine($"[trace-m6-vm] {vm.TraceSummary}");

            vm.OnDeactivated();
        });
    }

    [Test]
    public async Task Trace_NullResult_YieldsEmptyReport()
    {
        WorkbenchTraceReport report = WorkbenchTraceModel.Build(null, [], null);
        await Assert.That(report.Targets.Count).IsEqualTo(0);
        await Assert.That(report.FiresFor("stat:whatever").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Trace_View_TracePanelRenders_AfterEvaluate()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        await WithTempRules(async (vm, _) =>
        {
            vm.NewFileCommand.Execute(null);
            vm.OnActivated(new FakeDemoContext(demo));
            await vm.EvaluateCommand.ExecuteAsync(null);

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
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                WriteableBitmap? bmp = window.CaptureRenderedFrame();
                await Assert.That(bmp).IsNotNull();
                bmp!.Save(Path.Combine(HeadlessSession.ArtifactDir, "ruleworkbench-m6-trace.png"));

                // The trace panel occupies the bottom-right third; its combobox + fire rows must paint.
                int right = bmp.PixelSize.Width;
                int bottomTraceX0 = (int)(right * 0.70), bottomTraceX1 = right - 8;
                int y0 = Height - 175, y1 = Height - 20;
                int nonBg = CountDifferentFromCorner(bmp, bottomTraceX0, bottomTraceX1, y0, y1);
                Console.WriteLine($"[trace-m6-render] trace-panel non-background pixels: {nonBg}");
                await Assert.That(nonBg).IsGreaterThan(100).Because("the trace picker + fire list render");

                window.Close();
            });

            vm.OnDeactivated();
        });
    }

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

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Runs <paramref name="body" /> with a VM pinned to the repo's shipped <c>rules/</c> and a fresh
    ///     empty user overlay dir; disposes the VM's file watcher first so the test is deterministic, and
    ///     restores the env + deletes the temp dir after.
    /// </summary>
    private static async Task WithTempRules(Func<RuleWorkbenchTabViewModel, string, Task> body)
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "rules");
        string userDir = Path.Combine(Path.GetTempPath(), "dvwb_" + Guid.NewGuid().ToString("N"));
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

    private static int CountDifferentFromCorner(WriteableBitmap bmp, int x0, int x1, int y0, int y1)
    {
        PixelSize size = bmp.PixelSize;
        int w = size.Width, h = size.Height;
        byte[] buffer = new byte[w * h * 4]; // BGRA8888
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        byte bb = buffer[0], bg = buffer[1], br = buffer[2]; // corner (0,0) = background
        int count = 0;
        for (int y = y0; y < Math.Min(y1, h); y++)
        {
            for (int x = x0; x < Math.Min(x1, w); x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(buffer[i] - bb) > 6 || Math.Abs(buffer[i + 1] - bg) > 6
                                                 || Math.Abs(buffer[i + 2] - br) > 6)
                {
                    count++;
                }
            }
        }

        return count;
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

    private sealed class FakeHost : IModuleHost
    {
        public IModuleContext Context => null!;
        public bool HasCapability(string capability) => true;

        public void Log(ModuleLogLevel level, string message)
        {
        }
    }

    /// <summary>
    ///     A minimal <see cref="IModuleContext" /> that also carries a loaded demo via
    ///     <see cref="ICurrentDemoSource" />, the M5 first-party demo-access seam. Only the members the
    ///     Workbench actually touches (Players / CurrentPlayers / Advanced / the demo) are functional; the
    ///     rest are unused stubs.
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
