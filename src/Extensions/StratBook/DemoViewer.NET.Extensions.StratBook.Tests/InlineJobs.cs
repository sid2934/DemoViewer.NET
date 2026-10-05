namespace DemoViewer.NET.AppTests;

/// <summary>
///     An <see cref="IExtensionJobs" /> that runs each job on the caller's thread and records what was
///     submitted, so a test sees the options a service queued its work with.
/// </summary>
internal sealed class InlineJobs : IExtensionJobs
{
    private readonly List<(string Title, JobOptions Options)> _submitted = [];

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

    public CancellationToken JobToken { get; set; }

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

    private sealed class Context(CancellationToken token) : IJobContext
    {
        public CancellationToken CancellationToken => token;

        public void Report(int done, int total, string? detail = null)
        {
        }
    }
}
