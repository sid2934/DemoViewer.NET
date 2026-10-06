#region

using System.Collections.ObjectModel;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure-VM coverage of <see cref="ProcessingQueueStatusViewModel" /> (+ its
///     <see cref="DemoQueueRowViewModel" /> rows): the demo-processing-queue.md status-chip mapper. No
///     Avalonia / headless session, so it runs in parallel. Asserts the queue-state → chip vocabulary, the
///     status line, the Items → Rows projection + state→dot flags, per-item remove, and Pause/Resume, all
///     over a minimal in-memory <see cref="IDemoProcessingQueue" /> double (the real queue would need real
///     multi-GB parses to reach these states).
/// </summary>
public class ProcessingQueueStatusViewModelTests
{
    [Test]
    public async Task Running_MapsToWorkingPulsingChip_AndCountLine()
    {
        FakeQueue q = new()
        {
            RunningCount = 1,
            QueuedCount = 0
        };
        ProcessingQueueStatusViewModel vm = new(q);

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Working);
        await Assert.That(vm.Chip.IsPulsing).IsTrue().Because("a running parse pulses");
        await Assert.That(vm.Chip.Label).IsEqualTo("Processing 1");
        await Assert.That(vm.StatusLine).IsEqualTo("1 running · 0 queued");
    }

    [Test]
    public async Task QueuedOnly_MapsToWorkingNonPulsingChip()
    {
        FakeQueue q = new()
        {
            RunningCount = 0,
            QueuedCount = 2
        };
        ProcessingQueueStatusViewModel vm = new(q);

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Working);
        await Assert.That(vm.Chip.IsPulsing).IsFalse().Because("queued-but-not-running is steady, not pulsing");
        await Assert.That(vm.Chip.Label).IsEqualTo("2 queued");
    }

    [Test]
    public async Task Paused_MapsToOffChip_AndAnnotatesStatusLine()
    {
        FakeQueue q = new()
        {
            RunningCount = 1,
            QueuedCount = 3
        };
        q.Pause();
        ProcessingQueueStatusViewModel vm = new(q);

        await Assert.That(vm.Chip.DotState).IsEqualTo(StatusChipDotState.Off);
        await Assert.That(vm.Chip.Label).IsEqualTo("Background paused");
        await Assert.That(vm.IsPaused).IsTrue();
        await Assert.That(vm.PauseResumeLabel).IsEqualTo("Resume background work");
        await Assert.That(vm.StatusLine).Contains("paused");
    }

    [Test]
    public async Task Rows_ProjectItems_WithStateDotFlags()
    {
        FakeQueue q = new();
        q.Add("run.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Running);
        q.Add("done.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Completed);
        q.Add("fail.dem", "highlights", DemoJobPriority.Background, DemoQueueItemState.Failed);
        q.Add("rej.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Rejected);
        q.Add("cancel.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Cancelled);

        ProcessingQueueStatusViewModel vm = new(q);

        await Assert.That(vm.Rows.Count).IsEqualTo(1).Because("finished items live in Recent");
        await Assert.That(vm.IsEmpty).IsFalse();
        // Running → Working dot + pulsing.
        await Assert.That(vm.Rows[0].IsStateWorking).IsTrue();
        await Assert.That(vm.Rows[0].IsPulsing).IsTrue();
        await Assert.That(vm.Rows[0].StateLabel).IsEqualTo("Running");
        // Completed → Good; Failed → Error; Rejected → Degraded; Cancelled → Off. Recent is newest first.
        DemoQueueRowViewModel Recent(string name) => vm.RecentRows.Single(r => r.DisplayText == name);
        await Assert.That(vm.RecentRows.Count).IsEqualTo(4);
        await Assert.That(Recent("done.dem").IsStateGood).IsTrue();
        await Assert.That(Recent("fail.dem").IsStateError).IsTrue();
        await Assert.That(Recent("rej.dem").IsStateDegraded).IsTrue();
        await Assert.That(Recent("cancel.dem").IsStateOff).IsTrue();
    }

    [Test]
    public async Task AFinishingItem_LeavesTheLiveList_AndHeadsRecent_KeepingItsError()
    {
        FakeQueue q = new();
        DemoQueueItem a = q.Add("a.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Running);
        DemoQueueItem b = q.Add("b.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        q.Add("old.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Completed);
        ProcessingQueueStatusViewModel vm = new(q);
        await Assert.That(vm.RecentViewLabel).IsEqualTo("Recent (1)");

        a.State = DemoQueueItemState.Failed;
        a.Error = "Unexpected end of stream";
        a.EndedSeq = 5;
        b.State = DemoQueueItemState.Running;

        using (Assert.Multiple())
        {
            await Assert.That(vm.Rows.Select(r => r.DisplayText)).IsEquivalentTo(["b.dem"]);
            await Assert.That(vm.RecentRows.Select(r => r.DisplayText).ToList()).IsEquivalentTo(["a.dem", "old.dem"]);
            await Assert.That(vm.RecentRows[0].DisplayText).IsEqualTo("a.dem");
            await Assert.That(vm.RecentRows[0].HasError).IsTrue();
            await Assert.That(vm.RecentRows[0].Error).IsEqualTo("Unexpected end of stream");
            await Assert.That(vm.RecentRows[0].CanRemove).IsFalse();
            await Assert.That(vm.QueueViewLabel).IsEqualTo("Queue (1)");
            await Assert.That(vm.RecentViewLabel).IsEqualTo("Recent (2)");
        }

        // The queue later prunes its history; Recent keeps the session's.
        q.Drop(a);
        await Assert.That(vm.RecentRows.Select(r => r.DisplayText).ToList()).IsEquivalentTo(["a.dem", "old.dem"]);
    }

    [Test]
    public async Task AnItemTheQueueDropsUnfinished_IsNotHistory()
    {
        FakeQueue q = new();
        DemoQueueItem save = q.Add("save", "store", DemoJobPriority.Background, DemoQueueItemState.Running);
        ProcessingQueueStatusViewModel vm = new(q);
        q.Drop(save);

        await Assert.That(vm.Rows.Count).IsEqualTo(0);
        await Assert.That(vm.RecentRows.Count).IsEqualTo(0);
        await Assert.That(vm.IsRecentEmpty).IsTrue();
    }

    [Test]
    public async Task Recent_IsCapped_NewestFirst()
    {
        FakeQueue q = new();
        ProcessingQueueStatusViewModel vm = new(q);
        int total = ProcessingQueueStatusViewModel.RecentCap + 5;
        for (int i = 1; i <= total; i++)
        {
            DemoQueueItem item = q.Add($"d{i}.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Running);
            item.EndedSeq = i;
            item.State = DemoQueueItemState.Completed;
        }

        using (Assert.Multiple())
        {
            await Assert.That(vm.RecentRows.Count).IsEqualTo(ProcessingQueueStatusViewModel.RecentCap);
            await Assert.That(vm.RecentRows[0].DisplayText).IsEqualTo($"d{total}.dem");
            await Assert.That(vm.RecentRows[^1].DisplayText).IsEqualTo("d6.dem");
        }

        // An evicted item the queue still lists does not come back.
        q.RaiseChanged();
        await Assert.That(vm.RecentRows.Any(r => r.DisplayText == "d1.dem")).IsFalse();
        await Assert.That(vm.RecentRows[0].DisplayText).IsEqualTo($"d{total}.dem");
    }

    [Test]
    public async Task LiveRows_AreRunningFirst_ThenQueuedByTheQueuesStartRank_AndFollowARankChange()
    {
        FakeQueue q = new();
        DemoQueueItem late = q.Add("late.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        DemoQueueItem soon = q.Add("soon.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        DemoQueueItem light = q.Add("section", "stats", DemoJobPriority.Background, DemoQueueItemState.Queued);
        q.Add("running.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Running);
        late.StartRank = 1;
        soon.StartRank = 0;
        light.StartRank = 0;
        light.Light = true;
        ProcessingQueueStatusViewModel vm = new(q);
        DemoQueueRowViewModel lateRow = vm.Rows.Single(r => r.DisplayText == "late.dem");

        await Assert.That(string.Join(",", vm.Rows.Select(r => r.DisplayText)))
            .IsEqualTo("running.dem,soon.dem,late.dem,section");

        late.StartRank = 0;
        soon.StartRank = 1;
        await Assert.That(string.Join(",", vm.Rows.Select(r => r.DisplayText)))
            .IsEqualTo("running.dem,late.dem,soon.dem,section");
        await Assert.That(vm.Rows[1]).IsSameReferenceAs(lateRow).Because("rows move, they are not rebuilt");
    }

    [Test]
    public async Task MoveToTop_IsOfferedOnQueuedRowsOnly_AndForwardsToTheQueue()
    {
        FakeQueue q = new();
        DemoQueueItem queued = q.Add("q.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        q.Add("r.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Running);
        DemoQueueItem open = q.Add("open", "shell", DemoJobPriority.Foreground, DemoQueueItemState.Queued);
        open.Kind = QueueJobKind.DemoOpen;
        ProcessingQueueStatusViewModel vm = new(q);
        DemoQueueRowViewModel Row(string name) => vm.Rows.Single(r => r.DisplayText == name);

        using (Assert.Multiple())
        {
            await Assert.That(Row("q.dem").CanPromote).IsTrue();
            await Assert.That(Row("q.dem").PromoteLabel).IsEqualTo("Move to top");
            await Assert.That(Row("q.dem").RemoveLabel).IsEqualTo("Remove from queue");
            await Assert.That(Row("r.dem").IsQueued).IsFalse();
            await Assert.That(Row("r.dem").CanPromote).IsFalse();
            await Assert.That(Row("r.dem").RemoveLabel).IsEqualTo("Stop and remove");
            await Assert.That(Row("open").CanPromote).IsFalse().Because("an open already goes first");
        }

        Row("q.dem").PromoteCommand.Execute(null);
        await Assert.That(q.Promoted).IsEquivalentTo([queued.Id]);

        queued.Promoted = true;
        await Assert.That(Row("q.dem").IsPromoted).IsTrue();
        queued.Hold = DemoQueueHold.BackgroundOff;
        await Assert.That(Row("q.dem").PromoteLabel).IsEqualTo("Move to top (waits: background processing off)");
        queued.Hold = DemoQueueHold.Paused;
        await Assert.That(Row("q.dem").PromoteLabel).IsEqualTo("Move to top (waits: background work paused)");
    }

    [Test]
    public async Task TheViewToggle_ShowsOneViewAtATime_AndAReclickKeepsIt()
    {
        ProcessingQueueStatusViewModel vm = new(new FakeQueue());
        await Assert.That(vm.IsQueueView).IsTrue();
        vm.IsRecentView = true;
        await Assert.That(vm.IsQueueView).IsFalse();
        vm.IsRecentView = false;
        await Assert.That(vm.IsQueueView).IsTrue();
        vm.IsQueueView = true;
        await Assert.That(vm.IsRecentView).IsFalse();
    }

    [Test]
    public async Task PriorityChip_ShownOnlyWhenElevated()
    {
        FakeQueue q = new();
        q.Add("auto.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        q.Add("manual.dem", "highlights", DemoJobPriority.UserRequested, DemoQueueItemState.Queued);
        ProcessingQueueStatusViewModel vm = new(q);

        await Assert.That(vm.Rows[0].HasElevatedPriority).IsFalse().Because("routine Background work shows no chip");
        await Assert.That(vm.Rows[1].HasElevatedPriority).IsTrue();
        await Assert.That(vm.Rows[1].PriorityLabel).IsEqualTo("manual");
    }

    [Test]
    public async Task RowRemove_RemovesFromQueue_AndReprojects()
    {
        FakeQueue q = new();
        q.Add("a.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        q.Add("b.dem", "library", DemoJobPriority.Background, DemoQueueItemState.Queued);
        ProcessingQueueStatusViewModel vm = new(q);
        await Assert.That(vm.Rows.Count).IsEqualTo(2);

        vm.Rows[0].RemoveCommand.Execute(null);

        await Assert.That(q.Items.Count).IsEqualTo(1).Because("Row.Remove routes to queue.RemoveByUser");
        await Assert.That(vm.Rows.Count).IsEqualTo(1);
        await Assert.That(vm.Rows[0].DisplayText).IsEqualTo("b.dem");
    }

    [Test]
    public async Task TogglePause_FlipsQueuePause_AndReMapsChip()
    {
        FakeQueue q = new()
        {
            RunningCount = 1
        };
        ProcessingQueueStatusViewModel vm = new(q);
        await Assert.That(vm.IsPaused).IsFalse();

        vm.TogglePauseCommand.Execute(null);
        await Assert.That(q.IsPaused).IsTrue();
        await Assert.That(vm.IsPaused).IsTrue();
        await Assert.That(vm.Chip.Label).IsEqualTo("Background paused");

        vm.TogglePauseCommand.Execute(null);
        await Assert.That(q.IsPaused).IsFalse();
        await Assert.That(vm.Chip.Label).IsEqualTo("Processing 1");
    }

    [Test]
    public async Task BackgroundDisabled_AnnotatesStatusLineAndFlag()
    {
        FakeQueue q = new()
        {
            BackgroundEnabled = false
        };
        ProcessingQueueStatusViewModel vm = new(q);

        await Assert.That(vm.IsBackgroundDisabled).IsTrue();
        await Assert.That(vm.StatusLine).Contains("background disabled");
    }

    // ── Minimal in-memory IDemoProcessingQueue double (only the members the VM reads are meaningful) ──
    private sealed class FakeQueue : IDemoProcessingQueue
    {
        private readonly ObservableCollection<DemoQueueItem> _items = [];

        public FakeQueue() => Items = new ReadOnlyObservableCollection<DemoQueueItem>(_items);

        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; }
        public event Action? Changed;
        public event Action? CapacityAvailable;
        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused { get; private set; }
        public int QueuedCount { get; set; }
        public int RunningCount { get; set; }

        public void Pause()
        {
            IsPaused = true;
            Changed?.Invoke();
        }

        public void Resume()
        {
            IsPaused = false;
            Changed?.Invoke();
        }

        public void RemoveByUser(Guid itemId)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Id == itemId)
                {
                    _items.RemoveAt(i);
                }
            }

            CapacityAvailable?.Invoke();
            Changed?.Invoke();
        }

        public Task<ParsedDemo> RequestForegroundAsync(
            string? path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request) =>
            throw new NotSupportedException();

        public IDemoQueueHandle SubmitVisit(DemoVisitRequest request) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitJob(QueueJobRequest request) => throw new NotSupportedException();

        public int ActiveCount(QueueJobKind kind) => 0;

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void CancelOwned(string ownerTag)
        {
        }

        public List<Guid> Promoted { get; } = [];

        public bool Promote(Guid itemId)
        {
            Promoted.Add(itemId);
            return true;
        }

        public DemoQueueItem Add(string name, string owners, DemoJobPriority priority, DemoQueueItemState state)
        {
            DemoQueueItem item = new()
            {
                Id = Guid.NewGuid(),
                Path = "/demos/" + name,
                DisplayName = name,
                Owners = owners,
                Priority = priority,
                State = state
            };
            _items.Add(item);
            Changed?.Invoke();
            return item;
        }

        public void Drop(DemoQueueItem item)
        {
            _items.Remove(item);
            Changed?.Invoke();
        }

        public void RaiseChanged() => Changed?.Invoke();
    }
}
