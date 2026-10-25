#region

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Google.Protobuf.Reflection;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>
///     Index of the Valve proto message, enum, field and enum-value names, each mapped to the
///     .proto file it is declared in. Built from the generated descriptors in CS2OpenDev.Protos,
///     so it needs no proto sources on disk. Backs the source links in the parse-chain panel and
///     the command palette's ".proto" lookup.
///     <para>
///         Descriptors carry no line numbers, so a link resolves to a file, not a declaration.
///     </para>
/// </summary>
public sealed class ProtoIndex
{
    /// <summary>Blob root for the .proto sources these descriptors were generated from.</summary>
    public const string GitHubBase = "https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/";

    // "{TypeName}.{field or enum value}" → .proto file name
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    // "{TypeName}" → .proto file name
    private readonly Dictionary<string, string> _types = new(StringComparer.Ordinal);

    /// <summary>The .proto file names the index covers, ordered.</summary>
    public IReadOnlyList<string> ProtoFiles { get; private init; } = [];

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>Build the index from the descriptors compiled into CS2OpenDev.Protos.</summary>
    public static ProtoIndex Build()
    {
        List<FileDescriptor> files = ProtoFileDescriptors();
        ProtoIndex idx = new()
        {
            ProtoFiles = files.Select(f => f.Name).ToList()
        };

        foreach (FileDescriptor file in files)
        {
            idx.AddFile(file);
        }

        return idx;
    }

    /// <summary>Blob URL for one .proto file name. File-level: descriptors carry no line.</summary>
    public static string SourceUrl(string protoFile) => GitHubBase + protoFile;

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>Blob URL for the file declaring <paramref name="field" /> on <paramref name="type" />.</summary>
    public string? FieldUrl(string? type, string field) =>
        TryGetField(type, field, out string? file) ? SourceUrl(file) : null;

    /// <summary>Blob URL for the file declaring <paramref name="type" />, or null when unknown.</summary>
    public string? TypeUrl(string? type) =>
        TryGetType(type, out string? file) ? SourceUrl(file) : null;

    /// <summary>
    ///     Case-insensitive substring search over indexed message and enum names. Backs the
    ///     command palette's ".proto" lookup. Exact-prefix matches rank ahead of mid-string
    ///     matches, then alphabetical.
    /// </summary>
    public IEnumerable<ProtoResult> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        List<ProtoResult> hits = new();
        foreach (KeyValuePair<string, string> kv in _types)
        {
            int idx = kv.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            hits.Add(new ProtoResult(
                kv.Key,
                kv.Value,
                SourceUrl(kv.Value),
                idx == 0 ? 0 : 1));
        }

        foreach (ProtoResult r in hits
                     .OrderBy(r => r.Rank)
                     .ThenBy(r => r.MessageName, StringComparer.Ordinal))
        {
            yield return r;
        }
    }

    /// <summary>The .proto file declaring <paramref name="field" /> on <paramref name="type" />.</summary>
    public bool TryGetField(string? type, string field, [NotNullWhen(true)] out string? protoFile)
    {
        if (type is null)
        {
            protoFile = null;
            return false;
        }

        return _fields.TryGetValue($"{type}.{field}", out protoFile);
    }

    /// <summary>The .proto file declaring the message or enum <paramref name="type" />.</summary>
    public bool TryGetType(string? type, [NotNullWhen(true)] out string? protoFile)
    {
        if (type is null)
        {
            protoFile = null;
            return false;
        }

        return _types.TryGetValue(type, out protoFile);
    }

    // ── Descriptor walk ───────────────────────────────────────────────────────

    /// <summary>
    ///     Every .proto in CS2OpenDev.Protos compiles to a generated holder class exporting a static
    ///     <see cref="FileDescriptor" /> property; message classes export a MessageDescriptor from the
    ///     same property name, so the property type is what separates them. Enumerating the assembly
    ///     rather than naming the holders keeps a file added upstream from being silently missed.
    ///     Ordered by file name so a name declared in two files resolves the same way every run.
    /// </summary>
    private static List<FileDescriptor> ProtoFileDescriptors()
    {
        List<FileDescriptor> files = [];

        Type[] types;
        try
        {
            types = typeof(CDemoPacket).Assembly.GetExportedTypes();
        }
        catch
        {
            // A failed type load degrades the links. It must not escape into the shell's constructor.
            return files;
        }

        foreach (Type t in types)
        {
            PropertyInfo? prop = t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
            if (prop?.PropertyType != typeof(FileDescriptor))
            {
                continue;
            }

            try
            {
                if (prop.GetValue(null) is FileDescriptor fd)
                {
                    files.Add(fd);
                }
            }
            catch
            {
                // One holder's static initializer throwing must not drop the rest.
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return files;
    }

    /// <summary>
    ///     Top-level messages and enums only. Nested types share short names across files (key_t and
    ///     friends) and would collide on the one key.
    /// </summary>
    private void AddFile(FileDescriptor file)
    {
        foreach (MessageDescriptor msg in file.MessageTypes)
        {
            _types.TryAdd(msg.Name, file.Name);
            foreach (FieldDescriptor f in msg.Fields.InDeclarationOrder())
            {
                _fields.TryAdd($"{msg.Name}.{f.Name}", file.Name);
            }
        }

        foreach (EnumDescriptor en in file.EnumTypes)
        {
            _types.TryAdd(en.Name, file.Name);
            foreach (EnumValueDescriptor v in en.Values)
            {
                _fields.TryAdd($"{en.Name}.{v.Name}", file.Name);
            }
        }
    }
}

/// <summary>
///     One command-palette ".proto" search hit. <paramref name="Rank" /> orders results
///     (0 = prefix match, 1 = mid-string).
/// </summary>
public readonly record struct ProtoResult(
    string MessageName,
    string ProtoFile,
    string Url,
    int Rank);
