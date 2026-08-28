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
///     The close-demo memory gate, run with the shell WIRED the way the real app wires it —
///     a real <see cref="DemoProcessingQueue" /> and <see cref="DemoEvaluationCoordinator" />.
///     <para>
///         <see cref="MemoryReleaseTests" /> leaves both null (the default ctor args), which is exactly why
///         it stayed green while the shipped app retained ~3.6 GB after a close: the open routes through
///         <c>RequestForegroundAsync</c> and hands the parsed demo to a fire-and-forget
///         <c>FanOutParsed</c>, and the queue keeps terminal entries in a 30-deep history. None of that
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
            DemoEvaluationCoordinator coordinator = new([], queue, () => []);

            MainViewModel? vm = new(
                library: TestLibraries.Empty(),
                heavyJobGate: gate,
                processingQueue: queue,
                evaluationCoordinator: coordinator);

            await vm.AutoLoadDemoAsync(demo);
            (parsedRef, frameRef) = Capture(vm);

            await vm.CloseDemoCommand.ExecuteAsync(null);
            vm = null;

            // Give any fire-and-forget post-open work its chance to finish and drop its capture — the
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Parsed, WeakReference FirstFrame) Capture(MainViewModel vm)
    {
        ParsedDemo parsed = ((ICurrentDemoSource)vm.ModuleContext!).CurrentDemo!;
        return (new WeakReference(parsed), new WeakReference(parsed.Frames[0]));
    }
}
