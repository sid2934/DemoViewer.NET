#region

using System.ComponentModel;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Playback2D;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Carries the shell to extension contexts once the composition root has built it, so nothing an
///     extension registers has to resolve the shell while it is being constructed.
/// </summary>
internal sealed class ExtensionShellHub : IFirstPartyShellState, IFirstPartyExportChips
{
    private MainViewModel? _shell;
    private bool _playheadPending;

    public MainViewModel? Shell => _shell;

    public ParsedDemo? CurrentDemo => (_shell?.ModuleContext as ModuleContext)?.CurrentDemo;

    public string? CurrentDemoHash => (_shell?.ModuleContext as ModuleContext)?.DemoSha256;

    public int CurrentTick => _shell?.Playback.CurrentTick ?? 0;

    public bool IsPlaying => _shell?.Playback.IsPlaying ?? false;

    public bool IsLiveSyncSessionActive => _shell?.LiveSync?.State.IsSessionActive == true;

    public bool IsReelJobRunning => _shell?.ReelJob?.Status.IsRunning == true;

    public event Action? DemoChanged;

    // An export starts from a tab, so the shell is attached by the time a job mounts its status.
    public void Mount(string chipId, string featureId, Playback2DExportStatusViewModel status) =>
        _shell?.MountExportStatus(chipId, featureId, status);

    /// <summary>Raised on the UI thread, once per rendered frame at most, after the tick or play state moves.</summary>
    public event Action? PlayheadChanged;

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

        shell.Playback.PropertyChanged += OnPlaybackChanged;
    }

    // The play loop sets CurrentTick once per stepped frame, several per timer tick at high speed, so the
    // raise is posted at render priority and coalesced, the same shape as the controller's Advanced push.
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlaybackController.CurrentTick) or nameof(PlaybackController.IsPlaying))
            || PlayheadChanged is null || _playheadPending)
        {
            return;
        }

        _playheadPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _playheadPending = false;
            PlayheadChanged?.Invoke();
        }, DispatcherPriority.Render);
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
        private readonly Action _forwardPlayhead;
        private Action? _playheadHandlers;

        // Each of the extension's handlers runs even when one before it throws. A playhead handler runs every
        // rendered frame, so its faults count as recurring: once per exception type per minute.
        public ShellView(ExtensionShellHub hub, ExtensionGuard guard)
        {
            _hub = hub;
            hub.DemoChanged += () => RunEach(CurrentDemoChanged, guard, "demo change handler", FaultKind.Counted);
            _forwardPlayhead = () => RunEach(_playheadHandlers, guard, "playhead handler", FaultKind.Recurring);
        }

        private static void RunEach(Action? handlers, ExtensionGuard guard, string site, FaultKind kind)
        {
            if (handlers is null)
            {
                return;
            }

            foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
            {
                guard.Run(site, handler, kind);
            }
        }

        public ParsedDemo? CurrentDemo => _hub.CurrentDemo;

        public string? CurrentDemoHash => _hub.CurrentDemoHash;

        public int CurrentTick => _hub.CurrentTick;

        public bool IsPlaying => _hub.IsPlaying;

        // Joined to the hub only while the extension listens, so the hub posts nothing per frame for nobody.
        public event Action? PlayheadChanged
        {
            add
            {
                bool first = _playheadHandlers is null;
                _playheadHandlers += value;
                if (first && _playheadHandlers is not null)
                {
                    _hub.PlayheadChanged += _forwardPlayhead;
                }
            }
            remove
            {
                _playheadHandlers -= value;
                if (_playheadHandlers is null)
                {
                    _hub.PlayheadChanged -= _forwardPlayhead;
                }
            }
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

    internal sealed class StorageView(string extensionId, string? configRoot = null, string? cacheRoot = null) : IExtensionStorage
    {
        private readonly string? _configRoot = configRoot ?? AppPaths.ConfigRoot;
        private readonly string? _cacheRoot = cacheRoot ?? AppPaths.DemoCacheDir;

        public string? ConfigDirectory => Ensure(_configRoot is { } root
            ? Path.Combine(root, ExtensionDataDirectoryName, extensionId)
            : null);

        public string? CacheDirectory => Ensure(_cacheRoot is { } cache
            ? Path.Combine(cache, ExtensionDataDirectoryName, extensionId)
            : null);

        public async Task<bool> WriteAtomicAsync(StoreRoot root, string relativePath, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            if (Resolve(root, relativePath) is not { } target)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Beside the target, so the move is a rename on the same volume.
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, target, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        public async Task<byte[]?> ReadAsync(StoreRoot root, string relativePath, CancellationToken cancellationToken = default)
        {
            if (Resolve(root, relativePath) is not { } target || !File.Exists(target))
            {
                return null;
            }

            return await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        }

        // Null when the build has no folders; throws for a path that would leave the extension's folder.
        private string? Resolve(StoreRoot root, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(relativePath);
            if ((root == StoreRoot.Config ? ConfigDirectory : CacheDirectory) is not { } folder)
            {
                return null;
            }

            return PackDataRemover.ResolveSafe(folder, relativePath)
                   ?? throw new ArgumentException($"'{relativePath}' is not a file inside the extension's folder.", nameof(relativePath));
        }

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
