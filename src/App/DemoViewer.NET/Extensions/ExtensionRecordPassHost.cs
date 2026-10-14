#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     An extension's record pass as one of the host's. The pass sees the SDK's rows, never the cache's types;
///     a throw from <see cref="IExtensionRecordPass.Wants" /> or <see cref="IExtensionRecordPass.Run" />
///     reaches the runner, which skips the pass for that demo and reports it against the extension.
/// </summary>
internal sealed class ExtensionRecordPassHost(
    IExtensionRecordPass inner,
    ExtensionGuard guard,
    HostLibrary library,
    IReadOnlyList<FactKey>? dependsOn = null)
    : IRecordPass
{
    /// <summary>The extension's pass.</summary>
    public IExtensionRecordPass Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The extension's guard, which faults are reported to.</summary>
    public ExtensionGuard Guard { get; } = guard ?? throw new ArgumentNullException(nameof(guard));

    public string Id { get; } = inner.Id;

    public string Owner => Guard.Scope.Id;

    /// <summary>The facts the pass named when it registered.</summary>
    public IReadOnlyList<FactKey> DependsOn { get; } = dependsOn ?? [];

    // Facts stamps are per ruleset: every output of one ruleset is written under one stamp.
    public IReadOnlyList<string> InputStamps { get; } =
        [.. (dependsOn ?? []).Select(k => StampedFacts.StampId(k.RulesetId)).Distinct(StringComparer.Ordinal)];

    public string? Refusal() => DependsOn.Count == 0 ? null : RefusalOf(DependsOn, library.Facts.Declared, IsOff());

    public bool InputsOff()
    {
        Func<string, bool> isOff = IsOff();
        return DependsOn.Any(k => isOff(k.RulesetId));
    }

    private Func<string, bool> IsOff() =>
        library.Facts is IFactsVisibility visibility ? visibility.IsOff : static _ => false;

    /// <summary>Why a pass naming <paramref name="dependsOn" /> cannot be taken, or null when every key is one it can wait on.</summary>
    /// <param name="dependsOn">The keys the pass named.</param>
    /// <param name="declared">The outputs the host runs now.</param>
    /// <param name="isOff">True for a ruleset that is known but off now; its keys wait instead of refusing.</param>
    public static string? RefusalOf(IReadOnlyList<FactKey> dependsOn, IReadOnlyList<FactKey> declared, Func<string, bool> isOff)
    {
        ArgumentNullException.ThrowIfNull(dependsOn);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(isOff);
        List<string> refused = [];
        foreach (FactKey key in dependsOn)
        {
            // A scoreboard is declared but never written, so a pass waiting on it would never run.
            if (string.Equals(key.Output, FactKey.ScoreboardOutput, StringComparison.Ordinal)
                || (!declared.Contains(key) && !isOff(key.RulesetId)))
            {
                refused.Add(key.ToString());
            }
        }

        return refused.Count == 0
            ? null
            : $"depends on {string.Join(", ", refused)}, which the library does not write.";
    }

    public bool Wants(DemoCacheIndexEntry entry) => Inner.Wants(library.Project(entry));

    public void Run(DemoCacheRecord record, CancellationToken cancellationToken) =>
        Inner.Run(library.DetailOf(record), cancellationToken);

    /// <summary>
    ///     A factory handing out one host per pass instance, so the runner's per-row memory follows the pass;
    ///     a factory that builds a new pass gets a new host.
    /// </summary>
    public static Func<IRecordPass> Cached(RecordPassContribution contribution, Func<HostLibrary> library)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(library);
        object gate = new();
        ExtensionRecordPassHost? cached = null;
        return () =>
        {
            IExtensionRecordPass pass = contribution.Factory();
            if (!string.Equals(pass.Id, contribution.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Record pass registered as '{contribution.Id}' built an instance whose Id is '{pass.Id}'.");
            }

            lock (gate)
            {
                if (cached is null || !ReferenceEquals(cached.Inner, pass))
                {
                    cached = new ExtensionRecordPassHost(pass, contribution.Guard, library(), contribution.DependsOn);
                }

                return cached;
            }
        };
    }
}
