#region

using System.Reflection;
using System.Runtime.Loader;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     Loads a third-party extension's own dependencies from its folder. Anything the app already ships (the
///     SDK, Avalonia, CS2DemoKit, the runtime) resolves from the app, so the extension and the host share one
///     copy of every contract type, whatever the extension's folder carries.
/// </summary>
public sealed class ExternalLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public ExternalLoadContext(ExtensionCandidate candidate)
        : base($"external:{candidate?.Manifest.Id}@{candidate?.Manifest.Version}", isCollectible: false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _directory = candidate.Directory;
        try
        {
            _resolver = new AssemblyDependencyResolver(candidate.AssemblyPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _resolver = null;
        }
    }

    /// <summary>True when the app ships an assembly of this name, so the extension must use the app's copy.</summary>
    public static bool IsShared(string simpleName)
    {
        ArgumentNullException.ThrowIfNull(simpleName);
        foreach (Assembly loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return File.Exists(Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"))
               || TrustedPlatformAssembly(simpleName);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name || IsShared(name))
        {
            return null;
        }

        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string local = Path.Combine(_directory, name + ".dll");
            path = File.Exists(local) ? local : null;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    // The runtime's own assemblies sit in the shared framework, not beside the app.
    private static bool TrustedPlatformAssembly(string simpleName) =>
        AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list
        && list.Split(Path.PathSeparator).Any(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), simpleName, StringComparison.OrdinalIgnoreCase));
}
