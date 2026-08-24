#region

using System.Text.Json;
using Avalonia.Controls;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Session persistence for MODULE-contributed tabs. The framework has always declared
///     <see cref="IWorkspaceTabViewModel.SnapshotState" /> and never called it, so no module tab's state
///     survived a restart — the Reels clip tray being the first place that is a real loss rather than a
///     theoretical one (a half-built cross-demo reel is minutes of work).
///     <para>
///         These tests pin the framework contract with a FAKE tab, deliberately: the mechanism has to be
///         correct independently of any one module, and the awkward part is not storage but LIFECYCLE —
///         module VMs are lazy, so at restore time the object that should receive the state does not exist.
///     </para>
/// </summary>
/// <remarks>
///     The cases that <c>Activate</c> a tab run on the UI thread: activation realizes the descriptor's
///     View, and constructing a Control verifies dispatcher access. Off-thread they passed only while no
///     dispatch had yet bound the UI thread — a race the assembly warm-up now settles, and the "Call from
///     invalid thread" half of issue #6.
/// </remarks>
public class ModuleTabPersistenceTests
{
    private sealed record FakeState(List<string> Items, int Version);

    private sealed class FakeTabViewModel : IWorkspaceTabViewModel
    {
        public FakeState State { get; set; } = new([], 0);
        public int RestoreCalls { get; private set; }
        public object? LastRestorePayload { get; private set; }

        public void OnActivated(IModuleContext context)
        {
        }

        public void OnDeactivated()
        {
        }

        public object? SnapshotState() => State.Items.Count > 0 ? State : null;

        public void RestoreState(object? state)
        {
            RestoreCalls++;
            LastRestorePayload = state;

            // The contract: what comes back is a JsonElement, not the record that went in.
            if (state is JsonElement element
                && element.Deserialize<FakeState>() is { } restored)
            {
                State = restored;
            }
        }
    }

    private static WorkspaceTabDescriptor Descriptor(Func<IWorkspaceTabViewModel> factory, string id) => new()
    {
        TabId = id,
        Header = id,
        ViewModelFactory = factory,
        ViewFactory = () => new ContentControl()
    };

    /// <summary>
    ///     A tab that was never opened has no VM, and must not be built just to be asked for state — that
    ///     would pay every module's construction cost on every exit, for tabs the user never touched.
    /// </summary>
    [Test]
    public async Task AnUnopenedTab_IsNeverBuiltToBeSnapshotted()
    {
        int built = 0;
        WorkspaceTabDescriptor tab = Descriptor(() =>
        {
            built++;
            return new FakeTabViewModel();
        }, "fake.never-opened");

        // Snapshot the way the shell does: only ask tabs whose VM already exists.
        object? state = tab.TabViewModel?.SnapshotState();

        using (Assert.Multiple())
        {
            await Assert.That(built).IsEqualTo(0);
            await Assert.That(state).IsNull();
        }
    }

    /// <summary>
    ///     THE LIFECYCLE CASE. Restore happens at startup, when a lazy module VM does not exist yet. The
    ///     state is parked on the descriptor and applied the moment the VM is first built — not before
    ///     (nothing to give it) and not by force-building every module (that defeats the laziness).
    /// </summary>
    [Test]
    public async Task ParkedState_IsAppliedWhenTheLazyViewModelIsFirstBuilt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeTabViewModel? created = null;
            WorkspaceTabDescriptor tab = Descriptor(() => created = new FakeTabViewModel(), "fake.lazy");

            tab.PendingRestoreState = JsonSerializer.SerializeToElement(
                new FakeState(["alpha", "bravo"], 3));

            await Assert.That(created).IsNull().Because("parking state must not build the VM");

