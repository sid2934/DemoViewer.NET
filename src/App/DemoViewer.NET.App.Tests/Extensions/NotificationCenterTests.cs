#region

using System.Diagnostics;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Extension notifications: the host stamps the source, caps each extension and drops its oldest, coalesces
///     a flood into one UI post, never throws back, and shows a card only while its extension is on.
/// </summary>
public class NotificationCenterTests
{
    private static Notification Note(string id, string title = "Title", NotificationSeverity severity = NotificationSeverity.Info) =>
        new(id, severity, title, "Body");

    [Test]
    public async Task APost_ShowsACard_StampedWithTheExtension()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        FaultRig rig = new("pack.one");
        IExtensionNotifications notes = center.For(rig.Guard);

        notes.Post(Note("a", "Hello", NotificationSeverity.Warning));
        await Assert.That(center.Cards).IsEmpty().Because("nothing shows before the UI thread drains");
        posts.Drain();

        NotificationCardViewModel card = center.Cards.Single();
        using (Assert.Multiple())
        {
            await Assert.That(card.SourceName).IsEqualTo("Fake");
            await Assert.That(card.Title).IsEqualTo("Hello");
            await Assert.That(card.Body).IsEqualTo("Body");
            await Assert.That(card.IsWarning).IsTrue();
            await Assert.That(card.HasAction).IsFalse();
        }
    }

    [Test]
    public async Task OneExtension_KeepsAtMostTheCap_DroppingItsOldestFirst()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        IExtensionNotifications notes = center.For(new FaultRig().Guard);

        for (int i = 0; i < 5; i++)
        {
            notes.Post(Note("n" + i));
            posts.Drain();
        }

        await Assert.That(string.Join(",", center.Cards.Select(c => c.Id))).IsEqualTo("n4,n3,n2")
            .Because("newest first, and the two oldest went");
    }

    [Test]
    public async Task AFloodFromAWorkerThread_NeverBlocks_CostsOneUiPost_AndLeavesTheOtherExtensionsCard()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        IExtensionNotifications quiet = center.For(new FaultRig("pack.quiet").Guard);
        IExtensionNotifications loud = center.For(new FaultRig("pack.loud").Guard);
        quiet.Post(Note("quiet"));
        posts.Drain();
        int before = center.DrainsPosted;

        Stopwatch watch = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            for (int i = 0; i < 100_000; i++)
            {
                loud.Post(Note("loud" + i));
            }
        });
        watch.Stop();
        int drains = center.DrainsPosted - before;
        posts.Drain();

        using (Assert.Multiple())
        {
            await Assert.That(drains).IsEqualTo(1).Because("posts coalesce into one drain until it runs");
            await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
            await Assert.That(center.Cards.Count(c => c.Id.StartsWith("loud", StringComparison.Ordinal)))
                .IsEqualTo(NotificationCenter.PerExtensionCap);
            await Assert.That(center.Cards.Select(c => c.Id)).Contains("quiet")
                .Because("the cap is per extension, so a flood never pushes another's card out");
            await Assert.That(center.Cards.Select(c => c.Id)).Contains("loud99999");
        }
    }

    [Test]
    public async Task ASecondPostUnderTheSameId_ReplacesTheCardInPlace()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        IExtensionNotifications notes = center.For(new FaultRig().Guard);
        notes.Post(Note("same", "First"));
        notes.Post(Note("other"));
        posts.Drain();
        NotificationCardViewModel first = center.Cards.Single(c => c.Id == "same");

        notes.Post(Note("same", "Second", NotificationSeverity.Error));
        posts.Drain();

        using (Assert.Multiple())
        {
            await Assert.That(center.Cards.Count).IsEqualTo(2);
            await Assert.That(center.Cards.Single(c => c.Id == "same")).IsSameReferenceAs(first);
            await Assert.That(first.Title).IsEqualTo("Second");
            await Assert.That(first.IsError).IsTrue();
        }
    }

    [Test]
    public async Task Dismiss_ClosesTheCard_AndOnlyTheExtensionsOwn()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        IExtensionNotifications one = center.For(new FaultRig("pack.one").Guard);
        IExtensionNotifications two = center.For(new FaultRig("pack.two").Guard);
        one.Post(Note("shared"));
        two.Post(Note("shared"));
        posts.Drain();

        one.Dismiss("shared");
        posts.Drain();

        await Assert.That(center.Cards.Single().Owner.FeatureId).IsEqualTo("pack.two");
    }

    [Test]
    public async Task ATimeToLive_ClosesTheCardOnceItRunsOut()
    {
        PostQueue posts = new();
        ManualClock clock = new();
        NotificationCenter center = new(posts.Post, time: clock);
        IExtensionNotifications notes = center.For(new FaultRig().Guard);
        notes.Post(Note("short") with { TimeToLive = TimeSpan.FromSeconds(5) });
        notes.Post(Note("kept"));
        posts.Drain();

        clock.Advance(TimeSpan.FromSeconds(4));
        center.Expire();
        await Assert.That(center.Cards.Count).IsEqualTo(2);

        clock.Advance(TimeSpan.FromSeconds(2));
        center.Expire();
        await Assert.That(center.Cards.Select(c => c.Id)).IsEquivalentTo(["kept"]);
    }

    [Test]
    public async Task Garbage_IsDropped_AndNeverThrowsBack()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        IExtensionNotifications notes = center.For(new FaultRig().Guard);

        notes.Post(null!);
        notes.Post(new Notification("", NotificationSeverity.Info, "No id"));
        notes.Post(new Notification("x", NotificationSeverity.Info, " "));
        notes.Post(new Notification(null!, NotificationSeverity.Info, null!));
        notes.Dismiss(null!);
        notes.Dismiss("nothing");
        posts.Drain();

        await Assert.That(center.Cards).IsEmpty();
    }

    [Test]
    public async Task AThrowingAction_CountsAgainstTheExtension_AndStillClosesTheCard()
    {
        PostQueue posts = new();
        NotificationCenter center = new(posts.Post);
        FaultRig rig = new();
        IExtensionNotifications notes = center.For(rig.Guard);
        notes.Post(Note("act") with { Action = new NotificationAction("Go", () => throw new InvalidOperationException("boom")) });
        posts.Drain();
        NotificationCardViewModel card = center.Cards.Single();

        card.RunActionCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(rig.Faults.StateOf(rig.Scope.FeatureId).Count).IsEqualTo(1);
            await Assert.That(center.Cards).IsEmpty();
        }
    }

    [Test]
    public async Task AnExtensionThatIsOff_ShowsNothing_AndSwitchingItOffClosesItsCards()
    {
        PostQueue posts = new();
        FakeGate gate = new();
        NotificationCenter center = new(posts.Post, gate);
        IExtensionNotifications on = center.For(new FaultRig("pack.on").Guard);
        IExtensionNotifications off = center.For(new FaultRig("pack.off").Guard);
        gate.Answers["pack.off"] = false;
        off.Post(Note("hidden"));
        on.Post(Note("shown"));
        posts.Drain();
        await Assert.That(center.Cards.Select(c => c.Id)).IsEquivalentTo(["shown"]);

        // A suspension for faults goes through the same gate, so it closes the cards the same way.
        gate.Answers["pack.on"] = false;
        gate.RaiseChanged();
        posts.Drain();
        await Assert.That(center.Cards).IsEmpty();
    }

    // A flood reaches the shell's stack capped, and the strip's chips are not touched.
    [Test]
    [NotInParallel]
    public async Task AFloodingExtension_IsCappedInTheShellsStack_AndTheStripsChipsStay() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            PostQueue posts = new();
            FakeGate gate = new();
            NotificationCenter center = new(posts.Post, gate);
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(), null, gate);
            try
            {
                vm.AttachNotifications(center);
                int chips = vm.Chips.Count;
                IExtensionNotifications loud = center.For(new FaultRig("pack.loud").Guard);
                for (int i = 0; i < 10_000; i++)
                {
                    loud.Post(Note("loud" + i));
                }

                posts.Drain();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Notifications).IsSameReferenceAs(center.Cards);
                    await Assert.That(vm.Notifications.Count).IsEqualTo(NotificationCenter.PerExtensionCap);
                    await Assert.That(vm.Chips.Count).IsEqualTo(chips);
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

    // Composed the way the app composes it, an extension's context posts into the shell's one center.
    [Test]
    [NotInParallel]
    public async Task TheContext_PostsIntoTheComposedCenter()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-notes-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, root);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                // Not a pack.* id: the gate answers an unlisted one on, as it would an extension's listed switch.
                StubExtension pack = new("ext.notes");
                ServiceCollection services = App.ComposeServices(new Services.DesktopWindowService(() => null), [pack]);
                await using ServiceProvider provider = services.BuildServiceProvider();
                NotificationCenter center = provider.GetRequiredService<NotificationCenter>();

                provider.GetExtensionContext(pack.Id).Notifications.Post(Note("composed"));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                await Assert.That(center.Cards.Select(c => c.Id)).IsEquivalentTo(["composed"]);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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
