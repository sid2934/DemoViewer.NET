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
    IReadOnlyList<RecordInput>? dependsOn = null,
    Func<string, DemoDataOwner?>? owners = null)
    : IRecordPass
{
    private const string DataStampPrefix = "data:";

    private readonly Dictionary<string, DemoDataKey> _dataInputs = DataInputs(dependsOn ?? []);

    /// <summary>The extension's pass.</summary>
    public IExtensionRecordPass Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The extension's guard, which faults are reported to.</summary>
    public ExtensionGuard Guard { get; } = guard ?? throw new ArgumentNullException(nameof(guard));

    public string Id { get; } = inner.Id;

    public string Owner => Guard.Scope.Id;

    /// <summary>The inputs the pass named when it registered.</summary>
    public IReadOnlyList<RecordInput> DependsOn { get; } = dependsOn ?? [];

    // Facts stamps are per ruleset: every output of one ruleset is written under one stamp.
    public IReadOnlyList<string> InputStamps { get; } =
        [.. (dependsOn ?? []).Select(StampIdOf).Distinct(StringComparer.Ordinal)];

    /// <summary>The extension ids whose per-demo data the inputs name; their writes must reach the runner.</summary>
    public static IEnumerable<string> DataOwnersOf(IReadOnlyList<RecordInput> dependsOn) =>
        DataInputs(dependsOn).Values.Select(k => k.Owner).Distinct(StringComparer.Ordinal);

    public PackStamp? InputStamp(DemoCacheIndexEntry entry, string id)
    {
        if (!_dataInputs.TryGetValue(id, out DemoDataKey? key))
        {
            return entry.Stamp(id);
        }

        // A pending stamp has no write behind it yet. The count is left out: it moves without a write.
        if (owners?.Invoke(key.Owner)?.Data.Stamp(entry.Path, key.Facet) is not { } stamp || stamp.State == DemoDataState.Pending)
        {
            return null;
        }

        return new PackStamp(id, stamp.Schema, stamp.Fingerprint)
        {
            State = stamp.State == DemoDataState.Failed ? DemoAnalysisState.Failed : DemoAnalysisState.Indexed,
            ComputedAtTicks = stamp.WrittenAtTicks
        };
    }

    public string? Refusal()
    {
        if (DependsOn.Count == 0)
        {
            return null;
        }

        List<string> unknown = [.. _dataInputs.Values.Where(k => owners?.Invoke(k.Owner) is null).Select(k => k.ToString())];
        return RefusalOf([.. DependsOn.OfType<FactKey>()], library.Facts.Declared, IsOff(), unknown);
    }

    public bool InputsOff()
    {
        Func<string, bool> isOff = IsOff();
        return DependsOn.OfType<FactKey>().Any(k => isOff(k.RulesetId))
               || _dataInputs.Values.Any(k => owners?.Invoke(k.Owner) is { } owner && !owner.IsOn());
    }

    private Func<string, bool> IsOff() =>
        library.Facts is IFactsVisibility visibility ? visibility.IsOff : static _ => false;

    /// <summary>Why a pass naming <paramref name="dependsOn" /> cannot be taken, or null when every key is one it can wait on.</summary>
    /// <param name="dependsOn">The fact keys the pass named.</param>
    /// <param name="declared">The outputs the host runs now.</param>
    /// <param name="isOff">True for a ruleset that is known but off now; its keys wait instead of refusing.</param>
    /// <param name="unknownData">Facets the pass named whose owner the host does not know.</param>
    public static string? RefusalOf(IReadOnlyList<FactKey> dependsOn, IReadOnlyList<FactKey> declared, Func<string, bool> isOff,
        IReadOnlyList<string>? unknownData = null)
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

        refused.AddRange(unknownData ?? []);
        return refused.Count == 0
            ? null
            : $"depends on {string.Join(", ", refused)}, which the library does not write.";
    }

    private static string StampIdOf(RecordInput input) => input switch
    {
        FactKey fact => StampedFacts.StampId(fact.RulesetId),
        DemoDataKey data when data == DemoDataKey.RoundFacts => RoundFactsRecords.FacetId,
        DemoDataKey data => DataStampPrefix + data.Owner + "/" + data.Facet,
        _ => throw new ArgumentException($"'{input}' is not an input the host knows.", nameof(input))
    };

    private static Dictionary<string, DemoDataKey> DataInputs(IReadOnlyList<RecordInput> dependsOn)
    {
        Dictionary<string, DemoDataKey> inputs = new(StringComparer.Ordinal);
        foreach (DemoDataKey key in dependsOn.OfType<DemoDataKey>().Where(k => k != DemoDataKey.RoundFacts))
        {
            inputs.TryAdd(StampIdOf(key), key);
        }

        return inputs;
    }

    public bool Wants(DemoCacheIndexEntry entry) => Inner.Wants(library.Project(entry));

    public void Run(DemoCacheRecord record, CancellationToken cancellationToken) =>
        Inner.Run(library.DetailOf(record), cancellationToken);

    /// <summary>
    ///     A factory handing out one host per pass instance, so the runner's per-row memory follows the pass;
    ///     a factory that builds a new pass gets a new host.
    /// </summary>
    /// <param name="contribution">The registered pass.</param>
    /// <param name="library">The host library the pass reads.</param>
    /// <param name="owners">An extension's per-demo data by extension id, or null for one the host does not know.</param>
    public static Func<IRecordPass> Cached(RecordPassContribution contribution, Func<HostLibrary> library,
        Func<string, DemoDataOwner?>? owners = null)
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
                    cached = new ExtensionRecordPassHost(pass, contribution.Guard, library(), contribution.DependsOn, owners);
                }

                return cached;
            }
        };
    }
}

/// <summary>An extension's per-demo data as a record pass input, and whether the extension is on now.</summary>
/// <param name="Data">The extension's data store.</param>
/// <param name="IsOn">True while the extension is on; a pass naming its facets waits while it is off.</param>
internal sealed record DemoDataOwner(IExtensionDemoData Data, Func<bool> IsOn);
