#region

using DemoViewer.NET.Extensions.Loading;

#endregion

namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     One configured pack with its compatibility verdict. <see cref="FeaturePacks.Statuses" /> holds one
///     per configured pack whether or not it passed; Settings reads the list to show each extension's
///     version and, for one that failed, the reason in place of a working switch.
/// </summary>
/// <param name="Pack">The pack as configured.</param>
/// <param name="Manifest">Its manifest, or null when reading it failed (<see cref="PackCompatibility.ManifestInvalid" />).</param>
/// <param name="Compatibility">The verdict.</param>
/// <param name="Source">Where the pack's assembly came from; the shipped copy unless the loader chose a staged one.</param>
/// <param name="Rejected">
///     The staged candidates for this pack the loader looked at and did not load, newest first, each with
///     its reason (strat-book-plugin.md §7.8). Empty when nothing was staged or the staged copy is the one
///     running. Settings shows them under the row so a user can see why an update did not take.
/// </param>
public sealed record PackStatus(
    IFeaturePack Pack,
    ExtensionManifest? Manifest,
    PackCompatibility Compatibility,
    PackSource? Source = null,
    IReadOnlyList<LoadOutcome>? Rejected = null)
{
    /// <summary>True when the pack composes into this app.</summary>
    public bool IsCompatible => Compatibility.IsCompatible;

    /// <summary>The reason in user terms, or null when compatible.</summary>
    public string? Problem => Compatibility.Describe(Manifest, Pack.Id);

    /// <summary>Where the pack's assembly came from; <see cref="PackSource.Bundled" /> when not said.</summary>
    public PackSource Source { get; init; } = Source ?? PackSource.Bundled;

    /// <summary>The staged candidates the loader rejected for this pack; empty when not said.</summary>
    public IReadOnlyList<LoadOutcome> Rejected { get; init; } = Rejected ?? [];

    /// <summary>
    ///     Judges every pack in <paramref name="packs" /> against <paramref name="host" />, in order. A
    ///     manifest that cannot be read, or whose id is not the pack's, fails as
    ///     <see cref="PackCompatibility.ManifestInvalid" />; nothing here throws for a bad pack, since the
    ///     point is to keep it out rather than take the app down with it.
    /// </summary>
    public static IReadOnlyList<PackStatus> Evaluate(IReadOnlyList<IFeaturePack> packs, ExtensionHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(host);
        PackStatus[] statuses = new PackStatus[packs.Count];
        for (int i = 0; i < packs.Count; i++)
        {
            statuses[i] = Evaluate(packs[i], host);
        }

        return statuses;
    }

    /// <summary><see cref="Evaluate(IReadOnlyList{IFeaturePack}, ExtensionHostInfo)" /> for one pack.</summary>
    public static PackStatus Evaluate(IFeaturePack pack, ExtensionHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(host);
        ExtensionManifest manifest;
        try
        {
            manifest = pack.Manifest ?? throw new ExtensionManifestException("the pack declares no manifest");
        }
        catch (ExtensionManifestException ex)
        {
            return new PackStatus(pack, null, new PackCompatibility.ManifestInvalid(ex.Message));
        }

        if (!string.Equals(manifest.Id, pack.Id, StringComparison.Ordinal))
        {
            return new PackStatus(pack, manifest, new PackCompatibility.ManifestInvalid($"manifest id '{manifest.Id}' is not the pack id '{pack.Id}'"));
        }

        return new PackStatus(pack, manifest, PackCompatibility.Check(manifest, host));
    }
}