            tab.Activate(null!);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created!.RestoreCalls).IsEqualTo(1);
                await Assert.That(created.State.Items).IsEquivalentTo(new List<string> { "alpha", "bravo" });
                await Assert.That(created.State.Version).IsEqualTo(3);
                await Assert.That(tab.PendingRestoreState).IsNull()
                    .Because("the snapshot is consumed once");
            }
        });

    /// <summary>
    ///     Re-selecting a tab later in the session must not replay the startup snapshot over state the user
    ///     has changed since. Deactivate/Activate is an ordinary gesture — every tab switch does it.
    /// </summary>
    [Test]
    public async Task ReActivating_DoesNotReplayTheStartupSnapshot() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeTabViewModel? created = null;
            WorkspaceTabDescriptor tab = Descriptor(() => created = new FakeTabViewModel(), "fake.reactivate");
            tab.PendingRestoreState = JsonSerializer.SerializeToElement(new FakeState(["original"], 1));

            tab.Activate(null!);
            created!.State = new FakeState(["user-changed-this"], 9);

            tab.Deactivate();
            tab.Activate(null!);

            using (Assert.Multiple())
            {
                await Assert.That(created.RestoreCalls).IsEqualTo(1);
                await Assert.That(created.State.Items).IsEquivalentTo(new List<string> { "user-changed-this" });
            }
        });

    /// <summary>The payload round-trips through the session record under its stable, name-based TabId.</summary>
    [Test]
    public async Task ModuleState_RoundTripsThroughTheSessionPayload()
    {
        FakeTabViewModel vm = new() { State = new FakeState(["one", "two"], 7) };

        SessionPayload written = new(null, null, null, false, false, "fake.rt",
            new Dictionary<string, JsonElement>
            {
                ["fake.rt"] = JsonSerializer.SerializeToElement(vm.SnapshotState())
            });

        SessionPayload? read = JsonSerializer.Deserialize<SessionPayload>(
            JsonSerializer.Serialize(written));

        FakeTabViewModel restored = new();
        restored.RestoreState(read!.ModuleTabs!["fake.rt"]);

        using (Assert.Multiple())
        {
            await Assert.That(restored.State.Items).IsEquivalentTo(new List<string> { "one", "two" });
            await Assert.That(restored.State.Version).IsEqualTo(7);
        }
    }

    /// <summary>
    ///     A blob written by an older build must not cost the user the TAB.
    ///     <para>
    ///         The fake here is deliberately NAIVE — it calls <c>Deserialize&lt;T&gt;</c> and lets the
    ///         <c>JsonException</c> fly, which is exactly what a module written without reading the contract
    ///         does. So this asserts the FRAMEWORK is resilient, not that well-behaved modules are fine. The
    ///         first version of this test failed for precisely that reason, and the backstop in
    ///         <c>Activate</c> is the fix: without it, a renamed field in one module's state means a tab that
    ///         cannot be opened — or, if it is the restored active tab, a launch that fails.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnIncompatibleBlob_DoesNotCostTheTab() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FakeTabViewModel? created = null;
            WorkspaceTabDescriptor tab = Descriptor(() => created = new FakeTabViewModel(), "fake.legacy");

            // Shape from an imaginary older schema — a bare string where a record is expected.
            tab.PendingRestoreState = JsonSerializer.SerializeToElement("this used to be a state blob");

            tab.Activate(null!);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull().Because("the tab still opened");
                await Assert.That(tab.IsActive).IsTrue();
                await Assert.That(tab.ActiveContent).IsNotNull()
                    .Because("the View is realized even though the restore threw");
                await Assert.That(created!.RestoreCalls).IsEqualTo(1);
                await Assert.That(created.State.Items).IsEmpty();
                await Assert.That(tab.PendingRestoreState).IsNull()
                    .Because("a blob that threw must not be retried on the next activation");
            }
        });

    /// <summary>
    ///     A persisted key whose tab no longer exists (module removed, feature gated off) is simply never
    ///     handed to anyone — it must not throw, and must not leak onto a different tab.
    /// </summary>
    [Test]
    public async Task StateForAVanishedTab_IsIgnored() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            WorkspaceTabDescriptor present = Descriptor(() => new FakeTabViewModel(), "fake.present");

            Dictionary<string, JsonElement> states = new()
            {
                ["fake.present"] = JsonSerializer.SerializeToElement(new FakeState(["mine"], 1)),
                ["fake.removed-module"] = JsonSerializer.SerializeToElement(new FakeState(["orphan"], 1))
            };

            foreach (WorkspaceTabDescriptor tab in new[] { present })
            {
                if (states.TryGetValue(tab.TabId, out JsonElement state))
                {
                    tab.PendingRestoreState = state;
                }
            }

            present.Activate(null!);

            await Assert.That(((FakeTabViewModel)present.TabViewModel!).State.Items)
                .IsEquivalentTo(new List<string> { "mine" });
        });
}
