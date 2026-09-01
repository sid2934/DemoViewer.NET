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
        await Assert.That(vm.Chip.Label).IsEqualTo("Queue paused");
        await Assert.That(vm.IsPaused).IsTrue();
        await Assert.That(vm.PauseResumeLabel).IsEqualTo("Resume background");
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

        await Assert.That(vm.Rows.Count).IsEqualTo(5);
        await Assert.That(vm.IsEmpty).IsFalse();
        // Running → Working dot + pulsing.
        await Assert.That(vm.Rows[0].IsStateWorking).IsTrue();
        await Assert.That(vm.Rows[0].IsPulsing).IsTrue();
        await Assert.That(vm.Rows[0].StateLabel).IsEqualTo("Running");
        // Completed → Good; Failed → Error; Rejected → Degraded; Cancelled → Off.
        await Assert.That(vm.Rows[1].IsStateGood).IsTrue();
        await Assert.That(vm.Rows[2].IsStateError).IsTrue();
        await Assert.That(vm.Rows[3].IsStateDegraded).IsTrue();
        await Assert.That(vm.Rows[4].IsStateOff).IsTrue();
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
        await Assert.That(vm.Chip.Label).IsEqualTo("Queue paused");

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

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void Add(string name, string owners, DemoJobPriority priority, DemoQueueItemState state)
        {
            _items.Add(new DemoQueueItem
            {
                Id = Guid.NewGuid(),
                Path = "/demos/" + name,
                DisplayName = name,
                Owners = owners,
                Priority = priority,
                State = state
            });
            Changed?.Invoke();
        }
    }
}
