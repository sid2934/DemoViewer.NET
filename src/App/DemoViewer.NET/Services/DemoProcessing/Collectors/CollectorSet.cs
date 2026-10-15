#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing.Collectors;

/// <summary>An extension's collector as the host holds it: the contribution and the guard its faults go to.</summary>
/// <param name="Contribution">What the extension registered.</param>
/// <param name="Guard">The owning extension's guard; null for a host-owned collector.</param>
internal sealed record RegisteredCollector(CollectorContribution Contribution, ICollectorFaultReporter? Guard)
{
    public string Id => Contribution.Id;

    /// <summary>
    ///     Why the engine would refuse <paramref name="contribution" />, or null when it would not. A provider name
    ///     the engine does not register, or a class outside the grenade projectiles, throws at build, which would
    ///     fail every run the collector rides.
    /// </summary>
    public static string? Refusal(CollectorContribution contribution)
    {
        if (string.IsNullOrWhiteSpace(contribution.Id) || string.IsNullOrWhiteSpace(contribution.PassId))
        {
            return "a collector needs an id and the id of the pass that reads it";
        }

        PerPlayerEntityValueProviderRegistry providers = PerPlayerEntityValueProviderRegistry.CreateDefault();
        foreach (string name in contribution.Providers)
        {
            if (string.IsNullOrWhiteSpace(name) || providers.Get(name) is null)
            {
                return $"collector {contribution.Id} reads '{name}', which is no per-player provider of this engine";
            }
        }

        foreach (string name in contribution.ProjectileClasses)
        {
            if (!GrenadeProjectileClasses.Contains(name))
            {
                return $"collector {contribution.Id} reads projectile class '{name}', which is not one of {string.Join(", ", GrenadeProjectileClasses.All)}";
            }
        }

        return null;
    }
}

/// <summary>
///     The collectors one run carries: what the visit's passes registered, unioned. The run declares the union of
///     their reads; <see cref="Key" /> is what a cached run is matched by.
/// </summary>
internal sealed class CollectorSet
{
    private readonly Dictionary<string, RegisteredCollector> _byId;

    private CollectorSet(IEnumerable<RegisteredCollector> collectors)
    {
        _byId = new Dictionary<string, RegisteredCollector>(StringComparer.Ordinal);
        foreach (RegisteredCollector collector in collectors)
        {
            _byId.TryAdd(collector.Id, collector);
        }

        All = [.. _byId.Values.OrderBy(c => c.Id, StringComparer.Ordinal)];
        Providers = [.. All.SelectMany(c => c.Contribution.Providers).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
        ProjectileClasses = [.. All.SelectMany(c => c.Contribution.ProjectileClasses).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Key = string.Join("\n", All.Select(c => c.Contribution.Stamp));
    }

    public static CollectorSet Empty { get; } = new([]);

    /// <summary>Every collector, by id.</summary>
    public IReadOnlyList<RegisteredCollector> All { get; }

    /// <summary>The union of the declared providers, each once.</summary>
    public IReadOnlyList<string> Providers { get; }

    /// <summary>The union of the declared projectile classes, each once.</summary>
    public IReadOnlyList<string> ProjectileClasses { get; }

    /// <summary>The members' stamps; two sets with one key run the same reads.</summary>
    public string Key { get; }

    public bool IsEmpty => _byId.Count == 0;

    public static CollectorSet Of(IEnumerable<RegisteredCollector> collectors) => new(collectors);

    public CollectorSet Union(CollectorSet other) =>
        other.IsEmpty ? this : IsEmpty ? other : new CollectorSet([.. All, .. other.All]);

    /// <summary>True when every member of <paramref name="other" /> is here under the same stamp.</summary>
    public bool IsSupersetOf(CollectorSet other) =>
        other.All.All(c => _byId.TryGetValue(c.Id, out RegisteredCollector? mine)
                           && string.Equals(mine.Contribution.Stamp, c.Contribution.Stamp, StringComparison.Ordinal));

    public bool Contains(string collectorId) => _byId.ContainsKey(collectorId);

    /// <summary><paramref name="options" /> declaring this set's reads, with a fresh observer over it; unchanged when empty.</summary>
    public (AnalysisOptions Options, CollectorObserver? Observer) Declare(AnalysisOptions options)
    {
        if (IsEmpty)
        {
            return (options, null);
        }

        CollectorObserver observer = new(this);
        return (options with
        {
            ObservedProviders = Providers,
            ObservedProjectileClasses = ProjectileClasses,
            FrameObserver = observer
        }, observer);
    }

    /// <summary><paramref name="options" /> declaring this set's reads for a build; unchanged when empty.</summary>
    public AnalysisOptions DeclareReads(AnalysisOptions options) =>
        IsEmpty ? options : options with { ObservedProviders = Providers, ObservedProjectileClasses = ProjectileClasses };
}
