#region

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Hands an extension's context only to that extension's own code: an assembly that owns another
///     extension, or none at all, is refused, so a wrong id never reaches another extension's stores. The
///     host comes through <see cref="ForHost" /> instead, which takes no caller.
/// </summary>
/// <remarks>
///     Not a security boundary: a pack already holds the provider, and <c>ExtensionContextKey</c> has
///     structural equality, so code that reflects it out of the SDK resolves the keyed service itself. The
///     check catches a wrong id, not a hostile caller.
/// </remarks>
internal sealed class ExtensionContextAccess : IExtensionContextAccess
{
    private readonly ExtensionScope[] _scopes;
    private readonly IServiceProvider _services;

    // Scopes are named by id: a display name would run the extension's Features getter.
    public ExtensionContextAccess(IServiceProvider services, IEnumerable<IExtension> packs)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _scopes = [.. packs.Select(p => ExtensionScope.For(p, p.Id))];
    }

    public IExtensionContext Resolve(string extensionId, Assembly caller)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        ArgumentNullException.ThrowIfNull(caller);
        if (!_scopes.Any(s => s.Id == extensionId && s.Owns(caller)))
        {
            throw new InvalidOperationException(_scopes.FirstOrDefault(s => s.Owns(caller)) is { } other
                ? $"'{other.Id}' asked for the context of '{extensionId}'. An extension can only resolve its own context."
                : $"'{caller.GetName().Name}' owns no extension and asked for the context of '{extensionId}'. "
                  + "An extension's context resolves only for that extension's own code.");
        }

        return _services.GetRequiredKeyedService<IExtensionContext>(KeyFor(extensionId));
    }

    /// <summary>
    ///     The context of <paramref name="extensionId" />, for the host itself. No caller check: the host is
    ///     the composition root, not an assembly, and only the app's internals reach this.
    /// </summary>
    public static IExtensionContext ForHost(IServiceProvider services, string extensionId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        return services.GetRequiredKeyedService<IExtensionContext>(KeyFor(extensionId));
    }

    /// <summary>The service key <paramref name="extensionId" />'s context is registered under.</summary>
    public static object KeyFor(string extensionId) => new ExtensionContextKey(extensionId);
}
