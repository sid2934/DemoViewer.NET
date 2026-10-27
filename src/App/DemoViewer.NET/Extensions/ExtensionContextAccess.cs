#region

using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Where an extension's <see cref="IExtensionContext" /> is registered, and the host's own way to it.
///     The SDK's <c>GetExtensionContext&lt;TPack&gt;</c> keys on the pack's type, which an extension can only
///     name for itself; the host holds ids rather than types, so it comes through <see cref="ForHost" />.
/// </summary>
/// <remarks>
///     Not a security boundary: a pack already holds the provider, and both keys are reachable by
///     reflection. Type identity makes a wrong id an accident that cannot be written, not a caller the host
///     turns away.
/// </remarks>
internal static class ExtensionContextAccess
{
    /// <summary>
    ///     Registers <paramref name="pack" />'s context under both keys the host hands out: its id, and the
    ///     pack's own type. One instance answers for both, so every route sees the same stores and the same
    ///     single-flight export.
    /// </summary>
    public static void Register(IServiceCollection services, IExtension pack)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pack);
        object key = KeyFor(pack.Id);
        services.AddKeyedSingleton<IExtensionContext>(key, (sp, _) => new ExtensionContext(pack, sp));
        services.AddKeyedSingleton<IExtensionContext>(pack.GetType(),
            (sp, _) => sp.GetRequiredKeyedService<IExtensionContext>(key));
    }

    /// <summary>
    ///     The context of <paramref name="extensionId" />, for the host itself. The host is the composition
    ///     root rather than an extension, so it names an id where an extension names its type.
    /// </summary>
    public static IExtensionContext ForHost(IServiceProvider services, string extensionId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        return services.GetRequiredKeyedService<IExtensionContext>(KeyFor(extensionId));
    }

    /// <summary>The service key <paramref name="extensionId" />'s context is registered under for the host.</summary>
    public static object KeyFor(string extensionId) => new ExtensionContextKey(extensionId);
}
