#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     A staged extension directory whose manifest parsed and matches its place in the tree:
///     <c>&lt;extensions&gt;/&lt;id&gt;/&lt;version&gt;/</c> with <c>id</c> and <c>version</c> equal to the
///     manifest's. Nothing has been loaded yet; <see cref="ExtensionLoader.Select" /> judges it and
///     <see cref="ExtensionLoader.Load" /> loads it.
/// </summary>
/// <param name="Directory">The version directory, as a full path.</param>
/// <param name="Manifest">Its <c>extension.json</c>.</param>
public sealed record ExtensionCandidate(string Directory, ExtensionManifest Manifest)
{
    /// <summary>The assembly the manifest names, inside <see cref="Directory" />.</summary>
    public string AssemblyPath => Path.Combine(Directory, Manifest.Assembly);
}
