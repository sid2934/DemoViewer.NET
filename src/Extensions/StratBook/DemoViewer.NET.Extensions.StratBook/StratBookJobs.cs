namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>Runs a piece of the pack's work as a processing queue job and hands its result back.</summary>
internal static class StratBookJobs
{
    /// <summary>
    ///     Queues <paramref name="work" /> and completes with its result, or <paramref name="fallback" /> when the
    ///     job was removed before it ran. A throw from the work faults the task. Without a queue (null jobs,
    ///     tests) it runs on the pool.
    /// </summary>
    public static async Task<T> RunAsync<T>(IExtensionJobs? jobs, string title, Func<T> work, T fallback, JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (jobs is null)
        {
            return await Task.Run(work).ConfigureAwait(false);
        }

        T result = fallback;
        Exception? failure = null;
        await jobs.RunAsync(title, _ =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex) when (!JobScope.IsStop(ex))
            {
                failure = ex;
            }

            return Task.CompletedTask;
        }, options).ConfigureAwait(false);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        return result;
    }
}
