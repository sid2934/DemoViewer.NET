namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>How urgent a job is.</summary>
public enum JobPriority
{
    /// <summary>Indexing and anything the user did not ask for. Pausable, runs when the app is idle.</summary>
    Background,

    /// <summary>Work a click asked for: it goes to the front and stops background work.</summary>
    UserRequested
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
}

/// <summary>
///     The app's processing queue. Every job the extension runs off the UI thread goes through it, so the user
///     sees it and can pause or remove it. Jobs carry the extension's id, and switching the extension off
///     cancels the queued ones.
/// </summary>
public interface IExtensionJobs
{
    /// <summary>
    ///     Queues <paramref name="work" />. The task completes when the job ran, failed or was removed; it
    ///     faults only if the host could not queue it at all.
    /// </summary>
    /// <param name="title">The line the queue list shows.</param>
    /// <param name="work">The job.</param>
    /// <param name="options">How it is queued; null for a background compute job.</param>
    Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null);

    /// <summary>Removes every queued job of this extension. A running one finishes its current step.</summary>
    void CancelAll();
}
