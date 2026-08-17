#region

using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The window title doubles as a diagnostics readout (PID · CPU · RAM). The PID is there so a running
///     instance can be handed straight to <c>dotnet-gcdump</c> / <c>dotnet-dump</c> / <c>footprint</c> —
///     picking it out of <c>ps</c> is ambiguous while a test host or a second build is running.
/// </summary>
[NotInParallel]
public class WindowTitleTests
{
    [Test]
    public async Task WindowTitle_CarriesPidCpuAndRam()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());

            // The readout is produced by a 1s DispatcherTimer tick, so wait for a real one rather than
            // asserting the initial value — this checks the string the user actually sees.
            string title = "";
            for (int i = 0; i < 40 && !title.Contains("PID", StringComparison.Ordinal); i++)
            {
                await Task.Delay(100);
                title = vm.WindowTitle;
            }

            using (Assert.Multiple())
            {
                await Assert.That(title)
                    .Contains($"PID {Environment.ProcessId}")
                    .Because("the title must name THIS process so it can be attached to directly");
                await Assert.That(title).Contains("CPU");
                await Assert.That(title).Contains("RAM");
            }

            vm.Dispose();
        });
    }
}
