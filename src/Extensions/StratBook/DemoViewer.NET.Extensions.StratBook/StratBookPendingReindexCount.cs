#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundIndex;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's answer for the Settings "N demos will be re-indexed in the background" notice:
///     the union of every pack evaluator's own <c>PendingPaths()</c>, derived from the index, which already
///     honours each evaluator's own background-indexing opt-in. Built once per container, over the same
///     <see cref="IServiceProvider" /> every other <c>Contribute</c> closure captures, so
///     <c>SettingsViewModel</c> never needs to resolve these evaluators (or know this type exists) itself.
/// </summary>
internal sealed class StratBookPendingReindexCount(IServiceProvider services) : IReindexEstimate
{
    /// <inheritdoc />
    public string FeatureId => StratBookPack.PackFeatureId;

    /// <summary>
    ///     Runs the count as a queue item the user can see and pause, at user priority (the standing rule:
    ///     every off-UI-thread job goes through the one queue, and user-triggered work jumps the line). A
    ///     null <see cref="QueueWork.Ambient" /> (tests, the browser head) falls back to the pool.
    /// </summary>
    public Task<int> CountAsync() =>
        QueueWork.RunAsync(QueueWork.Ambient, QueueJobKind.SectionCompute, "Settings: demos to re-index",
            "settings", Compute, 0, DemoJobPriority.UserRequested);

    private int Compute()
    {
        HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
        Collect(pending, services.GetService<RoundIndexEvaluator>()?.PendingPaths());
        Collect(pending, services.GetService<GrenadeIndexEvaluator>()?.PendingPaths());
        Collect(pending, services.GetService<SuggestedTagsService>()?.PendingPaths());
        return pending.Count;
    }

    private static void Collect(HashSet<string> pending, IReadOnlyList<string>? paths)
    {
        if (paths is null)
        {
            return;
        }

        foreach (string path in paths)
        {
            pending.Add(path);
        }
    }
}
