#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The module context's demo reset with an extension handler that throws ahead of others: every
///     subscriber still runs, and the throw is counted against the extension. Without a fault tracker the
///     context behaves as before and the throw reaches the raiser.
/// </summary>
[NotInParallel]
public class HostMulticastFaultTests
{
    [Test]
    public async Task DemoReset_ReachesEverySubscriber_PastAThrowingExtensionHandler()
    {
        FaultRig rig = new();
        ModuleContext context = new(new PlaybackController(), () => null);
        context.SetFaults(rig.Faults);
        int after = 0;
        ((IModuleContext)context).DemoReset += () => throw new InvalidOperationException("extension");
        ((IModuleContext)context).DemoReset += () => after++;

        context.RaiseDemoReset();
        context.RaiseDemoReset();

        using (Assert.Multiple())
        {
            await Assert.That(after).IsEqualTo(2);
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task WithoutAFaultTracker_ADemoResetHandlerThrowStillReachesTheRaiser()
    {
        ModuleContext context = new(new PlaybackController(), () => null);
        ((IModuleContext)context).DemoReset += () => throw new InvalidOperationException("host");

        Assert.Throws<InvalidOperationException>(context.RaiseDemoReset);
        await Task.CompletedTask;
    }
}
