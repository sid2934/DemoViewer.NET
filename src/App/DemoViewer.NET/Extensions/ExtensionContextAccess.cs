#region

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Hands an extension's context to that extension's own code, or to the host. Anyone else is refused, so a
///     wrong id never reaches another extension's stores.
///     <para>
///         The host side is an explicit allow-list rather than "owns no extension", which is what it used to
///         be. A negative test like that admits every assembly that happens to carry no pack: a head, the
///         capture tool, a test harness, a future plugin host. None of those is an attacker today, and a
///         third-party pack cannot take that path at all because it arrives in its own
///         <see cref="Loading.ExternalLoadContext" /> and therefore owns a scope. But the set was open by
///         construction, and an allow-list is the difference between first-party code being trusted on
///         purpose and being trusted by accident.
///     </para>
/// </summary>
internal sealed class ExtensionContextAccess : IExtensionContextAccess
{
    private readonly Assembly[] _hostAssemblies;
    private readonly ExtensionScope[] _scopes;
    private readonly IServiceProvider _services;

    // Scopes are named by id: a display name would run the extension's Features getter.
    public ExtensionContextAccess(IServiceProvider services, IEnumerable<IExtension> packs)
        : this(services, packs, [typeof(ExtensionContextAccess).Assembly])
    {
    }

    /// <summary>
    ///     Test seam, and the declaration of who the host is. The default is this assembly alone, which is
    ///     the only one that registers this service and the only one that resolves a context it does not own
    ///     (<see cref="PackContributionSet" /> builds each pack's contributions). The heads, the capture tool
    ///     and the test projects are deliberately absent: none of them calls
    ///     <c>GetExtensionContext</c>, and a test that does gets the fallback path, because no test registers
    ///     this service.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="packs">The loaded extensions.</param>
    /// <param name="hostAssemblies">Assemblies allowed to resolve any extension's context.</param>
    internal ExtensionContextAccess(IServiceProvider services, IEnumerable<IExtension> packs,
        IReadOnlyCollection<Assembly> hostAssemblies)
    {
        ArgumentNullException.ThrowIfNull(hostAssemblies);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _scopes = [.. packs.Select(p => ExtensionScope.For(p, p.Id))];
        _hostAssemblies = [.. hostAssemblies];
    }

    public IExtensionContext Resolve(string extensionId, Assembly caller)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        ArgumentNullException.ThrowIfNull(caller);
        if (!_scopes.Any(s => s.Id == extensionId && s.Owns(caller))
            && !_hostAssemblies.Any(h => ReferenceEquals(h, caller)))
        {
            throw new InvalidOperationException(_scopes.FirstOrDefault(s => s.Owns(caller)) is { } other
                ? $"'{other.Id}' asked for the context of '{extensionId}'. An extension can only resolve its own context."
                : $"'{caller.GetName().Name}' is neither the host nor an extension, so it cannot resolve the context of '{extensionId}'.");
        }

        return _services.GetRequiredKeyedService<IExtensionContext>(KeyFor(extensionId));
    }

    /// <summary>The service key <paramref name="extensionId" />'s context is registered under.</summary>
    public static object KeyFor(string extensionId) => new ExtensionContextKey(extensionId);
}
