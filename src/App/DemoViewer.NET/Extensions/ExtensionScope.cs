#region

using System.Reflection;
using System.Runtime.Loader;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     One extension as fault attribution sees it: who it is, and which code is its own. Code is the
///     extension's when it lives in the entry assembly, or in any assembly of the extension's own load context
///     (its private dependencies). A pack loaded into the default context is matched by its entry assembly
///     alone, since everything else in that context is the app's.
/// </summary>
public sealed class ExtensionScope
{
    /// <param name="id">The extension's id.</param>
    /// <param name="featureId">Its master switch.</param>
    /// <param name="name">Its display name.</param>
    /// <param name="assembly">Its entry assembly.</param>
    /// <param name="ownFeatureIds">Every feature id the extension declares; its master switch is always one.</param>
    public ExtensionScope(string id, string featureId, string name, Assembly assembly, IEnumerable<string>? ownFeatureIds = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(featureId);
        ArgumentNullException.ThrowIfNull(assembly);
        Id = id;
        FeatureId = featureId;
        FeatureIds = new HashSet<string>(ownFeatureIds ?? [], StringComparer.Ordinal) { featureId };
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Assembly = assembly;
        AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(assembly);
        LoadContext = context is null || ReferenceEquals(context, AssemblyLoadContext.Default) ? null : context;
    }

    /// <summary>The extension's id.</summary>
    public string Id { get; }

    /// <summary>Its master switch.</summary>
    public string FeatureId { get; }

    /// <summary>Every feature id the extension declares, its master switch included.</summary>
    public IReadOnlySet<string> FeatureIds { get; }

    /// <summary>Its display name.</summary>
    public string Name { get; }

    /// <summary>Its entry assembly.</summary>
    public Assembly Assembly { get; }

    /// <summary>Its own load context, or null when it shares the app's default context.</summary>
    public AssemblyLoadContext? LoadContext { get; }

    /// <summary>The scope of a loaded pack, named from its manifest when it has one.</summary>
    public static ExtensionScope For(PackStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return For(status.Pack, status.Manifest?.Name);
    }

    /// <summary>The scope of <paramref name="pack" />.</summary>
    /// <param name="pack">The extension.</param>
    /// <param name="name">Its display name; null reads its master switch's label.</param>
    public static ExtensionScope For(IExtension pack, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return new ExtensionScope(pack.Id, pack.FeatureId, name ?? LabelOf(pack), pack.GetType().Assembly, FeatureIdsOf(pack));
    }

    /// <summary>
    ///     True when <paramref name="id" />, a chip, hub, demo action or toolbar item id, may be this extension's:
    ///     it starts with the extension's id and a dot. The extensions the app ships keep their own names.
    /// </summary>
    public bool OwnsId(string id) =>
        Id.StartsWith(Loading.ExternalExtensions.ReservedIdPrefix, StringComparison.Ordinal)
        || id.StartsWith(Id + ".", StringComparison.Ordinal);

    /// <summary>
    ///     <paramref name="requested" /> when it is one of this extension's own features, else its master switch.
    ///     A contribution gated on a feature it does not own would stay live after this extension is switched off.
    /// </summary>
    public string OwnFeature(string? requested) =>
        requested is not null && FeatureIds.Contains(requested) ? requested : FeatureId;

    /// <summary>True when <paramref name="assembly" /> is this extension's code.</summary>
    public bool Owns(Assembly? assembly) =>
        assembly is not null
        && (ReferenceEquals(assembly, Assembly)
            || (LoadContext is not null && ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), LoadContext)));

    /// <inheritdoc />
    public override string ToString() => Name;

    private static IEnumerable<string> FeatureIdsOf(IExtension pack)
    {
        try
        {
            return [.. pack.Features.Select(f => f.Id)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    // The Features getter is extension code and may throw; the id is always there.
    private static string LabelOf(IExtension pack)
    {
        try
        {
            return pack.Features.FirstOrDefault(d => d.Id == pack.FeatureId)?.Label ?? pack.Id;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return pack.Id;
        }
    }
}
