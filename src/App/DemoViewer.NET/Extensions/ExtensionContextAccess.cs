#region

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Hands an extension's context only to that extension's own code or to the app's. Code of one extension
///     asking for another's context is refused, so a wrong id never reaches another extension's stores.
/// </summary>
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
        if (!_scopes.Any(s => s.Id == extensionId && s.Owns(caller))
            && _scopes.FirstOrDefault(s => s.Owns(caller)) is { } other)
        {
            throw new InvalidOperationException(
                $"'{other.Id}' asked for the context of '{extensionId}'. An extension can only resolve its own context.");
        }

        return _services.GetRequiredKeyedService<IExtensionContext>(extensionId);
    }
}
