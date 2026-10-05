#region

using Avalonia.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
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
            () => services.GetService<JobKindRegistry>() ?? JobKindRegistry.Default, _guard, Post);
        Passes = new ExtensionPasses(extension.Id, () => services.GetService<DemoScheduler>(), Post);
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

    public IExtensionPasses Passes { get; }

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

            await AtomicFile.WriteAllBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
            return true;
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
///     The processing queue as one extension sees it: every job carries the extension's id as its owner, a
///     job that throws is counted against the extension before the queue marks it failed, a handle cancels
///     its own job only, and a job's completion is told on the UI thread through the extension's post.
/// </summary>
internal sealed class ExtensionJobs(string extensionId, Func<IDemoProcessingQueue?> queue, Func<JobKindRegistry> kinds,
    ExtensionGuard? guard = null, Action<Action>? post = null)
    : IExtensionJobs
{
    private readonly Action<Action> _post = post ?? (static a => a());

    public IJobHandle Enqueue(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        (QueueJobKind kind, string? extensionKind) = Resolve(request.Options.Kind);
        JobPriority priority = JobScope.IsUserAction ? JobPriority.UserRequested : request.Options.Priority;
        JobHandle handle = new(_post);
        if (request.DemoPath is { } path)
        {
            EnqueueOnDemo(request, path, priority, kinds().Label(kind, extensionKind), handle);
            return handle;
        }

        Func<IJobContext, Task> work = request.Work!;
        IDemoProcessingQueue? target = queue();
        if (QueueWork.RunsOnPool(target))
        {
            // No queue (the browser): the job still runs, on the pool, and still has an end to tell.
            handle.Started();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Counted(PoolContext.Instance, work).ConfigureAwait(false);
                    handle.End(JobStatus.Completed, null);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    handle.End(JobStatus.Failed, ex);
                }
            });
            return handle;
        }

        QueueJobRequest queued = new(kind, request.Title, extensionId, PriorityOf(priority), QueueWork.WithStopToken(async ctx =>
            {
                handle.Started();
                try
                {
                    await Counted(ctx, work).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException
                                           && !(ex is OperationCanceledException && ctx.CancellationToken.IsCancellationRequested))
                {
                    handle.Error = ex;
                    throw;
                }
            }), request.Options.Key, ReplacePending: request.Options.Key is not null, Preemptible: request.Options.Preemptible,
            Serial: request.Options.Serial, ExtensionKind: extensionKind, Level: LevelOf(priority));
        IDemoQueueHandle submitted = target!.SubmitJob(queued);
        handle.Attach(submitted.Cancel);
        if (submitted.State == DemoQueueItemState.Rejected)
        {
            handle.End(JobStatus.Rejected, null);
            return handle;
        }

        _ = submitted.Completion.ContinueWith(_ => handle.End(StatusOf(submitted.State), handle.Error),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return handle;
    }

    // A job on a demo rides the demo's visit. Its fault is counted here; the queue then records the pass failed.
    private void EnqueueOnDemo(JobRequest request, string path, JobPriority priority, string label, JobHandle handle)
    {
        if (queue() is not { } target || QueueWork.RunsOnPool(target))
        {
            handle.End(JobStatus.Rejected, null);
            return;
        }

        Func<IDemoJobContext, Task> work = request.DemoWork!;
        DemoJob job = DemoJob.Submit(target, new DemoJobRequest(path, label, extensionId, LevelOf(priority), async input =>
        {
            handle.Started();
            try
            {
                await QueueWork.WithStopToken(() => work(new DemoContext(input)), input.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (guard is not null && ex is not OutOfMemoryException
                                       && !(ex is OperationCanceledException && input.CancellationToken.IsCancellationRequested))
            {
                guard.Report("job", ex);
                throw;
            }
        }, request.ReadsUserCommands, request.Title));
        handle.Attach(job.Cancel);
        _ = job.Completion.ContinueWith(_ => handle.End(StatusOf(job.State), job.Error),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(work);
        options ??= new JobOptions();
        (QueueJobKind kind, string? extensionKind) = Resolve(options.Kind);

        QueueJobRequest request = new(kind, title, extensionId, PriorityOf(options.Priority), ctx => Counted(ctx, work), options.Key,
            ReplacePending: options.Key is not null, Preemptible: options.Preemptible, Serial: options.Serial,
            ExtensionKind: extensionKind, Level: LevelOf(options.Priority));
        return QueueWork.Submit(queue(), request);
    }

    public void CancelAll() => queue()?.CancelOwned(extensionId);

    private static DemoJobPriority PriorityOf(JobPriority priority) =>
        priority == JobPriority.UserRequested ? DemoJobPriority.UserRequested : DemoJobPriority.Background;

    private static PassLevel LevelOf(JobPriority priority) => priority switch
    {
        JobPriority.UserRequested => PassLevel.UserRequested,
        JobPriority.Backlog => PassLevel.Backlog,
        _ => PassLevel.Background
    };

    private static JobStatus StatusOf(DemoQueueItemState state) => state switch
    {
        DemoQueueItemState.Queued => JobStatus.Queued,
        DemoQueueItemState.Running => JobStatus.Running,
        DemoQueueItemState.Completed => JobStatus.Completed,
        DemoQueueItemState.Failed => JobStatus.Failed,
        DemoQueueItemState.Rejected => JobStatus.Rejected,
        _ => JobStatus.Cancelled
    };

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

        public Task StepAsideAsync() => inner.StepAsideAsync();
    }

    private sealed class PoolContext : IQueueJobContext
    {
        public static PoolContext Instance { get; } = new();

        public CancellationToken CancellationToken => CancellationToken.None;

        public void Report(int done, int total, string? detail = null)
        {
        }

        public Task StepAsideAsync() => Task.CompletedTask;

        public void ReleaseSlot()
        {
        }
    }

    // The demo's parse is the visit's; the input stops handing it out once the job's turn ends.
    private sealed class DemoContext(DemoJobInput input) : IDemoJobContext
    {
        public CancellationToken CancellationToken => input.CancellationToken;

        public string DemoPath => input.DemoPath;

        public ParsedDemo Parsed => input.Parsed;

        public void Report(int done, int total, string? detail = null)
        {
        }
    }

    // One job's state as the extension sees it. Completed is raised through the extension's post, once.
    private sealed class JobHandle(Action<Action> post) : IJobHandle
    {
        private readonly TaskCompletionSource<JobResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private Action? _cancel;
        private Action<JobResult>? _completed;
        private bool _cancelRequested;
        private int _status = (int)JobStatus.Queued;

        public Exception? Error { get; set; }

        public JobStatus Status => (JobStatus)Volatile.Read(ref _status);

        public Task<JobResult> Completion => _done.Task;

        public event Action<JobResult>? Completed
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                lock (_gate)
                {
                    if (!_done.Task.IsCompleted)
                    {
                        _completed += value;
                        return;
                    }
                }

                JobResult result = _done.Task.Result;
                post(() => value(result));
            }
            remove
            {
                lock (_gate)
                {
                    _completed -= value;
                }
            }
        }

        public void Cancel()
        {
            Action? cancel;
            lock (_gate)
            {
                _cancelRequested = true;
                cancel = _cancel;
            }

            cancel?.Invoke();
        }

        public void Attach(Action cancel)
        {
            bool now;
            lock (_gate)
            {
                _cancel = cancel;
                now = _cancelRequested;
            }

            if (now)
            {
                cancel();
            }
        }

        public void Started() =>
            Interlocked.CompareExchange(ref _status, (int)JobStatus.Running, (int)JobStatus.Queued);

        public void End(JobStatus status, Exception? error)
        {
            JobResult result = new(status, status == JobStatus.Failed ? error : null);
            Action<JobResult>? handlers;
            lock (_gate)
            {
                if (_done.Task.IsCompleted)
                {
                    return;
                }

                Volatile.Write(ref _status, (int)status);
                _done.SetResult(result);
                handlers = _completed;
                _completed = null;
            }

            if (handlers is not null)
            {
                foreach (Action<JobResult> handler in handlers.GetInvocationList().Cast<Action<JobResult>>())
                {
                    post(() => handler(result));
                }
            }
        }
    }
}

