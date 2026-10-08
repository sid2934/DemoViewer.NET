namespace DemoViewer.NET.Extensions;

/// <summary>Where each extension's own files live, and the rule a name must pass to become part of a path.</summary>
internal static class ExtensionFolders
{
    /// <summary>The folder under the config and cache roots that holds one folder per extension.</summary>
    public const string DataDirectoryName = "extension-data";

    /// <summary>
    ///     <paramref name="name" /> when it is safe as one path segment: letters, digits, '.', '-' and '_', at
    ///     most 128 characters, and not made of dots alone. Anything else throws, so no extension id or
    ///     facet name can climb out of the folder it names.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a safe path segment.</exception>
    public static string SafeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsSafeName(name))
        {
            throw new ArgumentException($"'{name}' is not usable as a folder or file name: letters, digits, '.', '-' and '_' only.",
                nameof(name));
        }

        return name;
    }

    /// <summary>True when <paramref name="name" /> passes <see cref="SafeName" />.</summary>
    public static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 128
        && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_')
        && name.Any(c => c != '.');
}
