#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The history log's pure half (strat-model.md §3.8): applying ops, materializing a revision from the log,
///     and inverting an entry. The store reads and appends the lines; everything here is over values.
///     <para>
///         <b>Revision and modifiedUtc are the entry's, not the ops'.</b> An op never touches either field;
///         <see cref="Materialize" /> stamps them from the entry it stopped at, which is what the store writes
///         into the strat file at that commit. Keeping them out of the ops is what keeps a one-field edit a
///         one-op entry.
///     </para>
/// </summary>
public static class StratHistory
{
    /// <summary>
    ///     The document a revision describes: entries <c>1..revision</c> applied in order to nothing. Null when
    ///     the log does not reach that revision or does not start with a whole-document add.
    /// </summary>
    /// <param name="entries">The log, in file order.</param>
    /// <param name="revision">The revision to build.</param>
    public static StratDocument? Materialize(IReadOnlyList<HistoryEntry> entries, int revision)
    {
        ArgumentNullException.ThrowIfNull(entries);
        JsonNode? root = null;
        HistoryEntry? last = null;
        foreach (HistoryEntry entry in entries)
        {
            if (entry.Revision > revision)
            {
                break;
            }

            root = ApplyAll(root, entry.Ops);
            last = entry;
        }

        if (last is null || last.Revision != revision || root is not JsonObject)
        {
            return null;
        }

        StratDocument? document = root.Deserialize(StratJsonContext.Default.StratDocument);
        if (document is not null)
        {
            document.Revision = last.Revision;
            document.ModifiedUtc = last.AtUtc;
        }

        return document;
    }

