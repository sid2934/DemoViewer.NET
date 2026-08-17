#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls;
using DemoViewer.NET.ViewModels.Tutorial;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lifecycle coverage of the <see cref="TutorialAnchor" /> attached property: a tagged control registers
///     itself on <c>AttachedToVisualTree</c> and unregisters on detach, so
///     <see cref="TutorialAnchor.TryResolve" /> finds it only while it is live in the tree. This is the seam
///     the overlay depends on to measure a step's spotlight — an inactive tab drops its content, and the
///     registry must not keep pointing at a detached control. The static registry is shared, so this class is
///     <see cref="NotInParallelAttribute" /> and asserts against its OWN mounted control by reference.
/// </summary>
[NotInParallel]
public class TutorialAnchorTests
{
    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [Test]
    public async Task TaggedControl_ResolvesWhileAttached_AndClearsAfterRemovalFromTree()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            const TutorialTarget target = TutorialTarget.StatsContent;

            StackPanel host = new();
            Border anchor = new();
            TutorialAnchor.SetTarget(anchor, target);
            host.Children.Add(anchor);

            Window window = new()
            {
                Width = 400,
                Height = 300,
                Content = host
            };
            window.Show();
            Pump();

            await Assert.That(anchor.GetVisualRoot()).IsNotNull().Because("the anchor is now in the visual tree");
            await Assert.That(TutorialAnchor.TryResolve(target, out Control resolved)).IsTrue()
                .Because("an attached tagged control registers itself");
            await Assert.That(ReferenceEquals(resolved, anchor)).IsTrue()
                .Because("TryResolve returns the live control that registered for this target");

            // Detach it from the tree — the same thing a tab deactivation does to its content root.
            host.Children.Remove(anchor);
            Pump();

            await Assert.That(TutorialAnchor.TryResolve(target, out _)).IsFalse()
                .Because("a detached control unregisters, so the target no longer resolves");
        });
    }

    [Test]
    public async Task UntaggedControl_NeverResolves()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            // A plain control with no TutorialAnchor.Target must not register for any target.
            Border plain = new();
            Window window = new()
            {
                Width = 200,
                Height = 200,
                Content = plain
            };
            window.Show();
            Pump();

            await Assert.That(TutorialAnchor.GetTarget(plain)).IsEqualTo(TutorialTarget.None);
            await Assert.That(TutorialAnchor.TryResolve(TutorialTarget.None, out _)).IsFalse()
                .Because("None is the sentinel for 'no anchor' and never resolves");
        });
    }
}
