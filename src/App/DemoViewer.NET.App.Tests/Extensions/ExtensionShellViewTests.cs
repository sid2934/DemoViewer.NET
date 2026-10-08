#region

using Avalonia.Threading;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The open demo and the playhead as an extension's <see cref="IExtensionShell" /> reports them, over a real
///     shell and the committed tour sample: the parse, its content key and its path move together through load,
///     reload and close, and the playhead event follows seeks and the play state, coalesced to one raise per
///     dispatcher pump.
/// </summary>
[NotInParallel]
public class ExtensionShellViewTests
{
    [Test]
    public async Task TheShellView_ReportsTheOpenDemo_ThroughLoadReloadAndClose()
    {
        string sample = Sample();

        await HeadlessSession.RunOnUi(async () =>
        {
            using MainViewModel vm = new(library: TestLibraries.Empty());
            (IExtensionShell shell, _) = Attach(vm);
            List<(ParsedDemo? Demo, string? Hash, string? Path)> seen = [];
            shell.CurrentDemoChanged += () => seen.Add((shell.CurrentDemo, shell.CurrentDemoHash, shell.CurrentDemoPath));

            await Assert.That(shell.CurrentDemo).IsNull();
            await Assert.That(shell.CurrentDemoHash).IsNull();

            await vm.AutoLoadDemoAsync(sample);
            await Assert.That(seen).IsNotEmpty();
            (ParsedDemo? first, string? hash, string? path) = seen[^1];
            using (Assert.Multiple())
            {
                await Assert.That(first).IsNotNull().Because("the change fires after the parse is in place");
                await Assert.That(path).IsEqualTo(sample);
                await Assert.That(hash).IsEqualTo(Playback2D.Pipeline.DemoContentHash.Compute(sample))
                    .Because("the content key is the library's lowercase-hex SHA-256 of the file");
                await Assert.That(shell.CurrentDemo).IsSameReferenceAs(first);
            }

            seen.Clear();
            await vm.AutoLoadDemoAsync(sample);
            await Assert.That(seen).IsNotEmpty();
            using (Assert.Multiple())
            {
                await Assert.That(seen[^1].Demo).IsNotNull();
                await Assert.That(seen[^1].Demo).IsNotSameReferenceAs(first).Because("a reload hands out the new parse");
                await Assert.That(seen[^1].Hash).IsEqualTo(hash);
            }

            seen.Clear();
            await vm.CloseDemoCommand.ExecuteAsync(null);
            await Assert.That(seen).IsNotEmpty();
            using (Assert.Multiple())
            {
                await Assert.That(seen[^1].Demo).IsNull();
                await Assert.That(seen[^1].Hash).IsNull();
                await Assert.That(shell.CurrentDemo).IsNull();
                await Assert.That(shell.CurrentTick).IsEqualTo(0);
            }
        });
    }

    [Test]
    public async Task PlayheadChanged_FollowsSeeksAndThePlayState_OncePerPump()
    {
        string sample = Sample();

        await HeadlessSession.RunOnUi(async () =>
        {
            using MainViewModel vm = new(library: TestLibraries.Empty());
            (IExtensionShell shell, _) = Attach(vm);
            await vm.AutoLoadDemoAsync(sample);
            Dispatcher.UIThread.RunJobs();

            List<(int Tick, bool Playing)> seen = [];
            Action record = () => seen.Add((shell.CurrentTick, shell.IsPlaying));
            shell.PlayheadChanged += record;

            int target = vm.Playback.Frames![vm.Playback.TotalFrames / 2].ServerTick;
            vm.Playback.SeekToTick(target);
            vm.Playback.SeekToTick(target + 64);
            await Assert.That(seen).IsEmpty().Because("the raise is posted, not run inside the seek");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(seen.Count).IsEqualTo(1).Because("two seeks in one pump raise once");
            await Assert.That(seen[0].Tick).IsGreaterThanOrEqualTo(target + 64);
            await Assert.That(seen[0].Tick).IsEqualTo(vm.Playback.CurrentTick);

            seen.Clear();
            vm.Playback.IsPlaying = true;
            Dispatcher.UIThread.RunJobs();
            vm.Playback.IsPlaying = false;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(seen.Select(s => s.Playing)).IsEquivalentTo([true, false]);

            seen.Clear();
            shell.PlayheadChanged -= record;
            vm.Playback.SeekToTick(target);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(seen).IsEmpty().Because("an unsubscribed handler hears nothing");
        });
    }

    [Test]
    public async Task AThrowingPlayheadHandler_CountsOnceAMinute_AndTheNextHandlerStillRuns()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using MainViewModel vm = new(library: TestLibraries.Empty());
            (IExtensionShell shell, FaultRig rig) = Attach(vm);
            int after = 0;
            shell.PlayheadChanged += () => throw new InvalidOperationException("broken overlay");
            shell.PlayheadChanged += () => after++;

            for (int i = 1; i <= 5; i++)
            {
                vm.Playback.CurrentTick = i * 64;
                Dispatcher.UIThread.RunJobs();
            }

            using (Assert.Multiple())
            {
                await Assert.That(after).IsEqualTo(5);
                await Assert.That(rig.Faults.StateOf(rig.Scope.FeatureId).Count).IsEqualTo(1)
                    .Because("a per-frame site counts once per exception type a minute");
                await Assert.That(rig.Switch.Suspensions).IsEmpty();
            }
        });
    }

    private static string Sample()
    {
        string? saved = Environment.GetEnvironmentVariable(TourDemoLocator.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, null);
            return TourDemoLocator.FindSampleDemo() ?? throw new InvalidOperationException("the committed tour sample is missing");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, saved);
        }
    }

    private static (IExtensionShell Shell, FaultRig Rig) Attach(MainViewModel vm)
    {
        FaultRig rig = new();
        ExtensionShellHub hub = new();
        hub.Attach(vm);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(hub)
            .AddSingleton(rig.Faults)
            .BuildServiceProvider();
        ExtensionContext context = new(new StubExtension(rig.Scope.FeatureId), services);
        return (context.Shell, rig);
    }
}