    /// <summary>
    ///     Applies ops to a document and returns the result as a new document; the input is not touched. The
    ///     route every caller that edits by ops takes, so the file and the log cannot describe different edits.
    /// </summary>
    /// <param name="document">The document before.</param>
    /// <param name="ops">The ops, in order.</param>
    /// <exception cref="InvalidOperationException">An op's path does not resolve.</exception>
    public static StratDocument Apply(StratDocument document, IEnumerable<PatchOp> ops)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ops);
        JsonNode? root = ApplyAll(ToNode(document), ops);
        return root?.Deserialize(StratJsonContext.Default.StratDocument)
               ?? throw new InvalidOperationException("the ops removed the document");
    }

    /// <summary>The document as a JSON tree, through the file's own serializer.</summary>
    public static JsonNode ToNode(StratDocument document) =>
        JsonSerializer.SerializeToNode(document, StratJsonContext.Default.StratDocument)!;

    /// <summary>Applies ops in order and returns the new root, which differs from the input only for a root op.</summary>
    /// <param name="root">The tree; null before revision 1.</param>
    /// <param name="ops">The ops.</param>
    public static JsonNode? ApplyAll(JsonNode? root, IEnumerable<PatchOp> ops)
    {
        foreach (PatchOp op in ops)
        {
            root = ApplyOne(root, op);
        }

        return root;
    }

    private static JsonNode? ApplyOne(JsonNode? root, PatchOp op)
    {
        if (op.Path.Length == 0)
        {
            return op.Op switch
            {
                PatchOp.Add or PatchOp.Replace => op.Value?.DeepClone(),
                PatchOp.Remove => null,
                _ => throw new InvalidOperationException($"unsupported op '{op.Op}'")
            };
        }

        List<string> tokens = ParsePointer(op.Path);
        JsonNode parent = Walk(root, tokens, op.Path);
        string last = tokens[^1];
        JsonNode? value = op.Value?.DeepClone();

        switch (parent)
        {
            case JsonObject obj:
                switch (op.Op)
                {
                    case PatchOp.Add:
                    case PatchOp.Replace:
                        // Lenient on replace of an absent member: the files omit nulls, so a field whose
                        // old value was null is absent on disk and a replace is the natural op for it.
                        obj[last] = value;
                        break;
                    case PatchOp.Remove:
                        obj.Remove(last);
                        break;
                    default:
                        throw new InvalidOperationException($"unsupported op '{op.Op}'");
                }

                break;
            case JsonArray array:
                int count = array.Count;
                int index = last == "-" ? count : ParseIndex(last, op.Path);
                switch (op.Op)
                {
                    case PatchOp.Add when index <= count:
                        array.Insert(index, value);
                        break;
                    case PatchOp.Replace when index < count:
                        array[index] = value;
                        break;
                    case PatchOp.Remove when index < count:
                        array.RemoveAt(index);
                        break;
                    default:
                        throw new InvalidOperationException($"'{op.Op}' at {op.Path} is out of range");
                }

                break;
            default:
                throw new InvalidOperationException($"{op.Path} does not name a member of an object or array");
        }

        return root;
    }

    private static JsonNode Walk(JsonNode? root, List<string> tokens, string path)
    {
        JsonNode? node = root;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            node = node switch
            {
                JsonObject obj => obj[tokens[i]],
                JsonArray array => ParseIndex(tokens[i], path) is int index && index < array.Count ? array[index] : null,
                _ => null
            };
        }

        return node ?? throw new InvalidOperationException($"{path} does not resolve");
    }

    private static int ParseIndex(string token, string path) =>
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? index
            : throw new InvalidOperationException($"{path}: '{token}' is not an array index");

    /// <summary>RFC 6901: split on <c>/</c>, then <c>~1</c> to <c>/</c> and <c>~0</c> to <c>~</c>, in that order.</summary>
    private static List<string> ParsePointer(string pointer)
    {
        if (pointer[0] != '/')
        {
            throw new InvalidOperationException($"'{pointer}' is not a JSON pointer");
        }

        return [.. pointer[1..].Split('/').Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
    }

    /// <summary>A pointer token for a member name, escaped per RFC 6901.</summary>
    public static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>
///     The ops of one pending commit (§3.8 granularity, decision 2): consecutive ops on the same path merge, so
///     a drag that fired forty replaces is one op, and a commit falls due after
///     <see cref="IdleCommitSeconds" /> without an edit. The other commit triggers (an explicit Save, a tab
///     deactivate, a demo swap, shutdown) are the session's and simply call <see cref="Drain" />.
/// </summary>
public sealed class StratCommitBuffer
{
    /// <summary>Idle time after the last edit at which the pending ops become a history entry.</summary>
    public const double IdleCommitSeconds = 30;

    private readonly List<PatchOp> _ops = [];

    public bool HasPending => _ops.Count > 0;

    /// <summary>When the last op was recorded; null when nothing is pending.</summary>
    public DateTime? LastEditUtc { get; private set; }

    /// <summary>A snapshot of the pending ops, merged.</summary>
    public IReadOnlyList<PatchOp> Pending => [.. _ops.Select(o => o.Clone())];

    /// <summary>Adds one op, merging it into the previous one when both name the same path.</summary>
    /// <param name="op">The op as applied, <c>from</c> filled.</param>
    /// <param name="atUtc">When it was applied.</param>
    public void Record(PatchOp op, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(op);
        LastEditUtc = atUtc;
        PatchOp next = op.Clone();
        if (_ops.Count == 0 || !string.Equals(_ops[^1].Path, next.Path, StringComparison.Ordinal))
        {
            _ops.Add(next);
            return;
        }

        PatchOp previous = _ops[^1];
        PatchOp? merged = (previous.Op, next.Op) switch
        {
            // The displaced value is the first op's; the new value is the last one's.
            (PatchOp.Replace, PatchOp.Replace) => PatchOp.ReplaceOp(previous.Path, previous.From, next.Value),
            (PatchOp.Add, PatchOp.Replace) => PatchOp.AddOp(previous.Path, next.Value),
            (PatchOp.Replace, PatchOp.Remove) => PatchOp.RemoveOp(previous.Path, previous.From),
            (PatchOp.Remove, PatchOp.Add) => PatchOp.ReplaceOp(previous.Path, previous.From, next.Value),
            _ => null
        };

        if (merged is null && previous.Op == PatchOp.Add && next.Op == PatchOp.Remove)
        {
            // Added and removed inside one commit: the log never needs to know.
            _ops.RemoveAt(_ops.Count - 1);
            return;
        }

        if (merged is null)
        {
            _ops.Add(next);
            return;
        }

        _ops.RemoveAt(_ops.Count - 1);

        // A replace that ends where it started (a drag let go at its origin) is no change at all.
        if (merged.Op == PatchOp.Replace && JsonNode.DeepEquals(merged.From, merged.Value))
        {
            return;
        }

        _ops.Add(merged);
    }

    /// <summary>True when ops are pending and none arrived in the last <see cref="IdleCommitSeconds" />.</summary>
    /// <param name="nowUtc">The time now.</param>
    public bool IsIdleCommitDue(DateTime nowUtc) =>
        HasPending && LastEditUtc is { } last && (nowUtc - last).TotalSeconds >= IdleCommitSeconds;

    /// <summary>Takes the pending ops for a commit and empties the buffer.</summary>
    public IReadOnlyList<PatchOp> Drain()
    {
        List<PatchOp> ops = [.. _ops];
        _ops.Clear();
        LastEditUtc = null;
        return ops;
    }
}
