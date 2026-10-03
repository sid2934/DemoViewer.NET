#region

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.ViewModels.UtilityBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The visual lineup picker (Strat Editor: visual lineup picker) and the step row's lineup and technique
///     combos: a pick writes kind, lineup and technique as one undo entry, cancel writes nothing, a stored alias
///     id opens selected and shows by name, and the map view model is disposed either way.
/// </summary>
[NotInParallel]
public class StratLineupPickerTests
{
    private const string Map = "de_mirage";

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentQueue<Action> _posted = new();

        public Harness(IDemoProcessingQueue? queue = null, bool defaultPicker = false)
        {
            (Index, Lineup) = StratThrowOriginTests.Indexed();
            Tab = new StratBookTabViewModel(new StratStore(null), null, _posted.Enqueue, false, grenades: Index,
                canvasMapLoader: defaultPicker ? null : _ => null,
                lineupMap: defaultPicker
                    ? null
                    : (map, asset) => new UtilityBookTabViewModel(Index, loadMapAsset: _ => asset, retire: a => a(), lockedMap: map,
                        ownsMapAsset: false,
                        background: queue is null ? null : QueueWork.Section(queue, "Lineup picker", "utility", "section:lineup-picker")));
            Tab.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            Tab.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            Tab.SelectedMap = Map;
            Tab.NewStratCommand.Execute(null);
            Pump();
        }

        public GrenadeIndex Index { get; }

        public GrenadeLineup Lineup { get; }

        public StratBookTabViewModel Tab { get; }

        public StratEditorViewModel Editor => Tab.Editor;

        public StratStep Step => Tab.Session.Document!.Steps[^1];

        public void Dispose() => Tab.Dispose();

        public void Pump()
        {
            while (_posted.TryDequeue(out Action? action))
            {
                action();
            }
        }

        // The editor's catalog groups the map off the UI thread; wait for it and run what it posts back.
        public async Task WarmAsync()
        {
            _ = Editor.ResolveLineup(Lineup.Id);
            for (int i = 0; i < 500 && Editor.ResolveLineup(Lineup.Id) is null; i++)
            {
                await Task.Delay(10);
                Pump();
            }

            Pump();
        }

        public StratStepRow AddThrow()
        {
            Editor.AddStepCommand.Execute(null);
            Editor.Steps[^1].Verb = "throw";
            Editor.EndEditBurst();
            return Editor.Steps[^1];
        }

