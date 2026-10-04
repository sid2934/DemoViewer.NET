#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Provenance;

/// <summary>
///     The Library card's provenance chip: badge-only, no filter. <see cref="ResolveProvenance" />
///     and <see cref="ResolveTeams" /> are the lazy points: the Library only reaches <see cref="BadgeFor" />
///     or <see cref="SetLabel" /> while this contribution's gate resolves on, so neither service is touched
///     while off.
/// </summary>
/// <param name="resolveProvenance">Resolves the live <see cref="IDemoProvenanceSource" />; called at most once.</param>
/// <param name="resolveTeams">Resolves the live <see cref="TeamIdentityService" /> that owns the override store.</param>
/// <param name="featureId">
///     The gate id this shows under; null (the pack's own usage) lets <c>PackContributions.Library</c>
///     stamp it to the owning pack's id. A caller that builds the shell directly (bypassing the pack)
///     names it explicitly, e.g. <c>StratBookPack.PackFeatureId</c>.
/// </param>
public sealed class ProvenanceLibraryContribution(
    Func<IDemoProvenanceSource> resolveProvenance,
    Func<TeamIdentityService> resolveTeams,
    string? featureId = null) : ILibraryContribution
{
    private IDemoProvenanceSource? _provenance;
    private TeamIdentityService? _teams;

    /// <inheritdoc />
    public string? FeatureId => featureId;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public LibraryFilter? Filter => null;

    /// <inheritdoc />
    public bool HasBadge => true;

    /// <inheritdoc />
    public LibraryBadge? BadgeFor(DemoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return BuildBadge(ResolveProvenance().Resolve(entry.FilePath));
    }

    /// <summary>
    ///     One <see cref="IDemoProvenanceSource.ResolveAll" /> call for the whole batch, not a
    ///     <see cref="IDemoProvenanceSource.Resolve" /> per entry: <c>Resolve</c> re-reads and copies Team
    ///     Identity's override list on every call, which a per-card loop would do once per card.
    /// </summary>
    public IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<DemoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<DemoEntry> list = [.. entries];
        IReadOnlyDictionary<string, DemoProvenance> resolved = ResolveProvenance().ResolveAll(list.Select(e => e.FilePath));
        Dictionary<string, LibraryBadge?> badges = new(list.Count, StringComparer.Ordinal);
        foreach (DemoEntry entry in list)
        {
            badges[entry.FilePath] = BuildBadge(resolved.GetValueOrDefault(entry.FilePath));
        }

        return badges;
    }

    private static LibraryBadge BuildBadge(DemoProvenance? p)
    {
        bool pinned = p?.IsOverride ?? false;
        string tooltip = pinned
            ? "Provenance: set by you. Click to change it or go back to automatic."
            : p?.Label is null
                ? "Provenance: nothing decided it yet. Click to set one."
                : "Provenance: automatic, from the clan tags, the header and Team Identity. Click to pin one.";
        return new LibraryBadge(p?.Label ?? "unlabeled", tooltip, pinned);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> BadgeLabels => DemoProvenanceLabel.All;

    /// <inheritdoc />
    public string? BadgeResetLabel => "Automatic";

    /// <inheritdoc />
    public string? BadgeResetTooltip => "Let the clan tags, the header and Team Identity decide";

    /// <inheritdoc />
    public void SetLabel(DemoEntry entry, string? label)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ResolveTeams().SetProvenanceOverride(entry.FilePath, label);
    }

    private IDemoProvenanceSource ResolveProvenance()
    {
        if (_provenance is null)
        {
            _provenance = resolveProvenance();
            _provenance.Changed += () => Changed?.Invoke();
        }

        return _provenance;
    }

    private TeamIdentityService ResolveTeams() => _teams ??= resolveTeams();
}
