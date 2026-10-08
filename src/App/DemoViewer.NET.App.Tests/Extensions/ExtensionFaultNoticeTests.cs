#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     What the user sees when an extension is switched off for the session: the Settings master row says
///     so with the count and the last site, "Turn on again" lifts it, "Keep off" writes the switch off,
///     turning the master switch back on lifts it too, and the shell's banner shows once per switch-off.
/// </summary>
[NotInParallel]
public class ExtensionFaultNoticeTests
{
    private static readonly ExtensionScope StratBook =
        new(StratBookPack.PackId, StratBookPack.PackFeatureId, "Strat Book", typeof(StratBookPack).Assembly);

    private sealed class Rig : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvfaultnotice_" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _sp;

        public Rig()
        {
            Directory.CreateDirectory(_dir);
            Settings = new SettingsService(_dir);
            Settings.Write(s => s.UserCategory = UserCategory.Developer);
            ServiceCollection services = new();
            services.Configure<AppSettings>(Settings.Configuration);
            _sp = services.BuildServiceProvider();
            Monitor = _sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
            Faults = new ExtensionFaults([StratBook], Posts.Post);
            Gate = new FeatureGate(Monitor, false, Faults);
        }

        public IOptionsMonitor<AppSettings> Monitor { get; }

        public PostQueue Posts { get; } = new();
        public SettingsService Settings { get; }
        public ExtensionFaults Faults { get; }
        public FeatureGate Gate { get; }

        public void Trip()
        {
            for (int i = 0; i < ExtensionFaults.WindowLimit; i++)
            {
                Action body = () => throw new InvalidOperationException("boom");
                Faults.Run(StratBook, "library filter", body);
            }

            Posts.Drain();
        }

        public void Dispose()
        {
            Gate.Dispose();
            _sp.Dispose();
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static SettingsViewModel Settings(Rig rig)
    {
        SettingsViewModel vm = new(rig.Settings, rig.Monitor, rig.Gate, new ThemeRegistry(), () => false);
        vm.AttachExtensionFaults(rig.Faults, null);
        return vm;
    }

    private static FeatureToggleRow Master(SettingsViewModel vm) =>
        vm.ExtensionsFeatureRows.Single(r => r.FeatureId == StratBookPack.PackFeatureId);

    [Test]
    public async Task TheMasterRow_SaysTheExtensionWasTurnedOff_AndTurnOnAgainLiftsIt()
    {
        using Rig rig = new();
        using SettingsViewModel vm = Settings(rig);
        FeatureToggleRow master = Master(vm);
        await Assert.That(master.Fault!.IsShown).IsFalse();

        rig.Trip();
        using (Assert.Multiple())
        {
            await Assert.That(master.Fault.IsShown).IsTrue();
            await Assert.That(master.Fault.Text).IsEqualTo("Turned off for this session after 3 errors (last: library filter).");
            await Assert.That(master.Fault.CanTurnOnAgain).IsTrue();
            await Assert.That(master.IsEnabled).IsFalse().Because("the suspension resolves the master switch off");
        }

        master.Fault.TurnOnAgainCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(master.Fault.IsShown).IsFalse();
            await Assert.That(master.IsEnabled).IsTrue();
            await Assert.That(rig.Faults.StateOf(StratBookPack.PackFeatureId).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task KeepOff_WritesTheSwitchOff_AndClearsTheNotice()
    {
        using Rig rig = new();
        using SettingsViewModel vm = Settings(rig);
        FeatureToggleRow master = Master(vm);

        rig.Trip();
        master.Fault!.KeepOffCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(rig.Settings.Current.Features.Overrides[StratBookPack.PackFeatureId]).IsFalse();
            await Assert.That(master.Fault.IsShown).IsFalse();
            await Assert.That(rig.Gate.IsSuspended(StratBookPack.PackFeatureId)).IsFalse();
            await Assert.That(master.IsEnabled).IsFalse().Because("off now comes from the user's own setting");
        }
    }

    [Test]
    public async Task TurningTheMasterSwitchBackOn_LiftsTheSuspension()
    {
        using Rig rig = new();
        rig.Settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = true);
        using SettingsViewModel vm = Settings(rig);
        FeatureToggleRow master = Master(vm);

        rig.Trip();
        await Assert.That(master.IsEnabled).IsFalse();

        master.IsEnabled = true;
        using (Assert.Multiple())
        {
            await Assert.That(rig.Gate.IsSuspended(StratBookPack.PackFeatureId)).IsFalse()
                .Because("the override already said on, so only lifting the suspension can turn it back on");
            await Assert.That(master.IsEnabled).IsTrue();
        }
    }

    // An extension that failed while starting stays off for the session whatever the user does: Keep off
    // writes the switch off, and turning the switch back on does not lift the suspension.
    [Test]
    public async Task AnExtensionThatFailedToStart_StaysSuspended_ThroughKeepOffAndTheMasterSwitch()
    {
        using Rig rig = new();
        rig.Faults.FailStartup(StratBook, "register", new InvalidOperationException("no"));
        rig.Posts.Drain();
        using SettingsViewModel vm = Settings(rig);
        FeatureToggleRow master = Master(vm);
        await Assert.That(master.Fault!.CanTurnOnAgain).IsFalse();

        master.Fault.KeepOffCommand.Execute(null);
        master.IsEnabled = true;

        using (Assert.Multiple())
        {
            await Assert.That(rig.Gate.IsSuspended(StratBookPack.PackFeatureId)).IsTrue();
            await Assert.That(master.IsEnabled).IsFalse();
            await Assert.That(master.Fault.IsShown).IsTrue();
        }
    }

    [Test]
    public async Task TheBanner_ShowsOncePerSwitchOff() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using Rig rig = new();
            MainViewModel shell = new(null, new ModuleRegistry(), TestLibraries.Empty());
            try
            {
                shell.AttachExtensionFaults(rig.Faults);
                await Assert.That(shell.HasExtensionFaultBanner).IsFalse();

                rig.Trip();
                ExtensionFaultNotice banner = shell.ExtensionFaultBanner!;
                await Assert.That(banner.Sentence)
                    .IsEqualTo("Strat Book: Turned off for this session after 3 errors (last: library filter).");

                shell.DismissExtensionFaultBannerCommand.Execute(null);
                Action late = () => throw new InvalidOperationException("after");
                rig.Faults.Run(StratBook, "library filter", late);
                rig.Posts.Drain();
                await Assert.That(shell.HasExtensionFaultBanner).IsFalse().Because("the same switch-off is not announced twice");

                rig.Faults.Resume(StratBookPack.PackFeatureId);
                rig.Trip();
                await Assert.That(shell.HasExtensionFaultBanner).IsTrue().Because("a new switch-off gets its own banner");

                shell.ExtensionFaultBanner!.TurnOnAgainCommand.Execute(null);
                using (Assert.Multiple())
                {
                    await Assert.That(shell.HasExtensionFaultBanner).IsFalse();
                    await Assert.That(rig.Gate.IsSuspended(StratBookPack.PackFeatureId)).IsFalse();
                }
            }
            finally
            {
                shell.Dispose();
            }
        });
}
