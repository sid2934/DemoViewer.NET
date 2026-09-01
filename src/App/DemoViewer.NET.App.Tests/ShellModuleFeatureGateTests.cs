#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The module-facing gate projection (registry §3.10). Four properties: it delegates, it folds the
///     desktop-only AND in exactly one place, it re-raises <c>Changed</c>, and a module with no gate at all
///     sees everything.
/// </summary>
public class ShellModuleFeatureGateTests
{
    [Test]
    public async Task Delegates_ToUnderlyingGate()
    {
        FakeGate gate = new();
        gate.Answers["playback2d.follow"] = false;
        gate.Answers["playback2d.timeline"] = true;

        using ShellModuleFeatureGate projection = new(gate);

        await Assert.That(projection.IsEnabled("playback2d.follow")).IsFalse();
        await Assert.That(projection.IsEnabled("playback2d.timeline")).IsTrue();
    }

    /// <summary>
    ///     The WASM branch, driven through the internal <c>Func&lt;bool&gt;</c> seam.
    ///     <c>OperatingSystem.IsBrowser()</c> is a JIT-folded intrinsic, so it cannot be faked from
    ///     outside, and a desktop-only gate never proved to close is a gate no test actually closes.
    /// </summary>
    [Test]
    public async Task DesktopOnlyId_IsFalse_OnBrowser_TrueOtherwise()
    {
        FakeGate gate = new();
        gate.Answers["playback2d.export"] = true;

        using ShellModuleFeatureGate onBrowser = new(gate, static () => true);
        using ShellModuleFeatureGate onDesktop = new(gate, static () => false);

        await Assert.That(onBrowser.IsEnabled("playback2d.export")).IsFalse()
            .Because("the browser head has no filesystem and no ffmpeg subprocess");
        await Assert.That(onDesktop.IsEnabled("playback2d.export")).IsTrue();

        // The AND is per-id: a browser host must not lose the other four.
        gate.Answers["playback2d.annotations"] = true;
        await Assert.That(onBrowser.IsEnabled("playback2d.annotations")).IsTrue();
    }

    /// <summary>A user override cannot resurrect export on the browser: the platform AND is not a default.</summary>
    [Test]
    public async Task DesktopOnlyId_StaysFalse_OnBrowser_EvenWhenTheGateSaysYes()
    {
        FakeGate gate = new();
        gate.Answers["playback2d.export"] = true;

        using ShellModuleFeatureGate projection = new(gate, static () => true);

        await Assert.That(projection.IsEnabled("playback2d.export")).IsFalse();
    }

    [Test]
    public async Task Changed_ReRaised_FromUnderlyingGate()
    {
        FakeGate gate = new();
        using ShellModuleFeatureGate projection = new(gate);

        int raised = 0;
        projection.Changed += () => raised++;

        gate.RaiseChanged();
        gate.RaiseChanged();

        await Assert.That(raised).IsEqualTo(2);
    }

    /// <summary>After disposal the projection must be off the gate's event, or a closed tab keeps firing.</summary>
    [Test]
    public async Task Dispose_Unsubscribes()
    {
        FakeGate gate = new();
        ShellModuleFeatureGate projection = new(gate);

        int raised = 0;
        projection.Changed += () => raised++;
        projection.Dispose();

        gate.RaiseChanged();

        await Assert.That(raised).IsEqualTo(0);
    }

    /// <summary>
    ///     Null fails OPEN, at both levels: a null underlying gate (designer / unit-test path), and an
    ///     <see cref="IModuleContext" /> that never had <c>SetFeatures</c> called on it: the default
    ///     interface implementation returns null and a module then shows everything, exactly as it did
    ///     before gating existed.
    /// </summary>
    [Test]
    public async Task NullFeatures_FailOpen()
    {
        using ShellModuleFeatureGate nullGate = new(null);
        await Assert.That(nullGate.IsEnabled("playback2d.export")).IsTrue();

        Playback2DFakeContext ungated = new()
        {
            Gate = null
        };
        await Assert.That(ungated.Features).IsNull()
            .Because("IModuleContext.Features is default-implemented as null so no test double broke");
        await Assert.That(ungated.Features?.IsEnabled("playback2d.annotations") ?? true).IsTrue()
            .Because("a module with no gate shows everything, exactly as it did before gating existed");
    }

    private sealed class FakeGate : IFeatureGate
    {
        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) =>
            !Answers.TryGetValue(featureId, out bool value) || value;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
