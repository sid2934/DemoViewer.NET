#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="PackSwitch" /> over fakes: transitions only on a real change of the resolved gate value,
///     the startup reason once and the in-session reason after, the enable's token cancelled by a switch-off,
///     the first-run wait, and a disposed switch that stops listening.
/// </summary>
public class PackSwitchTests
{
    [Test]
    public async Task Start_EnablesAPackThatResolvesOn_WithTheStartupReason_AndIgnoresAChangeThatLeavesItOn()
    {
        FakeGate gate = new() { On = true };
        FakeLifecycle lifecycle = new();
        FakePack pack = new();
        int reconsidered = 0;
        using PackSwitch packs = new([pack], gate, _ => lifecycle, null, () => reconsidered++);

        packs.Start();
        gate.Raise(); // a settings write that changed something else
        gate.Raise();
        await packs.Pending;

        using (Assert.Multiple())
        {
            await Assert.That(lifecycle.Enabled).IsEquivalentTo([ExtensionStartReason.Startup]);
            await Assert.That(lifecycle.Disabled).IsEqualTo(0);
            await Assert.That(packs.IsOn(pack)).IsTrue();
            await Assert.That(reconsidered).IsEqualTo(0).Because("startup re-polls through the library rescan, not here");
        }
    }

    [Test]
    public async Task OffToOn_EnablesInSession_AndReconsiders_OnToOff_CancelsTheEnable_AndDisables()
    {
        FakeGate gate = new() { On = false };
        FakeLifecycle lifecycle = new();
        FakePack pack = new();
        int reconsidered = 0;
        using PackSwitch packs = new([pack], gate, _ => lifecycle, null, () => reconsidered++);
        packs.Start();
        await Assert.That(lifecycle.Enabled).IsEmpty();

        gate.On = true;
        gate.Raise();
        await packs.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(lifecycle.Enabled).IsEquivalentTo([ExtensionStartReason.EnabledInSession]);
            await Assert.That(reconsidered).IsEqualTo(1).Because("the library is re-polled once the loads are queued");
            await Assert.That(lifecycle.LastToken.IsCancellationRequested).IsFalse();
            await Assert.That(packs.IsOn(pack)).IsTrue();
        }

        gate.On = false;
        gate.Raise();
        gate.Raise();
        await packs.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(lifecycle.Disabled).IsEqualTo(1).Because("one transition, one release, however many Changed arrive");
            await Assert.That(lifecycle.LastToken.IsCancellationRequested).IsTrue().Because("the enable in flight is cancelled through its token");
            await Assert.That(packs.IsOn(pack)).IsFalse();
        }

        gate.On = true;
        gate.Raise();
        await packs.Pending;
        await Assert.That(lifecycle.Enabled.Count).IsEqualTo(2).Because("on again reloads");
    }

    [Test]
    public async Task WhileTheWizardHasToAsk_NothingStarts_TheAnswerDoes()
    {
        FakeGate gate = new() { On = true };
        FakeLifecycle lifecycle = new();
        FakePack pack = new();
        bool waiting = true;
        using PackSwitch packs = new([pack], gate, _ => lifecycle, null, () => { }, () => waiting);

        packs.Start();
        gate.Raise();
        await Assert.That(lifecycle.Enabled).IsEmpty().Because("the pack resolves on, but the wizard decides");

        waiting = false;
        gate.Raise(); // the wizard's Finish writes settings
        await packs.Pending;
        await Assert.That(lifecycle.Enabled).IsEquivalentTo([ExtensionStartReason.EnabledInSession]);
    }

    [Test]
    public async Task APackWithoutALifecycle_IsTracked_ButNothingIsCalled()
    {
        FakeGate gate = new() { On = true };
        FakePack pack = new();
        using PackSwitch packs = new([pack], gate, _ => null, null, () => { });
        packs.Start();
        gate.On = false;
        gate.Raise();
        await packs.Pending;
        await Assert.That(packs.IsOn(pack)).IsFalse();
    }

    [Test]
    public async Task Disposed_StopsListening()
    {
        FakeGate gate = new() { On = false };
        FakeLifecycle lifecycle = new();
        FakePack pack = new();
        PackSwitch packs = new([pack], gate, _ => lifecycle, null, () => { });
        packs.Start();
        packs.Dispose();

        gate.On = true;
        gate.Raise();
        await Assert.That(lifecycle.Enabled).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task AFaultedEnableOrDisable_IsLogged_AndPendingStillCompletes()
    {
        CapturingLoggerProvider capture = new();
        ILoggerFactory previous = DiagnosticsLog.LoggerFactory;
        try
        {
            DiagnosticsLog.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(capture));
            FakeGate gate = new() { On = true };
            FakeLifecycle lifecycle = new()
            {
                EnableResult = Task.FromException(new InvalidOperationException("enable broke")),
                DisableResult = Task.FromException(new InvalidOperationException("disable broke"))
            };
            FakePack pack = new();
            using PackSwitch packs = new([pack], gate, _ => lifecycle, null, () => { });

            packs.Start();
            await packs.Pending;
            gate.On = false;
            gate.Raise();
            await packs.Pending;

            await Assert.That(capture.Errors).Contains(e => e.Contains("enable broke", StringComparison.Ordinal));
            await Assert.That(capture.Errors).Contains(e => e.Contains("disable broke", StringComparison.Ordinal));
        }
        finally
        {
            DiagnosticsLog.LoggerFactory = previous;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_errors)
                {
                    return [.. _errors];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null)
            {
                return;
            }

            lock (_errors)
            {
                _errors.Add(formatter(state, exception) + " " + exception.Message);
            }
        }
    }

    private sealed class FakeGate : IFeatureGate
    {
        public bool On { get; set; }
        public UserCategory Category => UserCategory.PowerUser;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => On;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeLifecycle : IExtensionLifecycle
    {
        public List<ExtensionStartReason> Enabled { get; } = [];
        public int Disabled { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task EnableResult { get; init; } = Task.CompletedTask;
        public Task DisableResult { get; init; } = Task.CompletedTask;

        public Task OnEnabledAsync(ExtensionStartReason reason, CancellationToken ct)
        {
            Enabled.Add(reason);
            LastToken = ct;
            return EnableResult;
        }

        public Task OnDisabledAsync()
        {
            Disabled++;
            return DisableResult;
        }

        public void OnShutdown(TimeSpan budget)
        {
        }
    }

    private sealed class FakePack : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.pack.fake";
        public string FeatureId => "pack.fake";
        public ExtensionManifest Manifest => FakeManifests.For(Id);
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
