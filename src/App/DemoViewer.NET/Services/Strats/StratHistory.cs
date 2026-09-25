#region

using System.Globalization;
using System.Text;
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

    /// <summary>
    ///     The value a pointer names, or null when it names nothing: what an op displaces, read before it is
    ///     applied so the session can fill <c>from</c> itself rather than trust the caller's.
    /// </summary>
    /// <param name="root">The tree.</param>
    /// <param name="path">An RFC 6901 pointer; <c>""</c> is the root.</param>
    public static JsonNode? ValueAt(JsonNode? root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return root;
        }

        JsonNode? node = root;
        foreach (string token in ParsePointer(path))
        {
            node = node switch
            {
                JsonObject obj => obj[token],
                JsonArray array => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < array.Count
                    ? array[index]
                    : null,
                _ => null
            };

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>
    ///     Ops that turn <paramref name="before" /> into <paramref name="after" />, <c>from</c> filled: members by
    ///     name, arrays element by element while their lengths agree and whole otherwise. Used where only the two
    ///     states are known, the uncommitted edits a crash left in a <c>pending</c> file.
    /// </summary>
    /// <param name="before">The tree before.</param>
    /// <param name="after">The tree after.</param>
    public static List<PatchOp> Diff(JsonNode? before, JsonNode? after)
    {
        List<PatchOp> ops = [];
        DiffInto(before, after, "", ops);
        return ops;
    }

    private static void DiffInto(JsonNode? before, JsonNode? after, string path, List<PatchOp> ops)
    {
        switch (before, after)
        {
            case (JsonObject a, JsonObject b):
                foreach ((string key, JsonNode? value) in a)
                {
                    if (!b.ContainsKey(key))
                    {
                        ops.Add(PatchOp.RemoveOp(path + "/" + Escape(key), value?.DeepClone()));
                    }
                }

                foreach ((string key, JsonNode? value) in b)
                {
                    string child = path + "/" + Escape(key);
                    if (a.TryGetPropertyValue(key, out JsonNode? old))
                    {
                        DiffInto(old, value, child, ops);
                    }
                    else
                    {
                        ops.Add(PatchOp.AddOp(child, value?.DeepClone()));
                    }
                }

                return;
            case (JsonArray a, JsonArray b) when a.Count == b.Count:
                for (int i = 0; i < a.Count; i++)
                {
                    DiffInto(a[i], b[i], path + "/" + i.ToString(CultureInfo.InvariantCulture), ops);
                }

                return;
            default:
                if (!JsonNode.DeepEquals(before, after))
                {
                    ops.Add(PatchOp.ReplaceOp(path, before?.DeepClone(), after?.DeepClone()));
                }

                return;
        }
    }
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

/// <summary>
///     A history entry's words (strat-model.md §3.8): "molotov moved from 1:22 to 1:16", "step added: A peeks
///     Connector at 1:05", "branch removed", "status Active → Archived". Free text for a person; the ops stay the
///     truth. Each op is read against the document as it stood just before it, so a step is named by what it
///     was then, and places print through the owner's callouts when a resolver is given.
/// </summary>
public static class StratDiffPhrasing
{
    // Fields whose value is prose: printing it in a one-line summary says nothing the pane's diff does not.
    private static readonly HashSet<string> TextFields = new(StringComparer.Ordinal) { "notes", "note", "text" };

    /// <summary>One line per op, a repeated line said once, in op order.</summary>
    /// <param name="before">The document before the first op; null before revision 1.</param>
    /// <param name="ops">The entry's ops.</param>
    /// <param name="callouts">The owner's callouts for the map, for place names; null prints canonical names split into words.</param>
    public static IReadOnlyList<string> Describe(JsonNode? before, IEnumerable<PatchOp> ops, CalloutResolver? callouts = null)
    {
        ArgumentNullException.ThrowIfNull(ops);
        JsonNode? tree = before?.DeepClone();
        List<string> lines = [];
        foreach (PatchOp op in ops)
        {
            string line = DescribeOne(tree, op, callouts);
            if (!lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }

            try
            {
                tree = StratHistory.ApplyAll(tree, [op]);
            }
            catch (InvalidOperationException)
            {
                // A log that does not apply still gets words for the rest, just without the context.
                tree = null;
            }
        }

        return lines;
    }

    /// <summary>The lines joined as a history summary: <c>branch added; status Theory → Active</c>.</summary>
    /// <param name="before">The document before the first op; null before revision 1.</param>
    /// <param name="ops">The entry's ops.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    public static string Summary(JsonNode? before, IEnumerable<PatchOp> ops, CalloutResolver? callouts = null) =>
        string.Join("; ", Describe(before, ops, callouts));

    /// <summary>
    ///     The summary of ops already applied to <paramref name="after" />: the state before is recovered by
    ///     inverting them, which is what <c>from</c> is for. What the store calls when a commit brings no summary.
    /// </summary>
    /// <param name="after">The document after the ops.</param>
    /// <param name="ops">The ops, in the order they were applied.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    public static string SummaryAfter(StratDocument after, IReadOnlyList<PatchOp> ops, CalloutResolver? callouts = null)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(ops);
        JsonNode? before;
        try
        {
            before = StratHistory.ApplyAll(StratHistory.ToNode(after), ops.Reverse().Select(o => o.Inverse()));
        }
        catch (InvalidOperationException)
        {
            before = null;
        }

        return Summary(before, ops, callouts);
    }

    private static string DescribeOne(JsonNode? tree, PatchOp op, CalloutResolver? callouts)
    {
        if (op.Path.Length == 0)
        {
            return op.Op == PatchOp.Remove ? "deleted" : "created";
        }

        string[] tokens =
        [
            .. op.Path[1..].Split('/')
                .Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))
        ];

        switch (tokens)
        {
            case ["steps", _]:
                JsonNode? step = op.Op == PatchOp.Add ? op.Value : StratHistory.ValueAt(tree, op.Path) ?? op.From;
                return op.Op switch
                {
                    PatchOp.Add => "step added: ",
                    PatchOp.Remove => "step removed: ",
                    _ => "step replaced: "
                } + StepSentence(op.Op == PatchOp.Replace ? op.Value : step, callouts);
            case ["steps", var index, "atSeconds"] when op.Op == PatchOp.Replace && Number(op.From) is { } from && Number(op.Value) is { } to:
                return $"{StepName(StratHistory.ValueAt(tree, "/steps/" + index))} moved from {StratClock.Format(from)} to {StratClock.Format(to)}";
            case ["steps", var index, .. var rest]:
                return StepName(StratHistory.ValueAt(tree, "/steps/" + index)) + ": " + FieldChange(rest, op, callouts);
            case ["branches", _]:
                return op.Op switch
                {
                    PatchOp.Add => "branch added",
                    PatchOp.Remove => "branch removed",
                    _ => "branch replaced"
                };
            case ["branches", _, .. var rest]:
                return "branch: " + FieldChange(rest, op, callouts);
            case ["slots", var index, .. var rest] when rest.Length > 0:
                string letter = Text(StratHistory.ValueAt(tree, "/slots/" + index + "/slot")) ?? index;
                if (rest is ["steamId"])
                {
                    return op.Op == PatchOp.Remove || op.Value is null ? $"slot {letter} unpinned" : $"slot {letter} pinned";
                }

                return $"slot {letter} " + FieldChange(rest, op, callouts);
            case ["tags", _] when op.Op is PatchOp.Add or PatchOp.Remove:
                string? tag = Text(op.Op == PatchOp.Add ? op.Value : op.From ?? StratHistory.ValueAt(tree, op.Path));
                return $"tag {(op.Op == PatchOp.Add ? "added" : "removed")}" + (tag is null ? "" : ": " + tag);
            case ["name"] when op.Op != PatchOp.Remove && Text(op.Value) is { } name:
                return "renamed to " + name;
            default:
                return FieldChange(tokens, op, callouts);
        }
    }

    // "economy full → force", "target site cleared", "note removed", "landing set to Jungle".
    private static string FieldChange(IReadOnlyList<string> tokens, PatchOp op, CalloutResolver? callouts)
    {
        // A place is an object with one field that matters; "to" reads better than "to place".
        List<string> named = [.. tokens.Where(t => !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out _))];
        bool isPlace = named.Count > 1 && named[^1] == "place";
        if (isPlace)
        {
            named.RemoveAt(named.Count - 1);
        }

        string field = named.Count == 0 ? "value" : string.Join(' ', named.Select(Words));
        bool prose = named.Count > 0 && TextFields.Contains(named[^1]);
        string? from = prose ? null : isPlace && Text(op.From) is { } fromPlace ? Place(fromPlace, callouts) : ValueText(op.From, callouts);
        string? to = prose ? null : isPlace && Text(op.Value) is { } toPlace ? Place(toPlace, callouts) : ValueText(op.Value, callouts);

        return op.Op switch
        {
            PatchOp.Remove => field + " removed",
            PatchOp.Add when prose || to is null => field + " added",
            PatchOp.Add => $"{field} set to {to}",
            _ when op.Value is null => field + " cleared",
            _ when prose => field + " edited",
            _ when op.From is null && to is not null => $"{field} set to {to}",
            _ when from is not null && to is not null => $"{field} {from} → {to}",
            _ => field + " changed"
        };
    }

    // Scalars print; a place object prints its place; anything else (a position, a stroke) is just "changed".
    private static string? ValueText(JsonNode? value, CalloutResolver? callouts)
    {
        if (value is JsonObject obj)
        {
            return obj.Count == 1 && Text(obj["place"]) is { } inner ? Place(inner, callouts) : null;
        }

        if (Number(value) is { } number)
        {
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        }

        string? text = Text(value);
        return text is not null && callouts?.IsCanonical(text) == true ? Place(text, callouts) : text;
    }

    // What a line about a step calls it: its utility when it has one ("molotov"), else whose move it is.
    private static string StepName(JsonNode? step)
    {
        if (step is not JsonObject obj)
        {
            return "a step";
        }

        if (Text(obj["utility"]?["kind"]) is { } kind)
        {
            return kind;
        }

        string actor = Text(obj["actor"]) ?? StratVocabulary.ActorAll;
        string verb = Text(obj["verb"]) ?? "move";
        return actor == StratVocabulary.ActorAll ? "the team's " + verb : $"{actor}'s {verb}";
    }

    // "A peeks Connector at 1:05", "C throws molotov to Jungle at 1:22", "all rotate to Bombsite B at 0:40".
    private static string StepSentence(JsonNode? step, CalloutResolver? callouts)
    {
        if (step is not JsonObject obj)
        {
            return "a step";
        }

        string actor = Text(obj["actor"]) ?? StratVocabulary.ActorAll;
        string verb = Text(obj["verb"]) ?? "move";
        string? utility = Text(obj["utility"]?["kind"]);
        string? target = Text(obj["utility"]?["landing"]?["place"]) ?? Text(obj["to"]?["place"]) ?? Text(obj["from"]?["place"]);

        StringBuilder sentence = new(actor);
        sentence.Append(' ').Append(actor == StratVocabulary.ActorAll ? verb : ThirdPerson(verb));
        if (utility is not null)
        {
            sentence.Append(' ').Append(utility);
        }

        if (target is not null)
        {
            string preposition = utility is not null
                ? " to "
                : verb switch
                {
                    "move" or "rotate" or "throw" => " to ",
                    "wait" or "call" or "other" => " at ",
                    _ => " "
                };
            sentence.Append(preposition).Append(Place(target, callouts));
        }

        if (Number(obj["atSeconds"]) is { } at)
        {
            sentence.Append(" at ").Append(StratClock.Format(at));
        }

        return sentence.ToString();
    }

    // Internal rather than private: RoleSheet and StratTextExporter phrase steps the same way (§3.13,
    // §3.14) and share these two rather than growing their own copies.
    internal static string ThirdPerson(string verb) => verb switch
    {
        "other" => "acts",
        _ when verb.EndsWith('s') || verb.EndsWith('x') || verb.EndsWith("sh", StringComparison.Ordinal)
               || verb.EndsWith("ch", StringComparison.Ordinal) => verb + "es",
        _ => verb + "s"
    };

    internal static string Place(string place, CalloutResolver? callouts) =>
        callouts?.Display(place) ?? CalloutResolver.SplitDisplay(place);

    // targetSite to "target site", holdSeconds to "hold seconds".
    private static string Words(string camel)
    {
        StringBuilder words = new();
        foreach (char c in camel)
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(c));
        }

        return words.ToString();
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // Through the JSON text: a value built in code holds an int that GetValue<double> refuses, one read from a
    // file holds a JsonElement that accepts it.
    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
}

