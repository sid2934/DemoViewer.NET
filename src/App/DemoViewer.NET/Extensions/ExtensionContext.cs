#region

using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Carries the shell to extension contexts once the composition root has built it, so nothing an
///     extension registers has to resolve the shell while it is being constructed.
/// </summary>
internal sealed class ExtensionShellHub
{
    private MainViewModel? _shell;

    public MainViewModel? Shell => _shell;

    public event Action? DemoChanged;

    public void Attach(MainViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (_shell is not null)
        {
            return;
        }

        _shell = shell;
        if (shell.ModuleContext is { } context)
        {
            context.DemoReset += () => DemoChanged?.Invoke();
        }
    }
}

/// <summary>
///     The host as one extension sees it. Every callback the extension hands in (a posted action, a feature
///     or demo change handler) runs as the extension's, so a throw is reported against it rather than
///     reaching the dispatcher or skipping the host's own subscribers.
/// </summary>
internal sealed class ExtensionContext : IExtensionContext
{
    private readonly ExtensionGuard _guard;
    private readonly string _logPrefix;

    public ExtensionContext(IExtension extension, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(services);
        ExtensionId = extension.Id;
        FeatureId = extension.FeatureId;
        _logPrefix = "Extension." + extension.Id;
        _guard = services.GetService<ExtensionFaults>()?.GuardFor(extension) ?? ExtensionGuard.Standalone(extension);
        Features = new GateView(services.GetService<IFeatureGate>(), _guard);
        Jobs = new ExtensionJobs(extension.Id, () => services.GetService<IDemoProcessingQueue>(),
            () => services.GetService<JobKindRegistry>() ?? JobKindRegistry.Default, _guard);
        Shell = new ShellView(services.GetRequiredService<ExtensionShellHub>(), _guard);
        Storage = new StorageView(extension.Id);
    }

    public string ExtensionId { get; }

    public string FeatureId { get; }

    public HostInfo Host { get; } = new(
        typeof(IExtension).Assembly.GetName().Version ?? new Version(1, 0),
        ExtensionHost.AppVersion?.ToString(),
        OperatingSystem.IsBrowser());

    public IExtensionFeatures Features { get; }

    public IExtensionJobs Jobs { get; }

    public IExtensionShell Shell { get; }

    public IExtensionStorage Storage { get; }

    public ILogger CreateLogger(string category) => DiagnosticsLog.CreateLogger(_logPrefix + "." + category);

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(_guard.Wrap("posted callback", action));
    }

    private sealed class GateView(IFeatureGate? gate, ExtensionGuard guard) : IExtensionFeatures
    {
        public bool IsEnabled(string featureId) => gate?.IsEnabled(featureId) ?? true;

        public event Action? Changed
        {
            add
            {
                if (gate is not null && value is not null)
                {
                    gate.Changed += Forward(value);
                }
            }
            remove
            {
                if (gate is not null && value is not null && _forwards.Remove(value, out EventHandler? handler))
                {
                    gate.Changed -= handler;
                }
            }
        }

        private readonly Dictionary<Action, EventHandler> _forwards = [];

        private EventHandler Forward(Action value)
        {
            EventHandler handler = (_, _) => guard.Run("feature change handler", value);
            _forwards[value] = handler;
            return handler;
        }
    }

    private sealed class ShellView : IExtensionShell
    {
        private readonly ExtensionShellHub _hub;

        // Each of the extension's handlers runs even when one before it throws.
        public ShellView(ExtensionShellHub hub, ExtensionGuard guard)
        {
            _hub = hub;
            hub.DemoChanged += () =>
            {
                if (CurrentDemoChanged is not { } handlers)
                {
                    return;
                }

                foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
                {
                    guard.Run("demo change handler", handler);
                }
            };
        }

        public string? CurrentDemoPath => _hub.Shell?.LoadedDemoPath;

        public event Action? CurrentDemoChanged;

        public async Task<bool> OpenDemoAsync(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (_hub.Shell is not { } shell)
            {
                return false;
            }

            await shell.LoadDemoFromPathAsync(path).ConfigureAwait(true);
            return shell.HasFile;
        }

        public void SeekToTick(int tick) => _hub.Shell?.Playback.SeekToTick(tick);

        public bool SelectTab(string tabId) => _hub.Shell?.TrySelectTab(tabId) ?? false;

        public void RevealInFileManager(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _hub.Shell?.OpenOutputFolder(path);
        }
    }

    private sealed class StorageView(string extensionId) : IExtensionStorage
    {
        public string? ConfigDirectory => Ensure(AppPaths.ConfigRoot is { } root
            ? Path.Combine(root, ExtensionDataDirectoryName, extensionId)
            : null);

        public string? CacheDirectory => Ensure(AppPaths.DemoCacheDir is { } cache
            ? Path.Combine(cache, ExtensionDataDirectoryName, extensionId)
            : null);

        private static string? Ensure(string? path)
        {
            if (path is not null)
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }
    }

    /// <summary>The folder under the config and cache roots that holds one folder per extension.</summary>
    public const string ExtensionDataDirectoryName = "extension-data";
}

/// <summary>
///     The processing queue as one extension sees it: every job carries the extension's id as its owner, and
///     a job that throws is counted against the extension before the queue marks it failed.
/// </summary>
internal sealed class ExtensionJobs(string extensionId, Func<IDemoProcessingQueue?> queue, Func<JobKindRegistry> kinds,
    ExtensionGuard? guard = null)
    : IExtensionJobs
{
    public Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(work);
        options ??= new JobOptions();
        (QueueJobKind kind, string? extensionKind) = Resolve(options.Kind);

        DemoJobPriority priority = options.Priority == JobPriority.UserRequested
            ? DemoJobPriority.UserRequested
            : DemoJobPriority.Background;
        QueueJobRequest request = new(kind, title, extensionId, priority, ctx => Counted(ctx, work), options.Key,
            ReplacePending: options.Key is not null, Preemptible: options.Preemptible, Serial: options.Serial,
            ExtensionKind: extensionKind);
        return QueueWork.Submit(queue(), request);
    }

    public void CancelAll() => queue()?.CancelOwned(extensionId);

    private async Task Counted(IQueueJobContext ctx, Func<IJobContext, Task> work)
    {
        try
        {
            await work(new Context(ctx)).ConfigureAwait(false);
        }
        catch (Exception ex) when (guard is not null && ex is not OutOfMemoryException
                                   && !(ex is OperationCanceledException && ctx.CancellationToken.IsCancellationRequested))
        {
            guard.Report("job", ex);
            throw;
        }
    }

    private (QueueJobKind Kind, string? ExtensionKind) Resolve(string kindId)
    {
        if (JobKindRegistry.BuiltInExtensionKinds.TryGetValue(kindId, out QueueJobKind core))
        {
            return (core, null);
        }

        if (kinds().IsDeclared(kindId))
        {
            return (QueueJobKind.Extension, kindId);
        }

        throw new ArgumentException(
            $"Job kind '{kindId}' is neither built in nor declared in {extensionId}'s IExtension.JobKinds.", nameof(kindId));
    }

    private sealed class Context(IQueueJobContext inner) : IJobContext
    {
        public CancellationToken CancellationToken => inner.CancellationToken;

        public void Report(int done, int total, string? detail = null) => inner.Report(done, total, detail);
    }
}
