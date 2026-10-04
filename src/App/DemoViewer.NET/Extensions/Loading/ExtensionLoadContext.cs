#region

using System.Reflection;
using System.Runtime.Loader;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     The context a staged extension assembly loads into: named after the extension, never collectible
///     ("off" is the Phase 1 switch, not an unload), and resolving nothing itself, so every reference the
///     extension makes (the app assembly, Avalonia, CS2DemoKit) falls through to the default context and
///     binds to the copy the app runs on. Only the extension's own assembly, loaded by path, lives here.
///     <para>
///         Not the default context, because the shipped copy is on the trusted platform assembly list:
///         <c>AssemblyLoadContext.Default.LoadFromAssemblyPath</c> of a same-named assembly returns the
///         app-directory copy, not the file named (strat-book-plugin.md §7.8).
///     </para>
/// </summary>
public sealed class ExtensionLoadContext : AssemblyLoadContext
{
    /// <param name="candidate">The staged copy this context is for; names the context.</param>
    public ExtensionLoadContext(ExtensionCandidate candidate)
        : base($"extension:{candidate?.Manifest.Id}@{candidate?.Manifest.Version}", isCollectible: false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
    }

    /// <summary>Null for everything: the default context resolves every dependency.</summary>
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
