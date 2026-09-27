#region

using System.IO.Compression;
using System.Text.Json;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Strats.Mining;

/// <summary>
///     A <see cref="RoundSignature" /> without its throws. Throws are attached on every mine from the Grenade Index,
///     because lineup ids are clustered across the whole library and move when any demo arrives.
/// </summary>
public sealed record CachedSignature(
    int Round,
    int Side,
    PatternKind Kind,
    string? Site,
    BuyType Buy,
    bool? Won,
    Guid? TeamId,
    int TickRate,
    int FreezeEndTick,
    int AnchorTick,
    IReadOnlyList<IReadOnlyList<MinedPawn>> Anchors,
    int ThrowFrom,
    int ThrowTo);

/// <summary>One demo's signatures as built from its record and positions file.</summary>
/// <param name="Sha256">The record's hash.</param>
/// <param name="Signatures">Every side, round and kind the demo produced; empty is a valid result.</param>
public sealed record DemoSignatures(string? Sha256, IReadOnlyList<CachedSignature> Signatures);

/// <summary>
///     Per-demo mining signatures, kept in memory and in <c>&lt;cache&gt;/strat-mining/signatures.json.gz</c>, so a
///     re-mine reads files only for demos whose inputs changed. An entry is valid only under the exact key it was
///     built with. Null path keeps it in memory. Not thread-safe: one mine at a time uses it.
/// </summary>
public sealed class SignatureCache(string? path)
{
    /// <summary>The file's shape version.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private bool _dirty;
    private Dictionary<string, Entry>? _entries;

    /// <summary>Demos held.</summary>
    public int Count => Entries.Count;

    private Dictionary<string, Entry> Entries => _entries ??= Read();

    /// <summary>The demo's signatures when they were built under <paramref name="key" />, else null.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="key">The key its inputs have now.</param>
    public DemoSignatures? TryGet(string demoPath, string key) =>
        Entries.TryGetValue(demoPath, out Entry? entry) && string.Equals(entry.Key, key, StringComparison.Ordinal)
            ? entry.Demo
            : null;

    /// <summary>Stores a demo's signatures under the key its inputs had when they were read.</summary>
    public void Put(string demoPath, string key, DemoSignatures demo)
    {
        Entries[demoPath] = new Entry(key, demo);
        _dirty = true;
    }

    /// <summary>Forgets every demo not in <paramref name="demoPaths" />.</summary>
    public void Retain(IReadOnlySet<string> demoPaths)
    {
        ArgumentNullException.ThrowIfNull(demoPaths);
        foreach (string gone in Entries.Keys.Where(p => !demoPaths.Contains(p)).ToList())
        {
            Entries.Remove(gone);
            _dirty = true;
        }
    }

    /// <summary>Writes the file when anything changed since it was read or last written.</summary>
    public void Save()
    {
        if (!_dirty || path is null)
        {
            _dirty = false;
            return;
        }

        try
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, $".signatures-{Guid.NewGuid():N}.tmp");
            using (FileStream stream = File.Create(temp))
            using (GZipStream gzip = new(stream, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, new CacheFile(SchemaVersion, Entries), JsonOptions);
            }

            File.Move(temp, path, true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept dirty: the next mine writes it again.
        }
    }

    private Dictionary<string, Entry> Read()
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using GZipStream gzip = new(stream, CompressionMode.Decompress);
                if (JsonSerializer.Deserialize<CacheFile>(gzip, JsonOptions) is { SchemaVersion: SchemaVersion, Demos: { } demos })
                {
                    return new Dictionary<string, Entry>(demos, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Unreadable: every demo is rebuilt and the file rewritten.
            }
        }

        return new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private sealed record Entry(string Key, DemoSignatures Demo);

    private sealed record CacheFile(int SchemaVersion, Dictionary<string, Entry> Demos);
}
