#region

using System.Runtime.CompilerServices;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The close-demo memory gate, run with the shell WIRED the way the real app wires it:
///     a real <see cref="DemoProcessingQueue" /> and <see cref="DemoScheduler" />.
///     <para>
///         <see cref="MemoryReleaseTests" /> leaves both null (the default ctor args), which is exactly why
///         it stayed green while the shipped app retained ~3.6 GB after a close: the open routes through
///         the open item and hands the parsed demo to the open's pass run on a worker, and the queue keeps
///         terminal entries in a 30-deep history. None of that
///         machinery exists in the unwired fixture, so no test could see a root that lives inside it.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class MemoryReleaseWiredTests
{
    [Test]
    public async Task CloseDemo_WithRealQueueAndCoordinator_ReleasesTheDemo()
    {
        string demo = DemoTestHelper.RequireDemo();
        WeakReference? parsedRef = null;
        WeakReference? frameRef = null;

        await HeadlessSession.RunOnUi(async () =>
        {
            HeavyJobGate gate = new();
            DemoProcessingQueue queue = new(gate, a => a());
            DemoScheduler coordinator = new([], queue, () => []);

            MainViewModel? vm = new(
                library: TestLibraries.Empty(),
                heavyJobGate: gate,
                processingQueue: queue,
                scheduler: coordinator);

            await vm.AutoLoadDemoAsync(demo);
            (parsedRef, frameRef) = Capture(vm);

            await vm.CloseDemoCommand.ExecuteAsync(null);
            vm = null;

            // Give any fire-and-forget post-open work its chance to finish and drop its capture: the
            // documented "release may be a few seconds late" window. If the demo is still alive after
            // this, it is held by a durable root, not by in-flight work.
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200);
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
            }

            queue.Dispose();
        });

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        using (Assert.Multiple())
        {
            await Assert.That(parsedRef!.IsAlive)
                .IsFalse()
                .Because("with the queue + coordinator wired (the real app's configuration) a closed demo "
                         + "must still be collectable — this is the configuration that retained 3.6 GB");
            await Assert.That(frameRef!.IsAlive)
                .IsFalse()
                .Because("one live frame pins the whole demo byte buffer via zero-copy slicing");
        }
    }

    [Test]
    public async Task AnOpenThroughTheQueue_EndsAsACompletedItem_AndTheClosedDemoIsReleased()
    {
        string demo = DemoTestHelper.RequireDemo();
        WeakReference? parsedRef = null;
        DemoQueueItemSnapshot? open = null;

        await HeadlessSession.RunOnUi(async () =>
        {
            HeavyJobGate gate = new();
            DemoProcessingQueue queue = new(gate, a => a());
            DemoScheduler coordinator = new([], queue, () => []);
            MainViewModel? vm = new(
                library: TestLibraries.Empty(),
                heavyJobGate: gate,
                processingQueue: queue,
                scheduler: coordinator);

            await vm.LoadDemoFromPathAsync(demo);
            parsedRef = Capture(vm).Parsed;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline
                   && queue.Snapshot().First(s => s.Kind == QueueJobKind.DemoOpen).State == DemoQueueItemState.Running)
            {
                await Task.Delay(50);
            }

            open = queue.Snapshot().First(s => s.Kind == QueueJobKind.DemoOpen);
            await vm.CloseDemoCommand.ExecuteAsync(null);
            vm = null;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200);
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
            }

            queue.Dispose();
        });

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        using (Assert.Multiple())
        {
            await Assert.That(open!.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(open.DisplayName).IsEqualTo("Open demo: " + Path.GetFileName(demo));
            await Assert.That(parsedRef!.IsAlive).IsFalse().Because("the finished open item must not root the demo");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Parsed, WeakReference FirstFrame) Capture(MainViewModel vm)
    {
        ParsedDemo parsed = ((ICurrentDemoSource)vm.ModuleContext!).CurrentDemo!;
        return (new WeakReference(parsed), new WeakReference(parsed.Frames[0]));
    }
}
