using CS2DemoKit.Parser;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>How urgent a job is.</summary>
public enum JobPriority
{
    /// <summary>Opt-in sweeps and anything the user did not ask for. Pausable, runs when the app is idle.</summary>
    Background,

    /// <summary>
    ///     Catching up on demos the library just added or found changed. Pausable like background work, and
    ///     runs ahead of it.
    /// </summary>
    Backlog,

    /// <summary>Work a click asked for: it goes to the front and stops background work.</summary>
    UserRequested
}

/// <summary>Where a job is, or how it ended.</summary>
public enum JobStatus
{
    /// <summary>Waiting for its turn.</summary>
    Queued,

    /// <summary>Running now.</summary>
    Running,

    /// <summary>Ran to the end.</summary>
    Completed,

    /// <summary>Threw, or the demo it named could not be read.</summary>
    Failed,

    /// <summary>Cancelled, removed from the queue list, or stopped because the extension was switched off.</summary>
    Cancelled,

    /// <summary>Not queued: the queue was full of background work, or the host has no queue.</summary>
    Rejected
}

/// <summary>How a job ended.</summary>
/// <param name="Status">One of the ended states: completed, failed, cancelled or rejected.</param>
/// <param name="Error">What the job threw, when it failed.</param>
public sealed record JobResult(JobStatus Status, Exception? Error = null);

/// <summary>A queued job: its state, its end, and a way to cancel it.</summary>
public interface IJobHandle
{
    /// <summary>Where the job is now. Safe to read from any thread.</summary>
    JobStatus Status { get; }

    /// <summary>Completes when the job ends, however it ends. Never faults.</summary>
    Task<JobResult> Completion { get; }

    /// <summary>
    ///     Raised once when the job ends, on the UI thread. A handler added after the end is raised at once,
    ///     still on the UI thread.
    /// </summary>
    event Action<JobResult>? Completed;

    /// <summary>Cancels this job only. A queued job never runs; a running one sees its token fire.</summary>
    void Cancel();
}

/// <summary>What a job that names a demo gets: the demo's parse for as long as the job runs.</summary>
public interface IDemoJobContext : IJobContext
{
    /// <summary>The demo the job named.</summary>
    string DemoPath { get; }

    /// <summary>
    ///     The demo's parse, shared with the demo's visit. Read only; do not keep it past the job, which
    ///     throws <see cref="ObjectDisposedException" /> once the job has ended.
    /// </summary>
    ParsedDemo Parsed { get; }
}

/// <summary>
///     A job for <see cref="IExtensionJobs.Enqueue" />. A job that names a demo joins that demo's visit: the
///     demo is read once for it and every pass that wants the demo, on the parse the shell holds when the demo
///     is open, and the job runs after those passes.
/// </summary>
public sealed class JobRequest
{
    /// <summary>A job that needs no demo.</summary>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="work">The job.</param>
    /// <param name="options">How it is queued; null for a background compute job.</param>
    public JobRequest(string title, Func<IJobContext, Task> work, JobOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(work);
        Title = title;
        Work = work;
        Options = options ?? new JobOptions();
    }

    private JobRequest(string title, string demoPath, Func<IDemoJobContext, Task> work, JobOptions? options)
    {
        Title = title;
        DemoPath = demoPath;
        DemoWork = work;
        Options = options ?? new JobOptions();
    }

    /// <summary>A job on one demo's parse.</summary>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="demoPath">The demo.</param>
    /// <param name="work">
    ///     The job. It holds the demo's parse until its task ends, and no other demo is read meanwhile, so it
    ///     must not wait on another job.
    /// </param>
    /// <param name="options">How it is queued; only <see cref="JobOptions.Priority" /> and <see cref="JobOptions.Kind" /> apply.</param>
    public static JobRequest OnDemo(string title, string demoPath, Func<IDemoJobContext, Task> work, JobOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(demoPath);
        ArgumentNullException.ThrowIfNull(work);
        return new JobRequest(title, demoPath, work, options);
    }

    /// <summary>The line the queue list shows.</summary>
    public string Title { get; }

    /// <summary>How it is queued.</summary>
    public JobOptions Options { get; }

    /// <summary>The job, when it names no demo.</summary>
    public Func<IJobContext, Task>? Work { get; }

    /// <summary>The demo the job runs on, or null.</summary>
    public string? DemoPath { get; }

    /// <summary>The job, when it names a demo.</summary>
    public Func<IDemoJobContext, Task>? DemoWork { get; }

    /// <summary>True when a job on a demo reads player inputs, which the parse otherwise may leave out.</summary>
    public bool ReadsUserCommands { get; init; }
}