/// <summary>
///     One line of the history pane (Strat Version History): an entry's revision, time, summary and per-op
///     words, and the record either side of it when a record is at hand (§3.8: runs tagged below this revision
///     against runs tagged at it or later).
/// </summary>
/// <param name="Revision">The entry's revision.</param>
/// <param name="AtUtc">When it was committed.</param>
/// <param name="Summary">The stored summary, or the phrased one when the entry has none.</param>
/// <param name="Changes">One line per op, phrased against the document as it stood before the entry.</param>
/// <param name="Before">Runs tagged at an earlier revision; null without a record.</param>
/// <param name="After">Runs tagged at this revision or later; null without a record.</param>
public sealed record StratHistoryRow(
    int Revision,
    DateTime AtUtc,
    string Summary,
    IReadOnlyList<string> Changes,
    RecordSplit? Before,
    RecordSplit? After);

/// <summary>The history pane's data: one row per log entry, newest first.</summary>
public static class StratHistoryPane
{
    /// <summary>
    ///     The rows for a log. The log is replayed once, so each entry is phrased against its own before-state
    ///     without materializing every revision separately.
    /// </summary>
    /// <param name="log">The history, in file order.</param>
    /// <param name="callouts">The owner's callouts for the strat's map; null for canonical names.</param>
    /// <param name="record">The strat's record, for the split either side of each entry; null leaves the splits out.</param>
    public static IReadOnlyList<StratHistoryRow> Rows(IReadOnlyList<HistoryEntry> log, CalloutResolver? callouts = null,
        StratRecord? record = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<StratHistoryRow> rows = [];
        JsonNode? tree = null;
        foreach (HistoryEntry entry in log)
        {
            IReadOnlyList<string> changes = StratDiffPhrasing.Describe(tree, entry.Ops, callouts);
            try
            {
                tree = StratHistory.ApplyAll(tree, entry.Ops);
            }
            catch (InvalidOperationException)
            {
                tree = null;
            }

            (RecordSplit Before, RecordSplit After)? split = record?.SplitAround(entry.Revision);
            string summary = string.IsNullOrWhiteSpace(entry.Summary) ? string.Join("; ", changes) : entry.Summary;
            rows.Add(new StratHistoryRow(entry.Revision, entry.AtUtc, summary, changes, split?.Before, split?.After));
        }

        rows.Reverse();
        return rows;
    }
}
