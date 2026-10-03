#region

using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.RoundIndex;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The default production count behind the Settings "N demos will be re-indexed in the background"
///     notice (architecture doc §8): the union of every pack evaluator's own <c>PendingPaths()</c>, which
///     already honours each evaluator's own background-indexing opt-in. The Settings view-model cannot
///     take any of these evaluators as a constructor dependency (its shape is fixed, and the composition
///     root wires it before this notice existed), so it resolves them through the app's service locator at
///     the moment the pack's switch flips on, the same pattern <c>StratBookPack</c> itself uses for a
///     lazily-reached shell service.
/// </summary>
internal static class StratBookPendingReindexCount
{
    /// <summary>Runs the count off the UI thread; a null <see cref="App.Services" /> (tests, a host not yet composed) counts zero.</summary>
    public static Task<int> ComputeAsync() => Task.Run(() => Compute(App.Services));

    internal static int Compute(IServiceProvider? services)
    {
        if (services is null)
        {
            return 0;
        }

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