/// <summary>
///     A label for a kind of job in the queue list. Declare it in <see cref="IExtension.JobKinds" /> and name it
///     in <see cref="JobOptions.Kind" />.
/// </summary>
/// <param name="Id">Unique across the app; prefix it with your extension id.</param>
/// <param name="Label">The short chip the queue list shows.</param>
/// <param name="IsLight">True for small jobs that may run beside a demo parse.</param>
/// <param name="Rank">Order among queued jobs of the same priority; lower runs first. The host's own range is 0 to 4.</param>
public sealed record ExtensionJobKind(string Id, string Label, bool IsLight, int Rank = 4);

/// <summary>The built-in job kinds.</summary>
public static class BuiltInJobKinds
{
    /// <summary>A light computation, such as a section's query.</summary>
    public const string Compute = "section";

    /// <summary>Writing a store to disk.</summary>
    public const string Save = "save";

    /// <summary>Reading a store from disk.</summary>
    public const string Load = "load";
}

/// <summary>How a job is queued.</summary>
/// <param name="Kind">A <see cref="BuiltInJobKinds" /> value or an id from <see cref="IExtension.JobKinds" />.</param>
/// <param name="Priority">How urgent it is.</param>
/// <param name="Key">One queued job per key: a newer submit replaces the queued one.</param>
/// <param name="Preemptible">True when the job checks its token at natural boundaries, so a user's job may stop it and requeue it.</param>
/// <param name="Serial">Jobs sharing a serial never run at the same time.</param>
public sealed record JobOptions(
    string Kind = BuiltInJobKinds.Compute,
    JobPriority Priority = JobPriority.Background,
    string? Key = null,
    bool Preemptible = false,
    string? Serial = null);

/// <summary>What a running job gets.</summary>
public interface IJobContext
{
    /// <summary>Fires when the user removes the job or the extension is switched off.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Progress for the queue list.</summary>
    void Report(int done, int total, string? detail = null);

    /// <summary>
    ///     Gives the parse slot up and takes it back once an open or a job the user asked for has had it. Call
    ///     between batches of a long job that does not read a demo. A no-op where there is no slot to give.
    /// </summary>
    Task StepAsideAsync() => Task.CompletedTask;
}

/// <summary>
///     The app's processing queue. Every job the extension runs off the UI thread goes through it, so the user
///     sees it and can pause or remove it. Jobs carry the extension's id, and switching the extension off
///     cancels the queued ones. Neither a handle nor <see cref="CancelAll" /> reaches another owner's work.
/// </summary>
public interface IExtensionJobs
{
    /// <summary>Queues <paramref name="request" /> and returns its handle.</summary>
    /// <exception cref="ArgumentException">The request names a job kind that is neither built in nor declared.</exception>
    IJobHandle Enqueue(JobRequest request);

    /// <summary>
    ///     Queues <paramref name="work" />. The task completes when the job ran, failed or was removed; it
    ///     faults only if the host could not queue it at all. Without a queue (the browser) it runs on the pool.
    /// </summary>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="work">The job.</param>
    /// <param name="options">How it is queued; null for a background compute job.</param>
    Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null);

    /// <summary>Removes every queued job of this extension. A running one finishes its current step.</summary>
    void CancelAll();
}

/// <summary>
///     The job the calling code runs in, for code that has no job context at hand: a section build several
///     calls deep, a click handler that queues through a shared runner. The host enters every job it runs.
/// </summary>
public static class JobScope
{
    private static readonly AsyncLocal<bool> _userAction = new();
    private static readonly AsyncLocal<CancellationToken> _current = new();

    /// <summary>True inside a <see cref="UserAction" /> scope.</summary>
    public static bool IsUserAction => _userAction.Value;

    /// <summary>
    ///     Marks the jobs queued inside the scope, and in what it awaits, as asked for by the user, whatever their
    ///     <see cref="JobOptions.Priority" />. For one runner shared by a click and a background refresh: the
    ///     click's handler opens the scope.
    /// </summary>
    public static IDisposable UserAction()
    {
        bool outer = _userAction.Value;
        _userAction.Value = true;
        return new Scope(() => _userAction.Value = outer);
    }

    /// <summary>
    ///     Throws <see cref="OperationCanceledException" /> when the job running this code has been stopped, by
    ///     the user or for the user's own job. For work that has no token at hand, at its natural boundaries.
    ///     Never throws outside a job.
    /// </summary>
    public static void ThrowIfStopped() => _current.Value.ThrowIfCancellationRequested();

    /// <summary>True when <paramref name="exception" /> is the queue stopping the running job, which must be let through.</summary>
    public static bool IsStop(Exception exception) =>
        exception is OperationCanceledException && _current.Value.IsCancellationRequested;

    /// <summary>
    ///     Makes <paramref name="token" /> the running job's for <see cref="ThrowIfStopped" /> and
    ///     <see cref="IsStop" /> until disposed. The host enters it around every job it runs.
    /// </summary>
    public static IDisposable Enter(CancellationToken token)
    {
        CancellationToken outer = _current.Value;
        _current.Value = token;
        return new Scope(() => _current.Value = outer);
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
