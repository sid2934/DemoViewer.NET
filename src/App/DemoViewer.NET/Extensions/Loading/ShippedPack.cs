namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     A pack compiled into the head, named without touching its type. The head cannot write
///     <c>new StratBookPack()</c> where the loader can see it: the JIT loads an assembly when it compiles a
///     method that mentions one of its types, even on a branch not taken, so the construction sits in
///     <see cref="Create" /> and runs only when the shipped copy is the one chosen. The shipped version
///     comes from the manifest copied beside the DLL (<see cref="ManifestPath" />), not from the type.
/// </summary>
/// <param name="Id">The pack id, equal to the manifest's.</param>
/// <param name="Create">Constructs the compiled-in pack. Called at most once, and never when a staged copy wins.</param>
/// <param name="ManifestPath">The shipped <c>extension.json</c>, as a full path.</param>
public sealed record ShippedPack(string Id, Func<IFeaturePack> Create, string ManifestPath)
{
    /// <summary>
    ///     The shipped pack whose manifest the build copied beside the app
    ///     (<c>&lt;AppContext.BaseDirectory&gt;/extension.json</c>, strat-book-plugin.md §7.7).
    /// </summary>
    public static ShippedPack BesideApp(string id, Func<IFeaturePack> create) =>
        new(id, create, Path.Combine(AppContext.BaseDirectory, Manifest.ExtensionManifest.FileName));
}
