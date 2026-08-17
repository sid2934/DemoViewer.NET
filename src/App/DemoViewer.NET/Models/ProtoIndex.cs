#region

using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>
///     Lightweight index of all proto message/enum/field definitions found in a directory of .proto files.
///     Built once at startup by scanning the protobufs submodule; used for source-link generation in the
///     parse chain panel.
/// </summary>
public sealed class ProtoIndex
{
    // Matches plain enum values: FOO_BAR = 3;  (no leading type keyword)
    private static readonly Regex _enumVal = new(
        @"^\s*(\w+)\s*=\s*-?\d+\s*;",
        RegexOptions.Compiled);

    // Matches proto field declarations, e.g.:
    //   optional bytes entity_data = 10;
    //   repeated .Foo bar = 3;
    //   map<int32, string> entries = 5;
    private static readonly Regex _fieldDecl = new(
        @"(?:optional|repeated|required|map)\s+[\w.<>, ]+\s+(\w+)\s*=\s*\d+",
        RegexOptions.Compiled);

    // ── Scanner ───────────────────────────────────────────────────────────────

    // Matches:  message Foo {   or   enum Foo {
    private static readonly Regex _typeOpen = new(
        @"^\s*(message|enum)\s+(\w+)\s*\{?",
        RegexOptions.Compiled);

    // "{MessageName}.{field}"  → SourceLocation
    private readonly Dictionary<string, SourceLocation> _fields = new(StringComparer.Ordinal);

    // "{MessageName}"           → SourceLocation
    private readonly Dictionary<string, SourceLocation> _messages = new(StringComparer.Ordinal);

    /// <summary>Proto dir.</summary>
    public string ProtoDir { get; private init; } = "";

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>Build.</summary>
    public static ProtoIndex Build(string protoDir)
    {
        ProtoIndex idx = new()
        {
            ProtoDir = protoDir
        };
        if (!Directory.Exists(protoDir))
        {
            return idx;
        }

        foreach (string file in Directory.GetFiles(protoDir, "*.proto", SearchOption.TopDirectoryOnly))
        {
            idx.ScanFile(file);
        }

        return idx;
    }

    /// <summary>
    ///     Case-insensitive substring search over indexed message/enum names. Backs the
    ///     command palette's ".proto" lookup. Exact-prefix matches rank
    ///     ahead of mid-string matches, then alphabetical. <see cref="ProtoResult.LocalPath" />
    ///     resolves to the on-disk .proto under <see cref="ProtoDir" /> for VS Code links.
    /// </summary>
    public IEnumerable<ProtoResult> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        List<ProtoResult> hits = new();
        foreach (KeyValuePair<string, SourceLocation> kv in _messages)
        {
            int idx = kv.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            hits.Add(new ProtoResult(
                kv.Key,
                kv.Value.RelativeFile,
                Path.Combine(ProtoDir, kv.Value.RelativeFile),
                kv.Value.Line,
                idx == 0 ? 0 : 1));
        }

        foreach (ProtoResult r in hits
                     .OrderBy(r => r.Rank)
                     .ThenBy(r => r.MessageName, StringComparer.Ordinal))
        {
            yield return r;
        }
    }

    /// <summary>Try get field.</summary>
    public bool TryGetField(string message, string field, out SourceLocation loc) =>
        _fields.TryGetValue($"{message}.{field}", out loc);

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>Try get message.</summary>
    public bool TryGetMessage(string name, out SourceLocation loc) =>
        _messages.TryGetValue(name, out loc);

    private static int CountChar(string s, char c)
    {
        int n = 0;
        foreach (char ch in s)
        {
            if (ch == c)
            {
                n++;
            }
        }

        return n;
    }

    private void ScanFile(string absPath)
    {
        string relPath = Path.GetFileName(absPath);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(absPath);
        }
        catch
        {
            return;
        }

        string? currentType = null;
        int braceDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;

            // Strip inline comments
            string line = lines[i];
            int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            // Count brace changes on this line BEFORE matching, so we know the pre-line depth
            int opens = CountChar(line, '{');
            int closes = CountChar(line, '}');

            // Check for message/enum declaration at current top-level scope (braceDepth == 0)
            Match mType = _typeOpen.Match(line);
            if (mType.Success)
            {
                string typeName = mType.Groups[2].Value;

                // Only index top-level types (braceDepth == 0 before this line's open brace)
                if (braceDepth == 0)
                {
                    currentType = typeName;
                    _messages[typeName] = new SourceLocation(relPath, lineNum);
                }
            }

            braceDepth += opens - closes;
            if (braceDepth < 0)
            {
                braceDepth = 0;
            }

            // Reset when we exit the top-level type block
            if (braceDepth == 0 && currentType != null && closes > 0)
            {
                currentType = null;
            }

            // Index field declarations inside the top-level type (braceDepth == 1)
            if (currentType != null && braceDepth == 1)
            {
                Match mField = _fieldDecl.Match(line);
                if (mField.Success)
                {
                    string fieldName = mField.Groups[1].Value;
                    _fields[$"{currentType}.{fieldName}"] = new SourceLocation(relPath, lineNum);
                    continue;
                }

                // Enum values (e.g. DEM_Packet = 4;)
                Match mEv = _enumVal.Match(line);
                if (mEv.Success)
                {
                    string valName = mEv.Groups[1].Value;
                    _fields[$"{currentType}.{valName}"] = new SourceLocation(relPath, lineNum);
                }
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
    string RelativeFilePath,
    string LocalPath,
    int Line,
    int Rank);