        public string StepJson() => JsonSerializer.SerializeToNode(Step, StratJsonContext.Default.StratStep)!.ToJsonString();
    }

    // Records each queued job's priority and runs it at once, as a queue with nothing else in it would.
    private sealed class RecordingQueue : IDemoProcessingQueue
    {
        public ConcurrentQueue<DemoJobPriority> Priorities { get; } = new();
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<DemoQueueItem> Items { get; } = new([]);

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public event Action? CapacityAvailable
        {
            add { }
            remove { }
        }

        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused => false;
        public int QueuedCount => 0;
        public int RunningCount => 0;

        public Task<CS2DemoKit.Parser.ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request) => throw new NotSupportedException();

        public int ActiveCount(QueueJobKind kind) => 0;

        public IDemoQueueHandle SubmitJob(QueueJobRequest request)
        {
            Priorities.Enqueue(request.Priority);
            return new DoneHandle(request.RunAsync(new InlineContext()));
        }

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void RemoveByUser(Guid itemId)
        {
        }

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void CancelOwned(string ownerTag)
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        private sealed class InlineContext : IQueueJobContext
        {
            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, int total, string? detail = null)
            {
            }

            public Task StepAsideAsync() => Task.CompletedTask;

            public void ReleaseSlot()
            {
            }
        }

        private sealed class DoneHandle(Task completion) : IDemoQueueHandle
        {
            public Guid Id { get; } = Guid.NewGuid();
            public DemoQueueItemState State => completion.IsCompleted ? DemoQueueItemState.Completed : DemoQueueItemState.Running;
            public Task Completion => completion;

            public void Cancel()
            {
            }
        }
    }

    private static Guid AliasOf(GrenadeLineup lineup) => lineup.AliasIds.First(a => a != lineup.Id);

    [Test]
    public async Task TheFixture_HasTwoTechniques_AndAnAliasId()
    {
        using Harness h = new();
        using (Assert.Multiple())
        {
            await Assert.That(h.Lineup.Techniques.Count).IsEqualTo(2);
            await Assert.That(h.Lineup.AliasIds.Any(a => a != h.Lineup.Id)).IsTrue()
                .Because("the alias tests need an id the lineup answers to besides its own");
        }
    }

    [Test]
    public async Task TheCatalog_ResolvesAnAliasId_ToTheLineup_WithItsTechniques()
    {
        using Harness h = new();
        using LineupOriginSource source = new(h.Index, a => a());
        source.Load(Map);
        StratLineupCatalog catalog = new(source);

        StratLineupChoice? choice = catalog.Resolve(Map, AliasOf(h.Lineup));
        using (Assert.Multiple())
        {
            await Assert.That(choice?.Id).IsEqualTo(h.Lineup.Id);
            await Assert.That(choice?.UtilityKind).IsEqualTo("smoke");
            await Assert.That(choice?.Techniques.Select(t => t.Key)).IsEquivalentTo(h.Lineup.Techniques.Select(t => t.Key));
            await Assert.That(catalog.Options(Map, "smoke").Select(o => o.Id)).IsEquivalentTo(new Guid?[] { h.Lineup.Id });
            await Assert.That(catalog.Options(Map, "flash")).IsEmpty();
        }
    }

    [Test]
    public async Task TheDropdown_ShowsAnAliasIdByName_WithoutRewritingIt()
    {
        using Harness h = new();
        h.AddThrow();
        Guid alias = AliasOf(h.Lineup);
        h.Tab.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = alias.ToString() }));
        await h.WarmAsync();

        StratStepRow row = h.Editor.Steps[0];
        using (Assert.Multiple())
        {
            await Assert.That(row.Lineup?.Id).IsEqualTo(h.Lineup.Id);
            await Assert.That(row.Lineup?.Label).IsNotEqualTo(alias.ToString()).Because("the name, not the raw id");
            await Assert.That(row.LineupOptions).Contains(row.Lineup!);
            await Assert.That(h.Step.Utility!.LineupId).IsEqualTo(alias).Because("projection never writes");
            await Assert.That(row.ShowTechnique).IsTrue();
            await Assert.That(row.Technique?.Key).IsEqualTo(h.Lineup.Techniques[0].Key).Because("null is the most thrown one");
            await Assert.That(h.Step.Utility!.Technique).IsNull();
        }
    }

    [Test]
    public async Task TheTechniqueCombo_WheelingBackToTheMostThrown_LeavesNothing()
    {
        using Harness h = new();
        h.AddThrow();
        h.Tab.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = h.Lineup.Id.ToString() }));
        await h.WarmAsync();
        int depth = h.Tab.Session.UndoDepth;
        string before = h.StepJson();

        // The stored technique is null, which the combo shows as the most thrown one: K0, K1, K0.
        StratStepRow row = h.Editor.Steps[0];
        await Assert.That(row.Technique?.Key).IsEqualTo(h.Lineup.Techniques[0].Key);
        row.Technique = row.Techniques[1];
        await Assert.That(h.Step.Utility!.Technique).IsEqualTo(h.Lineup.Techniques[1].Key);
        row = h.Editor.Steps[0];
        row.Technique = row.Techniques[0];

        using (Assert.Multiple())
        {
            await Assert.That(h.StepJson()).IsEqualTo(before).Because("no explicit copy of the default is left behind");
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth);
        }
    }

    [Test]
    public async Task TheTechniqueCombo_WritesTheKey_AsOneEntry_ThatUndoes()
    {
        using Harness h = new();
        h.AddThrow();
        h.Tab.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = h.Lineup.Id.ToString() }));
        await h.WarmAsync();
        int depth = h.Tab.Session.UndoDepth;
        string before = h.StepJson();

        h.Editor.Steps[0].Technique = h.Editor.Steps[0].Techniques[1];
        h.Editor.EndEditBurst();
        using (Assert.Multiple())
        {
            await Assert.That(h.Step.Utility!.Technique).IsEqualTo(h.Lineup.Techniques[1].Key);
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth + 1);
        }

        h.Tab.UndoCommand.Execute(null);
        await Assert.That(h.StepJson()).IsEqualTo(before);
    }

    [Test]
    public async Task ConfirmingWhatTheStepAlreadyHas_WritesNothing_AndKeepsAnAliasId()
    {
        using Harness h = new();
        h.AddThrow();
        Guid alias = AliasOf(h.Lineup);
        h.Tab.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = alias.ToString() }));
        await h.WarmAsync();
        string before = h.StepJson();
        int depth = h.Tab.Session.UndoDepth;

        h.Tab.OpenLineupPickerCommand.Execute(h.Editor.Steps[0]);
        LineupPickerViewModel picker = h.Tab.LineupPicker!;
        await Assert.That(picker.CanConfirm).IsTrue().Because("the stored lineup opens selected");
        picker.ConfirmCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(h.StepJson()).IsEqualTo(before);
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth);
            await Assert.That(h.Step.Utility!.LineupId).IsEqualTo(alias);
            await Assert.That(h.Editor.ApplyLineupPick(h.Step.Id, "smoke", h.Lineup.Id, h.Lineup.Techniques[0].Key)).IsFalse();
        }

        // Another technique of the same lineup writes the technique only, and still keeps the alias id.
        await Assert.That(h.Editor.ApplyLineupPick(h.Step.Id, "smoke", h.Lineup.Id, h.Lineup.Techniques[1].Key)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(h.Step.Utility!.LineupId).IsEqualTo(alias);
            await Assert.That(h.Step.Utility.Technique).IsEqualTo(h.Lineup.Techniques[1].Key);
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth + 1);
        }
    }

    [Test]
    public async Task ThePicker_Closes_WhenTheTabHides_TheStratChanges_OrItsStepIsDeleted()
    {
        using Harness h = new();
        StratStepRow row = h.AddThrow();

        h.Tab.OpenLineupPickerCommand.Execute(row);
        LineupPickerViewModel first = h.Tab.LineupPicker!;
        h.Tab.OnDeactivated();
        await Assert.That(h.Tab.LineupPicker).IsNull().Because("a hidden tab keeps no picker");
        await Assert.That(first.Map.IsDisposed).IsTrue();

        h.Tab.OpenLineupPickerCommand.Execute(h.Editor.Steps[0]);
        LineupPickerViewModel second = h.Tab.LineupPicker!;
        h.Editor.RemoveStepCommand.Execute(h.Editor.Steps[0]);
        await Assert.That(h.Tab.LineupPicker).IsNull().Because("its step is gone");
        await Assert.That(second.Map.IsDisposed).IsTrue();

        StratStepRow again = h.AddThrow();
        Guid stratId = h.Tab.Session.Document!.Id;
        h.Tab.OpenLineupPickerCommand.Execute(again);
        LineupPickerViewModel third = h.Tab.LineupPicker!;
        h.Tab.NewStratCommand.Execute(null);
        await Assert.That(h.Tab.Session.Document!.Id).IsNotEqualTo(stratId);
        await Assert.That(h.Tab.LineupPicker).IsNull().Because("another strat is open");
        await Assert.That(third.Map.IsDisposed).IsTrue();

        StratStepRow last = h.AddThrow();
        h.Tab.OpenLineupPickerCommand.Execute(last);
        h.Tab.SelectedStrat = null;
        await Assert.That(h.Tab.LineupPicker).IsNull().Because("no strat is open");
    }

    [Test]
    public async Task OpeningThePicker_QueuesItsReadsAsUserWork()
    {
        RecordingQueue queue = new();
        using Harness h = new(queue);
        StratStepRow row = h.AddThrow();

        h.Tab.OpenLineupPickerCommand.Execute(row);
        h.Tab.LineupPicker!.Kind = "flash";
        using (Assert.Multiple())
        {
            await Assert.That(queue.Priorities).IsNotEmpty();
            await Assert.That(queue.Priorities.All(p => p == DemoJobPriority.UserRequested)).IsTrue()
                .Because("the picker is opened and driven by the user, so its reads go to the front");
        }

        h.Tab.LineupPicker!.CancelCommand.Execute(null);
    }

    [Test]
    public async Task TheDefaultPicker_DrawsTheCanvasBundle_AndLeavesItToTheCanvas()
    {
        using Harness h = new(defaultPicker: true);
        StratStepRow row = h.AddThrow();

        h.Tab.OpenLineupPickerCommand.Execute(row);
        LineupPickerViewModel picker = h.Tab.LineupPicker!;
        for (int i = 0; i < 500 && !picker.Map.HasGroups; i++)
        {
            await Task.Delay(10);
            h.Pump();
        }

        using (Assert.Multiple())
        {
            await Assert.That(picker.Map.HasGroups).IsTrue();
            await Assert.That(h.Tab.Canvas.MapAsset).IsNotNull().Because("the baked de_mirage bundle ships with the app");
            await Assert.That(picker.Map.MapAsset).IsSameReferenceAs(h.Tab.Canvas.MapAsset);
        }

        LoadedMapAsset shared = h.Tab.Canvas.MapAsset!;
        picker.CancelCommand.Execute(null);
        h.Pump();
        await Assert.That(h.Tab.Canvas.MapAsset).IsSameReferenceAs(shared).Because("the canvas still owns and draws it");
    }

    [Test]
    public async Task APick_WritesKindLineupAndTechnique_AsOneEntry_AndDisposesTheMap()
    {
        using Harness h = new();
        StratStepRow row = h.AddThrow();
        int depth = h.Tab.Session.UndoDepth;

        h.Tab.OpenLineupPickerCommand.Execute(row);
        LineupPickerViewModel picker = h.Tab.LineupPicker!;
        await Assert.That(picker.Kind).IsEqualTo("smoke").Because("a step without a kind opens on smoke");
        await Assert.That(picker.CanConfirm).IsFalse();

        picker.Map.ClickLanding(picker.Map.Groups.Single().Id);
        await Assert.That(picker.Positions.Count).IsEqualTo(2).Because("one position per technique");
        LineupTechnique second = h.Lineup.Techniques[1];
        picker.SelectedPosition = picker.Positions.Single(p => p.Key == UtilityBookTabViewModel.PositionKey(h.Lineup, second));
        await Assert.That(picker.CanConfirm).IsTrue();
        picker.ConfirmCommand.Execute(null);

        UtilityRef utility = h.Step.Utility!;
        using (Assert.Multiple())
        {
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(utility.Kind).IsEqualTo("smoke");
            await Assert.That(utility.LineupId).IsEqualTo(h.Lineup.Id);
            await Assert.That(utility.Technique).IsEqualTo(second.Key);
            await Assert.That(h.Tab.LineupPicker).IsNull();
            await Assert.That(picker.Map.IsDisposed).IsTrue();
        }

        h.Tab.UndoCommand.Execute(null);
        await Assert.That(h.Step.Utility).IsNull().Because("one undo takes back the whole pick");
    }

    [Test]
    public async Task APick_OverAnotherKind_WritesTheKindFirst_AndKeepsTheLanding()
    {
        using Harness h = new();
        h.AddThrow();
        h.Tab.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null, new JsonObject
        {
            ["kind"] = "flash", ["lineupId"] = Guid.NewGuid().ToString(), ["technique"] = "stand-left",
            ["landing"] = new JsonObject { ["x"] = 1.0, ["y"] = 2.0 }
        }));
        string before = h.StepJson();
        int depth = h.Tab.Session.UndoDepth;

        bool wrote = h.Editor.ApplyLineupPick(h.Step.Id, "smoke", h.Lineup.Id, h.Lineup.Techniques[0].Key);
        using (Assert.Multiple())
        {
            await Assert.That(wrote).IsTrue();
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth + 1);
            await Assert.That(h.Step.Utility!.Kind).IsEqualTo("smoke");
            await Assert.That(h.Step.Utility.LineupId).IsEqualTo(h.Lineup.Id);
            await Assert.That(h.Step.Utility.Technique).IsEqualTo(h.Lineup.Techniques[0].Key);
            await Assert.That(h.Step.Utility.Landing?.X).IsEqualTo(1.0);
        }

        h.Tab.UndoCommand.Execute(null);
        await Assert.That(h.StepJson()).IsEqualTo(before);
    }

    [Test]
    public async Task Cancel_WritesNothing_AndDisposesTheMap()
    {
        using Harness h = new();
        StratStepRow row = h.AddThrow();
        string before = h.StepJson();
        int depth = h.Tab.Session.UndoDepth;

        h.Tab.OpenLineupPickerCommand.Execute(row);
        LineupPickerViewModel picker = h.Tab.LineupPicker!;
        picker.Map.ClickLanding(picker.Map.Groups.Single().Id);
        picker.SelectedPosition = picker.Positions[0];
        picker.Kind = "flash";
        picker.CancelCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(h.StepJson()).IsEqualTo(before);
            await Assert.That(h.Tab.Session.UndoDepth).IsEqualTo(depth);
            await Assert.That(h.Tab.LineupPicker).IsNull();
            await Assert.That(picker.Map.IsDisposed).IsTrue();
        }
    }

    [Test]
    public async Task AStoredAliasId_OpensThePickerWithItsPositionSelected()
    {
        using Harness h = new();
        StratStepRow row = h.AddThrow();
        LineupTechnique second = h.Lineup.Techniques[1];
        h.Tab.Session.Apply(PatchOp.ReplaceOp("/steps/0/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = AliasOf(h.Lineup).ToString(), ["technique"] = second.Key }));
        await h.WarmAsync();

        h.Tab.OpenLineupPickerCommand.Execute(h.Editor.Steps[0]);
        LineupPickerViewModel picker = h.Tab.LineupPicker!;
        using (Assert.Multiple())
        {
            await Assert.That(picker.Map.FocusedGroup).IsNotNull();
            await Assert.That(picker.SelectedPosition?.Key).IsEqualTo(UtilityBookTabViewModel.PositionKey(h.Lineup, second));
            await Assert.That(picker.CanConfirm).IsTrue();
        }

        picker.CancelCommand.Execute(null);
    }

    [Test]
    public async Task TheUtilityBookTab_IsUnchanged_WithoutALockedMap()
    {
        using Harness h = new();
        using UtilityBookTabViewModel tab = new(h.Index, loadMapAsset: _ => null, retire: a => a());
        using (Assert.Multiple())
        {
            await Assert.That(tab.SelectedMap).IsEqualTo(Map).Because("the tab still falls back to the first indexed map");
            await Assert.That(tab.QueryKinds).IsNull();
            await Assert.That(tab.HasGroups).IsTrue();
        }

        using UtilityBookTabViewModel locked = new(h.Index, loadMapAsset: _ => null, retire: a => a(), lockedMap: "de_nuke");
        using (Assert.Multiple())
        {
            await Assert.That(locked.SelectedMap).IsEqualTo("de_nuke").Because("the picker never shows another map");
            await Assert.That(locked.HasGroups).IsFalse();
        }
    }
}
