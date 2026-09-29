#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An open whose file read finishes after a newer open has already landed must leave the shell to the
///     newer open. The fake parse fails, so the newer open ends with its path and its error on the shell.
/// </summary>
[NotInParallel]
public class ShellOpenSupersedeTests
{
    [Test]
    public async Task ASlowReadFinishingLate_DoesNotResetTheShellUnderANewerOpen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-open-supersede-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.bin");
        string b = Path.Combine(dir, "b.bin");
        await File.WriteAllBytesAsync(a, [1]);
        await File.WriteAllBytesAsync(b, [1, 2, 3]);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, a => a(),
                    parseBytes: bytes => throw new InvalidDataException("not a demo"),
                    compactHeap: () => Task.CompletedTask);
                MainViewModel vm = new(library: TestLibraries.Empty(), heavyJobGate: gate, processingQueue: queue);
                TaskCompletionSource<byte[]> slowA = new(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.ReadDemoBytes = (path, ct) => path == a ? slowA.Task : File.ReadAllBytesAsync(path, ct);

                Task openA = vm.LoadDemoFromPathAsync(a);
                await vm.LoadDemoFromPathAsync(b);
                string afterB = vm.StatusText;

                // The read ignores the token and hands its bytes back late.
                slowA.SetResult([9]);
                await openA.WaitAsync(TimeSpan.FromSeconds(10));
                IReadOnlyList<DemoQueueItemSnapshot> items = queue.Snapshot();

                using (Assert.Multiple())
                {
                    await Assert.That(vm.LoadedDemoPath).IsEqualTo(b);
                    await Assert.That(vm.StatusText).IsEqualTo(afterB);
                    await Assert.That(vm.IsLoading).IsFalse();
                    await Assert.That(items.Single(s => s.DisplayName == "Open demo: a.bin").State)
                        .IsEqualTo(DemoQueueItemState.Cancelled);
                    await Assert.That(items.Single(s => s.DisplayName == "Open demo: b.bin").State)
                        .IsEqualTo(DemoQueueItemState.Failed);
                }
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
