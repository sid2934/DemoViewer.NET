#region

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     One logical demo: what a test asks for, and enough to decide whether a local file is it.
///     <see cref="Sha256" /> is the identity; <see cref="Dir" /> and <see cref="File" /> are only
///     location hints, so a corpus that moves between machines keeps the same entries.
/// </summary>
/// <param name="Name">Logical name, stable across corpus reorganisations.</param>
/// <param name="File">File name, without a directory.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file's bytes, or null when identity is not pinned.</param>
/// <param name="Bytes">Length in bytes. Checked before the hash, so a wrong file is usually rejected without reading it.</param>
/// <param name="Dir">Corpus-relative directory, or null for the corpus root.</param>
/// <param name="Sets">Set membership, for consumers that take a whole set rather than one demo.</param>
public sealed record CorpusDemo(
    string Name,
    string File,
    string? Sha256,
    long Bytes,
    string? Dir,
    IReadOnlyList<string> Sets)
{
    /// <summary>The path this demo takes under a corpus root.</summary>
    public string PathUnder(string corpusRoot) =>
        Dir is null ? Path.Combine(corpusRoot, File) : Path.Combine(corpusRoot, Dir, File);
}

/// <summary>
///     The committed record of which demos the corpus holds, read from
///     <c>corpus.manifest.json</c> embedded in this assembly.
///     <para>
///         <b>Embedded rather than a file in <c>demos/</c>.</b> Under a corpus-root override the
///         <c>demos/</c> directory belongs to another checkout, so a manifest living there would be
///         read from a tree other than the one the code came from and the two would drift. Embedding
///         it also keeps <c>demos/</c> a read-only directory for this layer, which the corpus rules
///         require.
///     </para>
///     <para>
///         <c>demos/CORPUS.md</c> remains the prose record. This file is the machine-readable one.
///     </para>
/// </summary>
public sealed class CorpusManifest
{
    private const string ResourceName = "DemoViewer.NET.TestSupport.corpus.manifest.json";

    private static readonly Lazy<CorpusManifest> _embedded = new(LoadEmbedded,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, CorpusDemo> _byFile;
    private readonly Dictionary<string, CorpusDemo> _byName;

    private CorpusManifest(IReadOnlyList<CorpusDemo> demos)
    {
        Demos = demos;
        _byFile = demos.ToDictionary(d => d.File, StringComparer.OrdinalIgnoreCase);
        _byName = demos.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The manifest embedded in this assembly.</summary>
    public static CorpusManifest Embedded => _embedded.Value;

    /// <summary>Every entry, in manifest order.</summary>
    public IReadOnlyList<CorpusDemo> Demos { get; }

    /// <summary>
    ///     Parses manifest JSON. Throws <see cref="InvalidDataException" /> on anything it cannot
    ///     use: a malformed manifest is a defect in the repository, never a reason to resolve
    ///     nothing and let a test pass.
    /// </summary>
    public static CorpusManifest Parse(string json)
    {
        ManifestFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ManifestFile>(json, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"demo corpus manifest is not valid JSON: {ex.Message}", ex);
        }

        if (file?.Demos is null)
        {
            throw new InvalidDataException("demo corpus manifest has no 'demos' array");
        }

        List<CorpusDemo> demos = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestEntry entry in file.Demos)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.File))
            {
                throw new InvalidDataException("demo corpus manifest has an entry with no 'name' or 'file'");
            }

            if (entry.Bytes <= 0)
            {
                throw new InvalidDataException($"demo corpus manifest entry '{entry.Name}' has no positive 'bytes'");
            }

            if (entry.Sha256 is not null && !IsHexSha256(entry.Sha256))
            {
                throw new InvalidDataException(
                    $"demo corpus manifest entry '{entry.Name}' has a 'sha256' that is not 64 hex characters");
            }

            if (!names.Add(entry.Name) || !files.Add(entry.File))
            {
                throw new InvalidDataException(
                    $"demo corpus manifest has a duplicate name or file at '{entry.Name}'");
            }

            demos.Add(new CorpusDemo(
                entry.Name,
                entry.File,
                entry.Sha256?.ToLowerInvariant(),
                entry.Bytes,
                string.IsNullOrWhiteSpace(entry.Dir) ? null : entry.Dir,
                entry.Sets ?? []));
        }

        return new CorpusManifest(demos);
    }

    /// <summary>The entry for a file name, or null when the manifest does not carry it.</summary>
    public CorpusDemo? ByFile(string fileName) =>
        _byFile.GetValueOrDefault(Path.GetFileName(fileName));

    /// <summary>The entry for a logical name, or null.</summary>
    public CorpusDemo? ByName(string logicalName) => _byName.GetValueOrDefault(logicalName);

    /// <summary>Every entry in a set, in manifest order. An unknown set is empty.</summary>
    public IReadOnlyList<CorpusDemo> Set(string set) =>
        [.. Demos.Where(d => d.Sets.Contains(set, StringComparer.OrdinalIgnoreCase))];

    private static bool IsHexSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static CorpusManifest LoadEmbedded()
    {
        Assembly assembly = typeof(CorpusManifest).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"embedded resource '{ResourceName}' is missing from {assembly.GetName().Name}. " +
                "It is declared as an EmbeddedResource in DemoViewer.NET.TestSupport.csproj.");
        }

        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class ManifestFile
    {
        [JsonPropertyName("demos")] public List<ManifestEntry>? Demos { get; init; }
    }

    private sealed class ManifestEntry
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("file")] public string? File { get; init; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
        [JsonPropertyName("bytes")] public long Bytes { get; init; }
        [JsonPropertyName("dir")] public string? Dir { get; init; }
        [JsonPropertyName("sets")] public List<string>? Sets { get; init; }
    }
}
