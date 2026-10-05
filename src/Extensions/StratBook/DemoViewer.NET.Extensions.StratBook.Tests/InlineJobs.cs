#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An <see cref="IExtensionJobs" /> that runs each job on the caller's thread and records what was
///     submitted, so a test sees the options a service queued its work with. A job on a demo gets the parse
///     <see cref="ParseDemo" /> hands out and counts as one read in <see cref="DemoReads" />.
/// </summary>
internal sealed class InlineJobs : IExtensionJobs
{
    private readonly List<(string Title, JobOptions Options)> _submitted = [];
    private readonly List<string> _demoReads = [];

    public IReadOnlyList<(string Title, JobOptions Options)> Submitted
    {
        get
        {
            lock (_submitted)
            {
                return [.. _submitted];
            }
        }
    }

    /// <summary>The demo paths jobs on a demo ran on, one per job.</summary>
    public IReadOnlyList<string> DemoReads
    {
        get
        {
            lock (_demoReads)
            {
                return [.. _demoReads];
            }
        }
    }

    public CancellationToken JobToken { get; set; }

    /// <summary>The parse a job on a demo gets; a job on a demo is rejected when null.</summary>
    public Func<string, ParsedDemo>? ParseDemo { get; set; }

    public IJobHandle Enqueue(JobRequest request)
    {
        lock (_submitted)
        {
            _submitted.Add((request.Title, request.Options));
        }

        if (request.DemoPath is { } path)
        {
            if (ParseDemo is not { } parse)
            {
                return Handle.Ended(new JobResult(JobStatus.Rejected));
            }

            lock (_demoReads)
            {
                _demoReads.Add(path);
            }

            return Handle.Of(request.DemoWork!(new DemoContext(path, parse(path), JobToken)));
        }

        return Handle.Of(request.Work!(new Context(JobToken)));
    }

    public Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null)
    {
        lock (_submitted)
        {
            _submitted.Add((title, options ?? new JobOptions()));
        }

        return work(new Context(JobToken));
    }

    public void CancelAll()
    {
    }

    public IDisposable UserAction() => new Nothing();

    public void ThrowIfStopped() => JobToken.ThrowIfCancellationRequested();

    public bool IsStop(Exception exception) => exception is OperationCanceledException && JobToken.IsCancellationRequested;

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Context(CancellationToken token) : IJobContext
    {
        public CancellationToken CancellationToken => token;

        public void Report(int done, int total, string? detail = null)
        {
        }
    }

    private sealed class DemoContext(string path, ParsedDemo parsed, CancellationToken token) : IDemoJobContext
    {
        public CancellationToken CancellationToken => token;

        public string DemoPath => path;

        public ParsedDemo Parsed => parsed;

        public void Report(int done, int total, string? detail = null)
        {
        }
    }

    private sealed class Handle : IJobHandle
    {
        private readonly Task<JobResult> _completion;

        private Handle(Task<JobResult> completion) => _completion = completion;

        public JobStatus Status => _completion.IsCompleted ? _completion.Result.Status : JobStatus.Running;

        public Task<JobResult> Completion => _completion;

        public event Action<JobResult>? Completed
        {
            add => _completion.ContinueWith(t => value?.Invoke(t.Result), TaskScheduler.Default);
            remove { }
        }

        public void Cancel()
        {
        }

        public static Handle Ended(JobResult result) => new(Task.FromResult(result));

        public static Handle Of(Task work) => new(Finish(work));

        private static async Task<JobResult> Finish(Task work)
        {
            try
            {
                await work.ConfigureAwait(false);
                return new JobResult(JobStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                return new JobResult(JobStatus.Cancelled);
            }
            catch (Exception ex)
            {
                return new JobResult(JobStatus.Failed, ex);
            }
        }
    }
}