/// <summary>The scheduler as one extension sees it: re-checks for everyone, busy reads for its own passes only.</summary>
internal sealed class ExtensionPasses(string extensionId, Func<DemoScheduler?> scheduler, Action<Action>? post = null)
    : IExtensionPasses
{
    private readonly object _gate = new();
    private readonly Action<Action> _post = post ?? (static a => a());
    private Action? _changed;
    private DemoScheduler? _subscribed;

    public event Action? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (_subscribed is null && scheduler() is { } source)
                {
                    _subscribed = source;
                    source.OutstandingChanged += OnOutstandingChanged;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    // Only the extension's own passes reach its handlers, on the UI thread.
    private void OnOutstandingChanged(string passId)
    {
        Action? handlers;
        lock (_gate)
        {
            handlers = _changed;
        }

        if (handlers is not null && _subscribed is { } source
                                  && string.Equals(source.OwnerOf(passId), extensionId, StringComparison.Ordinal))
        {
            _post(handlers);
        }
    }

    public void Request(string demoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        scheduler()?.Request(demoPath);
    }

    public void RecheckAll() => scheduler()?.RecheckAll();

    public bool IsBusy(string passId)
    {
        ArgumentException.ThrowIfNullOrEmpty(passId);
        return scheduler()?.HasOutstanding(passId, extensionId) ?? false;
    }
}
