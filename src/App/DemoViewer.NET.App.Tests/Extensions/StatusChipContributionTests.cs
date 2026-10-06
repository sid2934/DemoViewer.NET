#region

using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's status chip through the public contribution: the host owns the chip the strip binds,
///     copies the extension's state into it on the UI thread, and contains a source or a command that throws.
/// </summary>
[NotInParallel]
public class StatusChipContributionTests
{
    private const string PackFeature = "pack.chip";

    private static (MainViewModel Shell, HostStatusChip Chip, ExtensionFaults Faults, FakeGate Gate) NewShell(FakeSource source)
    {
        ChipPack pack = new(source);
        ExtensionFaults faults = ExtensionFaults.For([pack], static a => a());
        ServiceCollection services = new();
        services.AddSingleton(faults);
        PackContributionSet set = new([pack], services.BuildServiceProvider());
        FakeGate gate = new();
        MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(), null, gate);
        vm.AttachStatusChips(set.StatusChips);
        return (vm, set.StatusChips.Single(), faults, gate);
    }

    [Test]
    public async Task TheHostOwnsTheChip_AndCopiesTheSourcesState() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new() { Shown = true, Label = "Syncing", Dot = StatusChipDotState.Working };
            (MainViewModel vm, HostStatusChip chip, _, FakeGate gate) = NewShell(source);
            try
            {
                using (Assert.Multiple())
                {
                    await Assert.That(chip.FeatureId).IsEqualTo(PackFeature).Because("a null feature id takes the owning pack's");
                    await Assert.That(vm.Chips).Contains(chip.Chip);
                    await Assert.That(chip.Chip.Label).IsEqualTo("Syncing");
                    await Assert.That(chip.Chip.IsStateWorking).IsTrue();
                }

                source.Label = "Synced";
                source.Dot = StatusChipDotState.Good;
                source.Raise();
                await Assert.That(chip.Chip.Label).IsEqualTo("Synced");

                gate.Answers[PackFeature] = false;
                gate.RaiseChanged();
                await Assert.That(vm.Chips).DoesNotContain(chip.Chip).Because("an extension that is off shows no chip");

                gate.Answers.Remove(PackFeature);
                gate.RaiseChanged();
                await Assert.That(vm.Chips).Contains(chip.Chip);

                source.Shown = false;
                source.Raise();
                await Assert.That(vm.Chips).DoesNotContain(chip.Chip);
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task AChangeRaisedOffTheUiThread_IsAppliedOnTheUiThread() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new();
            (MainViewModel vm, HostStatusChip chip, _, _) = NewShell(source);
            try
            {
                int uiThread = Environment.CurrentManagedThreadId;
                int? appliedOn = null;
                chip.Chip.PropertyChanged += (_, _) => appliedOn ??= Environment.CurrentManagedThreadId;

                // A dedicated thread, joined: awaiting a pool task lets the dispatcher run the posted re-read
                // before the check below, and blocking on one can run it inline on this thread.
                int raisedOn = 0;
                Thread worker = new(() =>
                {
                    raisedOn = Environment.CurrentManagedThreadId;
                    source.Shown = true;
                    source.Label = "From a worker";
                    source.Raise();
                });
                worker.Start();
                worker.Join();
                bool shownBeforeDrain = vm.Chips.Contains(chip.Chip);
                int? appliedBeforeDrain = appliedOn;

                Dispatcher.UIThread.RunJobs();
                using (Assert.Multiple())
                {
                    await Assert.That(raisedOn).IsNotEqualTo(uiThread);
                    await Assert.That(shownBeforeDrain).IsFalse().Because("nothing is applied on the raising thread");
                    await Assert.That(appliedBeforeDrain).IsNull().Because("nothing is applied on the raising thread");
                    await Assert.That(vm.Chips).Contains(chip.Chip);
                    await Assert.That(chip.Chip.Label).IsEqualTo("From a worker");
                    await Assert.That(appliedOn).IsEqualTo(uiThread);
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task ManyChangesRaisedOffTheUiThread_ReadTheSourceOncePerPost() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new() { Shown = true };
            (MainViewModel vm, HostStatusChip _, _, _) = NewShell(source);
            try
            {
                Dispatcher.UIThread.RunJobs();
                int before = source.LabelReads;
                Thread worker = new(() =>
                {
                    for (int i = 0; i < 200; i++)
                    {
                        source.Raise();
                    }
                });
                worker.Start();
                worker.Join();
                Dispatcher.UIThread.RunJobs();

                await Assert.That(source.LabelReads - before).IsEqualTo(1).Because("the raises coalesce into one queued refresh");
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task AChipsClick_RunsItsAction_AndOpensAFlyoutOnlyWhenThereIsContent() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            CountingCommand action = new();
            StatusChipViewModel model = new() { PrimaryAction = action };
            StatusChip control = new() { DataContext = model };
            Button body = control.FindControl<Button>("Body")!;

            using (Assert.Multiple())
            {
                await Assert.That(body.Command).IsSameReferenceAs(action);
                await Assert.That(body.Flyout).IsNull().Because("an empty card is never opened");
            }

            model.FlyoutContent = new object();
            await Assert.That(body.Flyout).IsNotNull();
            model.FlyoutContent = null;
            await Assert.That(body.Flyout).IsNull();
        });

    [Test]
    public async Task AnExportChipWhoseIdAContributedChipHolds_IsLeftOut_AndNoSlotIsOrphaned() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new() { Shown = true, Label = "Mine" };
            (MainViewModel vm, HostStatusChip chip, _, _) = NewShell(source);
            using Playback2DExportStatusViewModel status = new(new IdleExportJob());
            try
            {
                int before = vm.Chips.Count;
                vm.MountExportStatus("test.chip", PackFeature, status);
                vm.MountExportStatus("test.chip", PackFeature, status);

                using (Assert.Multiple())
                {
                    await Assert.That(vm.Chips.Count).IsEqualTo(before);
                    await Assert.That(vm.Chips).Contains(chip.Chip).Because("the contributed chip keeps its id");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    [Test]
    public async Task ASourceThatThrows_HidesItsChip_AndCountsAFault_AndACommandThatThrowsIsContained() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeSource source = new() { Shown = true, Label = "Ok", Action = new ThrowingCommand() };
            (MainViewModel vm, HostStatusChip chip, ExtensionFaults faults, _) = NewShell(source);
            try
            {
                await Assert.That(vm.Chips).Contains(chip.Chip);

                chip.Chip.PrimaryAction!.Execute(null);
                await Assert.That(faults.StateOf(PackFeature).Count).IsEqualTo(1)
                    .Because("the click ran the extension's command under its guard");

                source.ThrowOnRead = true;
                source.Raise();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Chips).DoesNotContain(chip.Chip)
                        .Because("a chip whose state cannot be read is hidden, not half-shown");
                    await Assert.That(faults.StateOf(PackFeature).Count).IsEqualTo(2);
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    private sealed class FakeSource : IStatusChipSource
    {
        public bool ThrowOnRead { get; set; }

        public bool Shown { get; set; }

        public StatusChipDotState Dot { get; set; }

        public ICommand? Action { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsShown => Shown;

        private string _label = "";

        public int LabelReads;

        public string Label
        {
            get
            {
                Interlocked.Increment(ref LabelReads);
                return ThrowOnRead ? throw new InvalidOperationException("chip label") : _label;
            }
            set => _label = value;
        }

        public StatusChipDotState DotState => Dot;

        public bool IsPulsing => false;

        public bool IsHollow => false;

        public string? Tooltip => null;

        public ICommand? PrimaryAction => Action;

        public object? FlyoutContent => null;

        public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private sealed class IdleExportJob : Services.Export.IExportJobService
    {
        public Services.Export.ExportJobStatus Status => Services.Export.ExportJobStatus.Idle;

        public event EventHandler<Services.Export.ExportJobStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Start(Services.Export.Scene2DExportRequest request)
        {
        }

        public Task CancelAsync() => Task.CompletedTask;
    }

    private sealed class CountingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }

    private sealed class ThrowingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => throw new InvalidOperationException("chip click");
    }

    private sealed class ChipPack(FakeSource source) : IExtension
    {
        public string Id => "net.demoviewer.test.chip";
        public string FeatureId => PackFeature;
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services) =>
            contributions.StatusChip(new StatusChipContribution("test.chip", source));
    }

    private sealed class FakeGate : IFeatureGate
    {
        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => !Answers.TryGetValue(featureId, out bool value) || value;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
