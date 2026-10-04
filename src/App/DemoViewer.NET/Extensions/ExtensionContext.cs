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

/// <summary>The host as one extension sees it.</summary>
internal sealed class ExtensionContext : IExtensionContext
{
    private readonly string _logPrefix;

    public ExtensionContext(IExtension extension, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(services);
        ExtensionId = extension.Id;
        FeatureId = extension.FeatureId;
        _logPrefix = "Extension." + extension.Id;
        Features = new GateView(services.GetService<IFeatureGate>());
        Jobs = new ExtensionJobs(extension.Id, () => services.GetService<IDemoProcessingQueue>(),
            () => services.GetService<JobKindRegistry>() ?? JobKindRegistry.Default);
        Shell = new ShellView(services.GetRequiredService<ExtensionShellHub>());
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
        Dispatcher.UIThread.Post(action);
    }

    private sealed class GateView(IFeatureGate? gate) : IExtensionFeatures
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
            EventHandler handler = (_, _) => value();
            _forwards[value] = handler;
            return handler;
        }
    }

    private sealed class ShellView : IExtensionShell
    {
        private readonly ExtensionShellHub _hub;

        public ShellView(ExtensionShellHub hub)
        {
            _hub = hub;
            hub.DemoChanged += () => CurrentDemoChanged?.Invoke();
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

/// <summary>The processing queue as one extension sees it: every job carries the extension's id as its owner.</summary>
internal sealed class ExtensionJobs(string extensionId, Func<IDemoProcessingQueue?> queue, Func<JobKindRegistry> kinds)
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
        QueueJobRequest request = new(kind, title, extensionId, priority, ctx => work(new Context(ctx)), options.Key,
            ReplacePending: options.Key is not null, Preemptible: options.Preemptible, Serial: options.Serial,
            ExtensionKind: extensionKind);
        return QueueWork.Submit(queue(), request);
    }

    public void CancelAll() => queue()?.CancelOwned(extensionId);

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
