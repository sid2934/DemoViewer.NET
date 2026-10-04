#region

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     An extension's <c>extension.json</c>: what it is, which assembly and type to load, and which host it
///     fits. Read by the host before the assembly is loaded (the on-disk copy beside the DLL) and by the pack
///     itself in process (the embedded copy), so both sides describe one file. Parsing is strict about the
///     required members and ignores members it does not know, so a newer manifest still loads on an older
///     app, which then judges it by the fields it understands.
/// </summary>
/// <param name="Id">The pack id (<see cref="IExtension.Id" />), reverse-DNS.</param>
/// <param name="Name">The user-facing name ("Strat Book").</param>
/// <param name="Version">The extension's own version.</param>
/// <param name="Assembly">The assembly file name, no directory.</param>
/// <param name="EntryType">The full name of the <see cref="IExtension" /> type.</param>
/// <param name="RequiresHost">The <see cref="ExtensionHost.ContractVersion" /> range the extension was built against.</param>
/// <param name="RequiresCs2DemoKit">The CS2DemoKit range; exact by default, since the extension uses its types directly.</param>
/// <param name="MinAppVersion">The oldest app release the extension runs on, or null for any.</param>
public sealed partial record ExtensionManifest(
    string Id,
    string Name,
    SemVersion Version,
    string Assembly,
    string EntryType,
    VersionRange RequiresHost,
    VersionRange RequiresCs2DemoKit,
    SemVersion? MinAppVersion = null)
{
    /// <summary>The resource and file name both copies carry.</summary>
    public const string FileName = "extension.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses manifest JSON.</summary>
    /// <exception cref="ExtensionManifestException">Malformed JSON, a missing required member, or a bad value.</exception>
    public static ExtensionManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Dto dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, _json) ?? throw new ExtensionManifestException("The manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new ExtensionManifestException("The manifest is not valid: " + ex.Message, ex);
        }

        return FromDto(dto);
    }

    /// <summary>Parses manifest JSON from <paramref name="stream" />.</summary>
    /// <exception cref="ExtensionManifestException">Malformed JSON, a missing required member, or a bad value.</exception>
    public static ExtensionManifest Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads the copy embedded in <paramref name="assembly" /> under <see cref="FileName" />.</summary>
    /// <exception cref="ExtensionManifestException">No such resource, or the manifest does not parse.</exception>
    public static ExtensionManifest ReadEmbedded(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using Stream stream = assembly.GetManifestResourceStream(FileName)
            ?? throw new ExtensionManifestException($"{assembly.GetName().Name} embeds no {FileName}.");
        return Parse(stream);
    }

    private static ExtensionManifest FromDto(Dto dto)
    {
        string id = Required(dto.Id, "id");
        if (!IsValidId(id))
        {
            throw new ExtensionManifestException($"'id' must be a reverse-DNS name; got '{id}'.");
        }

        string assembly = Required(dto.Assembly, "assembly");
        if (!string.Equals(Path.GetFileName(assembly), assembly, StringComparison.Ordinal)
            || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtensionManifestException($"'assembly' must be a bare .dll file name; got '{assembly}'.");
        }

        if (!SemVersion.TryParse(Required(dto.Version, "version"), out SemVersion? version))
        {
            throw new ExtensionManifestException($"'version' is not a semantic version: '{dto.Version}'.");
        }

        if (!VersionRange.TryParse(Required(dto.RequiresHost, "requiresHost"), out VersionRange? host))
        {
            throw new ExtensionManifestException($"'requiresHost' is not a version range: '{dto.RequiresHost}'.");
        }

        if (!VersionRange.TryParse(Required(dto.RequiresCs2DemoKit, "requiresCs2DemoKit"), out VersionRange? kit))
        {
            throw new ExtensionManifestException($"'requiresCs2DemoKit' is not a version range: '{dto.RequiresCs2DemoKit}'.");
        }

        SemVersion? minApp = null;
        if (dto.MinAppVersion is { } minText && !SemVersion.TryParse(minText, out minApp))
        {
            throw new ExtensionManifestException($"'minAppVersion' is not a semantic version: '{minText}'.");
        }

        return new ExtensionManifest(id, Required(dto.Name, "name"), version, assembly, Required(dto.EntryType, "entryType"), host, kit, minApp);
    }

    /// <summary>
    ///     The id rule the manifest and the feed share: reverse-DNS over <c>[A-Za-z0-9._-]</c>, starting
    ///     with a letter or digit. The id names a folder under the config root, and the loader skips dot
    ///     folders, so a leading dot is refused here rather than hidden there.
    /// </summary>
    public static bool IsValidId(string? id) =>
        id is not null && IdPattern().IsMatch(id) && id.Contains('.', StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdPattern();

    private static string Required(string? value, string member) =>
        string.IsNullOrWhiteSpace(value) ? throw new ExtensionManifestException($"'{member}' is required.") : value.Trim();

    // [JsonRequired] fails the deserialization when a member is absent; a present null or blank fails in
    // Required above, so both read as "missing".
    private sealed class Dto
    {
        [JsonRequired]
        public string? Id { get; set; }

        [JsonRequired]
        public string? Name { get; set; }

        [JsonRequired]
        public string? Version { get; set; }

        [JsonRequired]
        public string? Assembly { get; set; }

        [JsonRequired]
        public string? EntryType { get; set; }

        [JsonRequired]
        public string? RequiresHost { get; set; }

        [JsonRequired]
        public string? RequiresCs2DemoKit { get; set; }

        public string? MinAppVersion { get; set; }
    }
}

/// <summary>A manifest that could not be read or did not validate.</summary>
public sealed class ExtensionManifestException : Exception
{
    public ExtensionManifestException()
    {
    }

    public ExtensionManifestException(string message) : base(message)
    {
    }

    public ExtensionManifestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
