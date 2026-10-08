#region

using System.Reflection;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>Supplies a manifest without an embedded resource. Test doubles only; a real extension embeds its manifest.</summary>
internal interface IManifestSource
{
    ExtensionManifest Manifest { get; }
}

/// <summary>An extension's manifest: the extension.json its build embedded, read once per assembly.</summary>
public static class ExtensionManifests
{
    private static readonly ConditionalWeakTable<Assembly, ExtensionManifest> _embedded = new();

    /// <summary>The manifest <paramref name="extension" /> carries.</summary>
    /// <exception cref="ExtensionManifestException">The assembly embeds no valid manifest.</exception>
    public static ExtensionManifest Of(IExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (extension is IManifestSource source)
        {
            return source.Manifest;
        }

        return _embedded.GetValue(extension.GetType().Assembly, ExtensionManifest.ReadEmbedded);
    }
}
