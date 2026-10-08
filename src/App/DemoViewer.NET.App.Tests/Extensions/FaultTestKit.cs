#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Stands in for the UI thread's post: actions wait until the test drains them.</summary>
internal sealed class PostQueue
{
    private readonly Queue<Action> _pending = new();

    public int Count => _pending.Count;

    public void Post(Action action) => _pending.Enqueue(action);

    public void Drain()
    {
        while (_pending.TryDequeue(out Action? action))
        {
            action();
        }
    }
}

/// <summary>Records what the fault tracker asked of the gate.</summary>
internal sealed class RecordingSwitch : IFeatureSuspension
{
    private readonly HashSet<string> _suspended = new(StringComparer.Ordinal);

    public List<string> Suspensions { get; } = [];
    public List<string> Resumptions { get; } = [];

    /// <summary>Runs inside <see cref="SuspendForSession" />, the way the gate's Changed handlers do.</summary>
    public Action? OnSuspend { get; set; }

    public void SuspendForSession(string packFeatureId)
    {
        Suspensions.Add(packFeatureId);
        _suspended.Add(packFeatureId);
        OnSuspend?.Invoke();
    }

    public void ResumeForSession(string packFeatureId)
    {
        Resumptions.Add(packFeatureId);
        _suspended.Remove(packFeatureId);
    }

    public bool IsSuspended(string packFeatureId) => _suspended.Contains(packFeatureId);
}

/// <summary>Captures every diagnostics log entry while installed.</summary>
internal sealed class CapturedLogs : ILoggerProvider, ILogger, IDisposable
{
    private readonly List<(int EventId, string Message, Exception? Exception)> _entries = [];
    private readonly ILoggerFactory _previous;

    private CapturedLogs()
    {
        _previous = DiagnosticsLog.LoggerFactory;
        DiagnosticsLog.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(this).SetMinimumLevel(LogLevel.Trace));
    }

    public IReadOnlyList<(int EventId, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public static CapturedLogs Install() => new();

    public IEnumerable<string> WithEvent(int eventId) => Entries.Where(e => e.EventId == eventId).Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose() => DiagnosticsLog.LoggerFactory = _previous;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((eventId.Id, formatter(state, exception), exception));
        }
    }
}

/// <summary>A fault tracker over one fake extension, its posts held for the test to drain.</summary>
internal sealed class FaultRig
{
    public FaultRig(string id = "pack.fake")
    {
        Scope = new ExtensionScope(id, id, "Fake", typeof(FaultRig).Assembly);
        Faults = new ExtensionFaults([Scope], Posts.Post, Clock);
        Faults.AttachSwitch(Switch);
    }

    public ManualClock Clock { get; } = new();
    public PostQueue Posts { get; } = new();
    public RecordingSwitch Switch { get; } = new();
    public ExtensionScope Scope { get; }
    public ExtensionFaults Faults { get; }
    public ExtensionGuard Guard => Faults.GuardFor(Scope);

    public void Throw(string site = "site", FaultKind kind = FaultKind.Counted)
    {
        Action body = () => throw new InvalidOperationException("boom at " + site);
        Faults.Run(Scope, site, body, kind);
    }
}

/// <summary>An extension that contributes nothing, for a collector under test.</summary>
internal sealed class StubExtension(string featureId = "pack.fake") : IExtension
{
    public string Id => "net.demoviewer." + featureId;
    public string FeatureId => featureId;
    public IEnumerable<ExtensionFeature> Features => [];

    public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
    }

    public void Contribute(IExtensionContributions contributions, IServiceProvider services)
    {
    }
}
