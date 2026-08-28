#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the Settings "Replay walkthrough" affordance wiring: the command is only offered when a starter
///     action was injected (<see cref="SettingsViewModel.CanReplayWalkthrough" />), and invoking it both runs
///     the starter AND raises <c>CloseRequested</c> so the tour is visible on the main window (Settings
///     dismisses). With no starter (WASM / degraded host) the affordance is hidden. Pure VM over a temp-dir
///     <see cref="SettingsService" />.
///     <para>
///         <see cref="NotInParallelAttribute" /> despite being a VM-only test: building the VM constructs a
///         <c>ThemeRegistry</c>, which is an Avalonia <c>ResourceDictionary</c> and therefore calls
///         <c>VerifyAccess()</c>. Run concurrently with a class that owns the single headless UI session,
///         that check sees a worker thread and throws "Call from invalid thread". The failure is
///         batch-composition dependent — it surfaced only when adding unrelated test classes reshuffled the
///         partition — so the class passes in isolation and looks like a flake. <c>SettingsViewModelTests</c>
///         constructs the registry the same way and was already marked for the same reason.
///     </para>
/// </summary>
[NotInParallel]
public class TutorialSettingsReplayTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvtutreplay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // Mirrors SettingsViewModelTests.NewVm, plus the optional replayWalkthrough starter the shell injects.
    private static (SettingsViewModel Vm, ServiceProvider Sp) NewVm(string dir, Action? replayWalkthrough)
    {
        SettingsService svc = new(dir);
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        services.AddSingleton<IFeatureGate>(s =>
            new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
        ServiceProvider sp = services.BuildServiceProvider();
        IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
        IFeatureGate gate = sp.GetRequiredService<IFeatureGate>();
        SettingsViewModel vm = new(svc, monitor, gate, new ThemeRegistry(), replayWalkthrough);
        return (vm, sp);
    }

    [Test]
    public async Task WithStarter_CanReplay_AndCommandInvokesStarterAndCloses()
    {
        string dir = NewTempDir();
        try
        {
            int started = 0;
            (SettingsViewModel vm, ServiceProvider sp) = NewVm(dir, () => started++);
            using (sp)
            {
                await Assert.That(vm.CanReplayWalkthrough).IsTrue()
                    .Because("a starter action was wired (the desktop host)");

                bool closed = false;
                vm.CloseRequested += (_, _) => closed = true;

                vm.ReplayWalkthroughCommand.Execute(null);

                await Assert.That(started).IsEqualTo(1).Because("the replay command invokes the injected starter");
                await Assert.That(closed).IsTrue()
                    .Because("replay closes Settings so the tour is visible on the main window");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task WithoutStarter_CannotReplay()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, ServiceProvider sp) = NewVm(dir, null);
            using (sp)
            {
                await Assert.That(vm.CanReplayWalkthrough).IsFalse()
                    .Because("no starter was wired — the affordance is hidden (WASM / degraded host)");
                await Assert.That(vm.ReplayWalkthroughCommand.CanExecute(null)).IsFalse()
                    .Because("CanReplayWalkthrough gates the command");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
