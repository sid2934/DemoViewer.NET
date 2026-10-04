#region

using System.Globalization;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     What the strat editor may show for a step's lineup. Answers from a cache only: while a map is being grouped
///     it has no choices and resolves nothing, and the host re-projects when the map is ready.
/// </summary>
public interface IStratLineupCatalog
{
    /// <summary>The map's lineups of a strat utility kind, most thrown first; empty while not ready.</summary>
    /// <param name="map">The strat's map.</param>
    /// <param name="utilityKind">A strat utility kind (<c>smoke</c>, <c>molotov</c>, ...).</param>
    IReadOnlyList<StratLineupOption> Options(string map, string utilityKind);

    /// <summary>The lineup a stored id names, an alias id included, or null (unknown, or not ready).</summary>
    /// <param name="map">The strat's map.</param>
    /// <param name="lineupId">A stored lineup id.</param>
    StratLineupChoice? Resolve(string map, Guid lineupId);
}

/// <summary>A resolved lineup: its primary id, title, strat utility kind and techniques, most thrown first.</summary>
public sealed record StratLineupChoice(Guid Id, string Title, string UtilityKind, IReadOnlyList<StratTechniqueOption> Techniques);

/// <summary>One way a lineup is thrown: a <see cref="GrenadeLineups.TechniqueKey" /> and what the combo shows.</summary>
public sealed record StratTechniqueOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The editor's catalog over <see cref="LineupOriginSource" />'s per-map grouping, which runs off the UI thread.</summary>
/// <param name="source">The grouped lineups.</param>
public sealed class StratLineupCatalog(LineupOriginSource source) : IStratLineupCatalog
{
    // "Molotov" in the strat vocabulary covers both the Grenade Index's Molotov and Incendiary kinds
    // (the Grenade Index tells them apart; the Strat Model's closed utility kind list does not).
    private static readonly Dictionary<string, IReadOnlySet<GrenadeKind>> KindsByUtility = new(StringComparer.Ordinal)
    {
        ["smoke"] = new HashSet<GrenadeKind> { GrenadeKind.Smoke },
        ["molotov"] = new HashSet<GrenadeKind> { GrenadeKind.Molotov, GrenadeKind.Incendiary },
        ["he"] = new HashSet<GrenadeKind> { GrenadeKind.He },
        ["flash"] = new HashSet<GrenadeKind> { GrenadeKind.Flash },
        ["decoy"] = new HashSet<GrenadeKind> { GrenadeKind.Decoy }
    };

    /// <summary>The grenade kinds a strat utility kind stands for, or null for one outside the vocabulary.</summary>
    /// <param name="utilityKind">A strat utility kind.</param>
    public static IReadOnlySet<GrenadeKind>? GrenadeKindsFor(string utilityKind) => KindsByUtility.GetValueOrDefault(utilityKind);

    /// <summary>The strat utility kind a grenade kind is written as.</summary>
    /// <param name="kind">A grenade kind.</param>
    public static string UtilityKindOf(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Molotov or GrenadeKind.Incendiary => "molotov",
        GrenadeKind.He => "he",
        GrenadeKind.Flash => "flash",
        GrenadeKind.Decoy => "decoy",
        _ => "smoke"
    };

    /// <summary>A lineup's techniques as the editor offers them: label and throw count.</summary>
    /// <param name="lineup">The lineup.</param>
    public static IReadOnlyList<StratTechniqueOption> TechniquesOf(GrenadeLineup lineup)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        return
        [
            .. lineup.Techniques.Select(t => new StratTechniqueOption(t.Key,
                string.Create(CultureInfo.InvariantCulture, $"{t.Label} ({t.Throws.Count})")))
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<StratLineupOption> Options(string map, string utilityKind) =>
        GrenadeKindsFor(utilityKind) is { } kinds && source.For(map) is { } lineups
            ? [.. lineups.Lineups.Where(l => kinds.Contains(l.Kind)).Select(l => new StratLineupOption(l.Lineup.Id, l.Title))]
            : [];

    /// <inheritdoc />
    public StratLineupChoice? Resolve(string map, Guid lineupId) =>
        source.For(map)?.ById.GetValueOrDefault(lineupId) is { } found
            ? new StratLineupChoice(found.Lineup.Id, found.Title, UtilityKindOf(found.Kind), TechniquesOf(found.Lineup))
            : null;
}
