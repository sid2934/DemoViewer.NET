#region

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The Strat Book's persisted store (strat-model.md §3.2, §3.8, §3.11): <c>&lt;config&gt;/strats/index.json</c>
///     plus one folder per owner (<c>team-&lt;guid&gt;</c> or <c>me</c>) holding <c>book.json</c> and one folder per
///     map with <c>callouts.json</c>, each strat's <c>&lt;id&gt;.dvstrat.json</c> and its
///     <c>&lt;id&gt;.history.jsonl</c>.
///     <para>
///         <b>User truth, not a cache.</b> The <c>TagStore</c> rules: atomic writes, a failed write reported
///         rather than swallowed, an unreadable file read as absent and never written over by a fresh one.
///     </para>
///     <para>
///         <b>History first, strat second.</b> A commit appends one line to the log and then replaces the strat
///         file. The log is never rewritten. A crash between the two leaves the log ahead of the file, and the
///         next load re-applies the missing entries and rewrites the file, so nothing committed is lost.
///     </para>
///     <para>
///         <b>The index is derived.</b> Rebuilt from the folders when missing or corrupt, and reconciled against
///         the listing at start, the <c>TagStore</c> rule.
///     </para>
///     <para>
///         <b>Browser.</b> A null root keeps strats, logs, books and callouts in dictionaries for the session,
///         the <c>DemoCacheStore._memoryRecords</c> rule. Nothing survives a reload, which the tab says.
///     </para>
/// </summary>
public sealed class StratStore
{
    /// <summary>The schema version this build writes on every Strat Book file.</summary>
    public const int SchemaVersion = 1;

    public const string StratExtension = ".dvstrat.json";
    public const string HistoryExtension = ".history.jsonl";
    public const string BookFileName = "book.json";
    public const string CalloutsFileName = "callouts.json";

    /// <summary>Where <see cref="Delete" /> moves a strat's files, inside its map folder (decision 10).</summary>
    public const string TrashFolderName = ".trash";

    /// <summary>
    ///     The extension-bag field an autosaved working copy carries (§3.12): the file holds edits not yet
    ///     committed, at the previous revision, and the next session re-opens them uncommitted.
    /// </summary>
    public const string PendingField = "pending";

    // Which session holds each strat: the single writer while the Strat Book has it open. Under _gate.
    private readonly Dictionary<Guid, StratSession> _checkedOut = [];

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, StratIndexEntry> _index = [];

    // Where each strat's file is on disk, from the listing. Under _gate.
    private readonly Dictionary<Guid, string> _paths = [];

    // The browser's (and tests') files, as JSON text so no reader shares a live object with a writer.
    // Keyed by id for strats and logs, by owner folder for books, by "owner/map" for callouts. Under _gate.
    private readonly Dictionary<Guid, string> _memoryStrats = [];
    private readonly Dictionary<Guid, List<string>> _memoryHistory = [];
    private readonly Dictionary<string, string> _memoryBooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _memoryCallouts = new(StringComparer.Ordinal);

    private readonly Action<Action> _post;
    private readonly Func<DateTime> _utcNow;

    // Serializes every read-modify-write: a commit, a status change, a delete, a reconcile.
    private readonly Lock _rmwGate = new();

    private readonly string? _root;

    /// <param name="stratsRoot"><c>&lt;config&gt;/strats</c>, or null for an in-memory store (the browser host, tests).</param>
    /// <param name="post">Marshals <see cref="Changed" /> onto the UI thread; defaults to synchronous.</param>
    /// <param name="utcNow">The clock commits are stamped with; tests pin it so the goldens are byte-stable.</param>
    public StratStore(string? stratsRoot, Action<Action>? post = null, Func<DateTime>? utcNow = null)
    {
        _root = string.IsNullOrWhiteSpace(stratsRoot) ? null : stratsRoot;
        _post = post ?? (action => action());
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        LoadIndex();
    }

    /// <summary>False on the browser host and in tests without a root: nothing survives the process.</summary>
    public bool IsPersistent => _root is not null;

    /// <summary>A point-in-time snapshot of every index row, safe to enumerate off-lock.</summary>
    public IReadOnlyList<StratIndexEntry> Index
    {
        get
        {
            lock (_gate)
            {
                return [.. _index.Values.OrderBy(e => e.Id)];
            }
        }
    }

    private string? IndexPath => _root is null ? null : Path.Combine(_root, "index.json");

    /// <summary>Raised through the post delegate after a commit or a delete with the strat's id, or null after a rebuild.</summary>
    public event Action<Guid?>? Changed;

    /// <summary>A book's map folder under a strats root.</summary>
    /// <param name="stratsRoot">The strats root.</param>
    /// <param name="owner">The book's owner.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    public static string FolderFor(string stratsRoot, StratOwner owner, string map)
    {
        ArgumentException.ThrowIfNullOrEmpty(stratsRoot);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(map);
        return Path.Combine(stratsRoot, owner.FolderName, map.ToLowerInvariant());
    }

    /// <summary>The bytes this store writes for a strat. Public so a golden can compare against them.</summary>
    public static string Serialize(StratDocument document) =>
        JsonSerializer.Serialize(document, StratJsonContext.Default.StratDocument);

    /// <summary>A strat, or null when it is absent or unreadable.</summary>
    /// <param name="id">The strat's id.</param>
    public StratDocument? TryLoad(Guid id) => Load(id).Document;

    /// <summary>
    ///     Loads a strat with its validation issues, reconciling it against its log first. Never throws for a
    ///     missing, truncated or hand-broken file.
    /// </summary>
    /// <param name="id">The strat's id.</param>
    public StratLoadResult Load(Guid id)
    {
        lock (_rmwGate)
        {
            string? json = ReadStratText(id, out string? path, out bool ioFailed);
            if (json is null)
            {
                return ioFailed ? StratLoadResult.Unreadable(id, path) : new StratLoadResult(null, path, false, []);
            }

            StratDocument? document = Parse(json);
            if (document is null || document.Id != id)
            {
                return StratLoadResult.Unreadable(id, path);
            }

            document = Reconcile(document, path);
            return new StratLoadResult(document, path, false, StratValidator.Validate(document, index: Index));
        }
    }

    /// <summary>A new strat in a book, committed as revision 1. Revision 0 on the result means the write failed.</summary>
    /// <param name="owner">The book.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    /// <param name="side"><c>T</c> or <c>CT</c>.</param>
    /// <param name="type">The strat type.</param>
    /// <param name="name">The display name.</param>
    public StratDocument Create(StratOwner owner, string map, string side, string type, string name)
    {
        StratDocument document = StratDocument.Create(Guid.NewGuid(), owner, map, side, type, name, _utcNow());
        Save(document, [], "created");
        return document;
    }

    /// <summary>
    ///     Commits a strat: validates it, appends one history line with <paramref name="ops" />, then replaces the
    ///     strat file atomically, and bumps <see cref="StratDocument.Revision" /> and <see cref="StratDocument.ModifiedUtc" />
    ///     on <paramref name="document" /> to match. A strat the store has never seen is revision 1, whose log line
    ///     is the whole document whatever <paramref name="ops" /> says. Empty ops on a known strat commit nothing.
    ///     Never throws for I/O.
    /// </summary>
    /// <param name="document">The strat after the edits.</param>
    /// <param name="ops">The edits since the last commit, <c>from</c> filled.</param>
    /// <param name="summary">The history line's summary; free text, the ops are the truth.</param>
    public StratSaveResult Save(StratDocument document, IReadOnlyList<PatchOp> ops, string? summary)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ops);

        IReadOnlyList<StratIssue> issues = StratValidator.Validate(document, index: Index);
        if (issues.FirstOrDefault(i => i.Severity == StratIssueSeverity.Refusal) is { } refusal)
        {
            return StratSaveResult.Failed("refused: " + refusal.Message, issues);
        }

        if (FolderProblem(document) is { } problem)
        {
            return StratSaveResult.Failed(problem, issues);
        }

        lock (_rmwGate)
        {
            StratIndexEntry? known;
            string? oldPath;
            lock (_gate)
            {
                known = _index.GetValueOrDefault(document.Id);
                oldPath = _paths.GetValueOrDefault(document.Id);
            }

            bool unreadableOnDisk = false;
            if (known is null)
            {
                unreadableOnDisk = ReadStratText(document.Id, out _, out bool ioFailed) is not null || ioFailed;
            }

            if (unreadableOnDisk)
            {
                // A file exists that the index does not describe because it could not be read. Writing a
                // revision 1 over it would destroy a hand-edited strat; the store declines instead.
                return StratSaveResult.Failed("an unreadable strat file already has this id", issues);
            }

            if (known is not null && ops.Count == 0)
            {
                return new StratSaveResult(true, null, known.Revision, issues);
            }

            DateTime now = _utcNow();
            // A committed file never carries the working-copy marker, whoever saved it.
            StratDocument committed = WithPending(document, false);
            committed.Revision = (known?.Revision ?? 0) + 1;
            committed.ModifiedUtc = now;
            committed.Owner = committed.Owner.Clone();
            committed.Map = committed.Map.ToLowerInvariant();

            // A commit that brings no words (the session's idle, deactivate and shutdown commits) gets them from
            // its ops, so every line in the log reads as something in the history pane.
            summary ??= known is null ? "created" : PhraseSummary(committed, ops);

            HistoryEntry entry = new()
            {
                Revision = committed.Revision,
                AtUtc = now,
                Summary = summary,
                Ops = known is null ? [PatchOp.AddOp("", StratHistory.ToNode(committed))] : [.. ops.Select(o => o.Clone())]
            };

            string? newPath = _root is null ? null : StratPath(committed);
            string? moveFailure = MoveIfRelocated(document.Id, oldPath, newPath);
            if (moveFailure is not null)
            {
                return StratSaveResult.Failed(moveFailure, issues);
            }

            if (!AppendHistory(committed, entry))
            {
                return StratSaveResult.Failed("the history line could not be written", issues);
            }

            // The log now has the commit, so a failure below is recovered on the next load.
            if (!WriteStrat(committed, newPath))
            {
                return StratSaveResult.Failed("the strat file could not be written; the next load recovers it from history", issues);
            }

            document.Revision = committed.Revision;
            document.ModifiedUtc = now;
            document.Map = committed.Map;
        }

        RaiseChanged(document.Id);
        return new StratSaveResult(true, null, document.Revision, issues);
    }

    // The owner's callouts over the embedded list: display needs only the primary aliases, and the store has no
    // zones to hand. Phrasing is a nicety, so a failure leaves the summary empty rather than failing the commit.
    private string? PhraseSummary(StratDocument committed, IReadOnlyList<PatchOp> ops)
    {
        try
        {
            CalloutResolver callouts = CalloutResolver.For(committed.Map, null, LoadCallouts(committed.Owner, committed.Map));
            return StratDiffPhrasing.SummaryAfter(committed, ops, callouts);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A status change as a one-op commit. False when the strat is absent or the commit failed.</summary>
    /// <param name="id">The strat's id.</param>
    /// <param name="status">The new status.</param>
    public bool SetStatus(Guid id, StratStatus status)
    {
        // While the Strat Book has the strat open its session is the single writer: the change becomes one of
        // its edits, undoable and committed by its rule, the TagStore.Update routing.
        if (SessionFor(id) is { } holder)
        {
            _post(() => holder.SetStatus(status));
            return true;
        }

        lock (_rmwGate)
        {
            if (TryLoad(id) is not { } loaded)
            {
                return false;
            }

            string value = status.ToString();
            if (string.Equals(loaded.Status, value, StringComparison.Ordinal))
            {
                return true;
            }

            // A working copy nobody has open: its edits go into this commit with the status, or the file would
            // hold them unmarked with no line in the log.
            List<PatchOp> ops = [.. PendingOps(loaded)];
            StratDocument document = WithPending(loaded, false);
            PatchOp op = PatchOp.ReplaceOp("/status", JsonValue.Create(document.Status), JsonValue.Create(value));
            document.Status = value;
            ops.Add(op);
            return Save(document, ops, $"status {op.From} → {value}").Saved;
        }
    }

    /// <summary>
    ///     Moves a strat's file and log to <c>&lt;owner&gt;/&lt;map&gt;/.trash/</c> and drops its index row. Never a
    ///     hard delete on disk (decision 10); in memory the strat is simply forgotten.
    /// </summary>
    /// <param name="id">The strat's id.</param>
    public bool Delete(Guid id)
    {
        lock (_rmwGate)
        {
            bool removed;
            string? path;
            lock (_gate)
            {
                removed = _index.Remove(id) | _memoryStrats.Remove(id) | _memoryHistory.Remove(id);
                path = _paths.GetValueOrDefault(id);
            }

            if (path is not null)
            {
                string folder = Path.GetDirectoryName(path)!;
                string trash = Path.Combine(folder, TrashFolderName);
                string stamp = _utcNow().ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    Directory.CreateDirectory(trash);
                    foreach (string file in new[] { path, HistoryPathBeside(path) }.Where(File.Exists))
                    {
                        string target = Path.Combine(trash, Path.GetFileName(file));

                        // A strat deleted twice (restored by hand, deleted again) keeps both copies.
                        if (File.Exists(target))
                        {
                            target = Path.Combine(trash, stamp + "." + Path.GetFileName(file));
                        }

                        File.Move(file, target);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                lock (_gate)
                {
                    _paths.Remove(id);
                }

                removed = true;
            }

            if (!removed)
            {
                return false;
            }
        }

        RaiseChanged(id);
        return true;
    }

    /// <summary>
    ///     Writes a session's working copy over a committed strat without committing it (§3.12): no history line,
    ///     no revision bump, no validation refusal, because a strat mid-edit may be briefly invalid and a crash
    ///     must still find the edits. <paramref name="pending" /> stamps <see cref="PendingField" />; false writes
    ///     the committed state back without it. False when the strat was never committed or the write failed.
    /// </summary>
    /// <param name="document">The session's document, at its last committed revision.</param>
    /// <param name="pending">Whether it holds edits the log does not have yet.</param>
    public bool WriteWorkingCopy(StratDocument document, bool pending)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (FolderProblem(document) is not null)
        {
            return false;
        }

        lock (_rmwGate)
        {
            StratIndexEntry? known;
            string? path;
            lock (_gate)
            {
                known = _index.GetValueOrDefault(document.Id);
                path = _paths.GetValueOrDefault(document.Id);
            }

            if (known is null)
            {
                return false;
            }

            // The committed file's own place: an owner or map edit moves the strat at commit, with its log.
            StratDocument copy = WithPending(document, pending);
            copy.Revision = known.Revision;
            if (!WriteStrat(copy, _root is null ? null : path ?? StratPath(copy)))
            {
                return false;
            }
        }

        RaiseChanged(document.Id);
        return true;
    }

    /// <summary>Whether a strat file is an autosaved working copy with uncommitted edits.</summary>
    /// <param name="document">A loaded strat.</param>
    public static bool IsPending(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Extra?.TryGetValue(PendingField, out JsonElement value) == true && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    ///     The uncommitted edits in a working copy, as ops from its committed revision (rebuilt from the log) to
    ///     the file, <c>from</c> filled. Empty for a committed file. A log that cannot rebuild the revision gives
    ///     one whole-document replace, so the edits still reach the next commit.
    /// </summary>
    /// <param name="document">A loaded strat.</param>
    public IReadOnlyList<PatchOp> PendingOps(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsPending(document))
        {
            return [];
        }

        StratDocument working = WithPending(document, false);
        StratDocument? committed;
        try
        {
            committed = Materialize(document.Id, document.Revision);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            committed = null;
        }

        JsonNode after = StratHistory.ToNode(working);
        if (committed is null)
        {
            return [PatchOp.ReplaceOp("", null, after)];
        }

        // Revision and modifiedUtc are the entry's, never an op's (StratHistory's rule).
        committed.ModifiedUtc = working.ModifiedUtc;
        return StratHistory.Diff(StratHistory.ToNode(committed), after);
    }

    /// <summary>A copy with the <see cref="PendingField" /> marker set or cleared; the input is not touched.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="pending">Whether to mark it.</param>
    public static StratDocument WithPending(StratDocument document, bool pending)
    {
        ArgumentNullException.ThrowIfNull(document);
        StratDocument copy = document.Clone();
        copy.Extra?.Remove(PendingField);
        if (pending)
        {
            copy.Extra ??= [];
            using JsonDocument marker = JsonDocument.Parse("true");
            copy.Extra[PendingField] = marker.RootElement.Clone();
        }
        else if (copy.Extra is { Count: 0 })
        {
            copy.Extra = null;
        }

        return copy;
    }

    /// <summary>
    ///     Makes a session the single writer for a strat until the returned handle is disposed (§3.11). Exclusive
    ///     per process: a second session on the same strat is a programming error.
    /// </summary>
    /// <param name="id">The strat's id.</param>
    /// <param name="session">The session taking it.</param>
    public IDisposable CheckOut(Guid id, StratSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_checkedOut.TryGetValue(id, out StratSession? holder) && !ReferenceEquals(holder, session))
            {
                throw new InvalidOperationException($"strat {id} is already checked out by another session");
            }

            _checkedOut[id] = session;
        }

        return new CheckOutScope(this, id, session);
    }

    /// <summary>
    ///     The session holding a strat, or null. A writer outside the Strat Book (Create Strat From Round, an
    ///     import) applies its ops through it while it is there instead of saving past it.
    /// </summary>
    /// <param name="id">The strat's id.</param>
    public StratSession? SessionFor(Guid id)
    {
        lock (_gate)
        {
            return _checkedOut.GetValueOrDefault(id);
        }
    }

    /// <summary>A strat's log in file order. A torn last line (a crash mid-append) is skipped.</summary>
    /// <param name="id">The strat's id.</param>
    public IReadOnlyList<HistoryEntry> History(Guid id)
    {
        List<string> lines;
        string? path;
        lock (_gate)
        {
            path = _paths.GetValueOrDefault(id);
            lines = _memoryHistory.TryGetValue(id, out List<string>? memory) ? [.. memory] : [];
        }

        if (_root is not null)
        {
            if (path is null)
            {
                return [];
            }

            try
            {
                string historyPath = HistoryPathBeside(path);
                lines = File.Exists(historyPath) ? [.. File.ReadAllLines(historyPath)] : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        return ParseHistory(lines);
    }

    /// <summary>The strat as it was at a revision, rebuilt from its log. Null when the log does not reach it.</summary>
    /// <param name="id">The strat's id.</param>
    /// <param name="revision">The revision.</param>
    public StratDocument? Materialize(Guid id, int revision) => StratHistory.Materialize(History(id), revision);

    /// <summary>The owner's book, or a fresh one when none is stored or it cannot be read.</summary>
    /// <param name="owner">The owner.</param>
    public StratBook LoadBook(StratOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string? json = ReadSmallFile(owner.FolderName, _root is null ? null : Path.Combine(_root, owner.FolderName, BookFileName), _memoryBooks);
        StratBook? book = null;
        try
        {
            book = json is null ? null : JsonSerializer.Deserialize(json, StratJsonContext.Default.StratBook);
        }
        catch (JsonException)
        {
            book = null;
        }

        return book ?? new StratBook { Owner = owner.Clone() };
    }

    /// <summary>Writes an owner's book atomically. False on a failed write; never throws for I/O.</summary>
    /// <param name="book">The book; its owner names the folder.</param>
    public bool SaveBook(StratBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (OwnerProblem(book.Owner) is not null)
        {
            return false;
        }

        string json = JsonSerializer.Serialize(book, StratJsonContext.Default.StratBook);
        return WriteSmallFile(book.Owner.FolderName, _root is null ? null : Path.Combine(_root, book.Owner.FolderName, BookFileName),
            json, _memoryBooks);
    }

    /// <summary>An owner's callouts for a map, or an empty table when none is stored or it cannot be read.</summary>
    /// <param name="owner">The owner.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    public CalloutTable LoadCallouts(StratOwner owner, string map)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(map);
        string key = owner.FolderName + "/" + map.ToLowerInvariant();
        string? json = ReadSmallFile(key, _root is null ? null : Path.Combine(FolderFor(_root, owner, map), CalloutsFileName), _memoryCallouts);
        CalloutTable? table = null;
        try
        {
            table = json is null ? null : JsonSerializer.Deserialize(json, StratJsonContext.Default.CalloutTable);
        }
        catch (JsonException)
        {
            table = null;
        }

        return table ?? new CalloutTable { Map = map.ToLowerInvariant() };
    }

    /// <summary>
    ///     Writes an owner's callouts for the table's map. False when the validator refuses the table (a duplicate
    ///     alias, an alias to no place) or the write fails. The owner is a parameter because the file, as the
    ///     design shapes it, names its map but not its owner: the folder does that.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="table">The table.</param>
    public bool SaveCallouts(StratOwner owner, CalloutTable table)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(table);
        if (OwnerProblem(owner) is not null || MapProblem(table.Map) is not null
            || StratValidator.ValidateCallouts(table).Any(i => i.Severity == StratIssueSeverity.Refusal))
        {
            return false;
        }

        table.Map = table.Map.ToLowerInvariant();
        string key = owner.FolderName + "/" + table.Map;
        string json = JsonSerializer.Serialize(table, StratJsonContext.Default.CalloutTable);
        return WriteSmallFile(key, _root is null ? null : Path.Combine(FolderFor(_root, owner, table.Map), CalloutsFileName), json,
            _memoryCallouts);
    }

    /// <summary>Index rows matching every non-null filter, by name.</summary>
    public IReadOnlyList<StratIndexEntry> Query(StratOwner? owner, string? map, string? side, StratStatus? status) =>
    [
        .. Index.Where(e => (owner is null || e.Owner.Equals(owner))
                            && (map is null || string.Equals(e.Map, map, StringComparison.OrdinalIgnoreCase))
                            && (side is null || string.Equals(e.Side, side, StringComparison.Ordinal))
                            && (status is null || string.Equals(e.Status, status.Value.ToString(), StringComparison.Ordinal)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id)
    ];

    /// <summary>Persists <c>index.json</c>. Deferred like <c>DemoCacheStore.SaveIndex</c>; silent on failure because it is derived.</summary>
    public void SaveIndex()
    {
        string? indexPath = IndexPath;
        if (indexPath is null)
        {
            return;
        }

        StratIndexFile file = new() { Entries = [.. Index] };
        try
        {
            WriteAtomic(indexPath, JsonSerializer.Serialize(file, StratJsonContext.Default.StratIndexFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Rebuildable from the folders.
        }
    }

    /// <summary>Re-reads every strat under the owner folders into the index: the recovery for a lost index.</summary>
    public void RebuildIndexFromDisk()
    {
        if (_root is null)
        {
            return;
        }

        lock (_gate)
        {
            _index.Clear();
            _paths.Clear();
        }

        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach ((Guid id, string path) in EnumerateStrats())
        {
            IndexFile(id, path);
        }

        RecoverOrphanedLogs();
        SaveIndex();
        RaiseChanged(null);
    }

    // The log is ahead of the file after a crash between the two writes of a commit: apply the missing entries
    // and rewrite the file. A log that cannot be applied leaves the file as it is; the file still opens.
    private StratDocument Reconcile(StratDocument document, string? path)
    {
        IReadOnlyList<HistoryEntry> log = History(document.Id);
        if (log.Count == 0 || log[^1].Revision <= document.Revision)
        {
            return document;
        }

        // A working copy behind its log: a commit's line landed and its strat write did not, after an autosave
        // had put uncommitted edits in the file. Those edits are the commit's, so the log's state wins; replaying
        // its ops over the working copy could apply an array insert twice.
        if (IsPending(document))
        {
            try
            {
                if (StratHistory.Materialize(log, log[^1].Revision) is { } committed)
                {
                    WriteStrat(committed, path);
                    return committed;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or JsonException)
            {
                // The file as it is; it still opens.
            }

            return document;
        }

        try
        {
            JsonNode? root = StratHistory.ToNode(document);
            HistoryEntry? last = null;
            foreach (HistoryEntry entry in log.Where(e => e.Revision > document.Revision))
            {
                root = StratHistory.ApplyAll(root, entry.Ops);
                last = entry;
            }

            StratDocument? recovered = root?.Deserialize(StratJsonContext.Default.StratDocument);
            if (recovered is null || last is null)
            {
                return document;
            }

            recovered.Revision = last.Revision;
            recovered.ModifiedUtc = last.AtUtc;
            WriteStrat(recovered, path);
            return recovered;
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            return document;
        }
    }

    // A first commit whose log line landed and whose strat file did not: the listing finds only the log.
    private void RecoverOrphanedLogs()
    {
        foreach (string log in EnumerateFiles("*" + HistoryExtension))
        {
            string name = Path.GetFileName(log);
            if (!Guid.TryParse(name[..^HistoryExtension.Length], out Guid id)
                || File.Exists(Path.Combine(Path.GetDirectoryName(log)!, id.ToString("D") + StratExtension)))
            {
                continue;
            }

            List<HistoryEntry> entries;
            try
            {
                entries = ParseHistory(File.ReadAllLines(log));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            StratDocument? document;
            try
            {
                document = entries.Count == 0 ? null : StratHistory.Materialize(entries, entries[^1].Revision);
            }
            catch (Exception e) when (e is InvalidOperationException or JsonException)
            {
                document = null;
            }

            if (document is null || document.Id != id)
            {
                continue;
            }

            string path = Path.Combine(Path.GetDirectoryName(log)!, id.ToString("D") + StratExtension);
            if (WriteStrat(document, path))
            {
                IndexFile(id, path);
            }
        }
    }

    private static string? MoveIfRelocated(Guid id, string? oldPath, string? newPath)
    {
        if (oldPath is null || newPath is null || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An owner or map change moves the log with the strat, before the append, so the log stays whole.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            string oldLog = HistoryPathBeside(oldPath);
            if (File.Exists(oldLog))
            {
                File.Move(oldLog, HistoryPathBeside(newPath));
            }

            File.Delete(oldPath);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "the strat could not be moved to its new book or map";
        }
    }

    private bool AppendHistory(StratDocument committed, HistoryEntry entry)
    {
        string line = JsonSerializer.Serialize(entry, StratHistoryJsonContext.Default.HistoryEntry);
        if (_root is null)
        {
            lock (_gate)
            {
                if (!_memoryHistory.TryGetValue(committed.Id, out List<string>? lines))
                {
                    _memoryHistory[committed.Id] = lines = [];
                }

                lines.Add(line);
            }

            return true;
        }

        string historyPath = HistoryPathBeside(StratPath(committed));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);

            // Opened for append and never rewritten (§3.8). A log whose last line was torn by a crash gets a
            // newline first, so the torn line stays alone and is skipped rather than swallowing this one.
            using FileStream stream = new(historyPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes((EndsTorn(historyPath, stream.Position) ? "\n" : "") + line + "\n");
            stream.Write(bytes);
            stream.Flush(true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool EndsTorn(string path, long length)
    {
        if (length == 0)
        {
            return false;
        }

        using FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        reader.Seek(-1, SeekOrigin.End);
        return reader.ReadByte() != '\n';
    }

    private bool WriteStrat(StratDocument document, string? path)
    {
        string json = Serialize(document);
        if (path is null)
        {
            lock (_gate)
            {
                _memoryStrats[document.Id] = json;
                _index[document.Id] = StratIndexEntry.From(document);
            }

            return true;
        }

        try
        {
            WriteAtomic(path, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        lock (_gate)
        {
            _paths[document.Id] = path;
            _index[document.Id] = StratIndexEntry.From(document);
        }

        return true;
    }

    private string? ReadStratText(Guid id, out string? path, out bool ioFailed)
    {
        ioFailed = false;
        lock (_gate)
        {
            path = _paths.GetValueOrDefault(id);
            if (_root is null)
            {
                return _memoryStrats.GetValueOrDefault(id);
            }
        }

        if (path is null)
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ioFailed = true;
            return null;
        }
    }

    private string? ReadSmallFile(string key, string? path, Dictionary<string, string> memory)
    {
        if (path is null)
        {
            lock (_gate)
            {
                return memory.GetValueOrDefault(key);
            }
        }

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool WriteSmallFile(string key, string? path, string json, Dictionary<string, string> memory)
    {
        if (path is null)
        {
            lock (_gate)
            {
                memory[key] = json;
            }

            return true;
        }

        try
        {
            WriteAtomic(path, json);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private string StratPath(StratDocument document) =>
        Path.Combine(FolderFor(_root!, document.Owner, document.Map), document.Id.ToString("D") + StratExtension);

    private static string HistoryPathBeside(string stratPath) =>
        stratPath[..^StratExtension.Length] + HistoryExtension;

    private static string? FolderProblem(StratDocument document) =>
        OwnerProblem(document.Owner) ?? MapProblem(document.Map);

    // The owner and the map become folder names; anything that could escape the root or collide with
    // another book is refused before a path is built from it.
    private static string? OwnerProblem(StratOwner owner) =>
        (owner.IsTeam && owner.TeamId != Guid.Empty)
        || (string.Equals(owner.Kind, StratOwner.MeKind, StringComparison.Ordinal) && owner.TeamId is null)
            ? null
            : "the owner names no book";

    private static string? MapProblem(string map) =>
        string.IsNullOrWhiteSpace(map) || map.StartsWith('.') || map.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || map.Contains('/') || map.Contains('\\')
            ? "the map is not a folder name"
            : null;

    private static StratDocument? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, StratJsonContext.Default.StratDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<HistoryEntry> ParseHistory(IEnumerable<string> lines)
    {
        List<HistoryEntry> entries = [];
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize(line, StratHistoryJsonContext.Default.HistoryEntry) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A torn line from a crash mid-append; the commit it belonged to never reached the strat file.
            }
        }

        return entries;
    }

    // <root>/<owner>/<map>/<id>.dvstrat.json. The trash sits one level deeper and is never listed.
    private IEnumerable<(Guid Id, string Path)> EnumerateStrats()
    {
        foreach (string file in EnumerateFiles("*" + StratExtension))
        {
            string name = Path.GetFileName(file);
            if (Guid.TryParse(name[..^StratExtension.Length], out Guid id))
            {
                yield return (id, file);
            }
        }
    }

    private List<string> EnumerateFiles(string pattern)
    {
        List<string> files = [];
        if (_root is null || !Directory.Exists(_root))
        {
            return files;
        }

        try
        {
            foreach (string ownerDir in Directory.EnumerateDirectories(_root))
            {
                if (StratOwner.FromFolderName(Path.GetFileName(ownerDir)) is null)
                {
                    continue;
                }

                foreach (string mapDir in Directory.EnumerateDirectories(ownerDir))
                {
                    if (!Path.GetFileName(mapDir).StartsWith('.'))
                    {
                        files.AddRange(Directory.EnumerateFiles(mapDir, pattern));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished mid-listing is simply not listed.
        }

        return files;
    }

    private void IndexFile(Guid id, string path)
    {
        lock (_gate)
        {
            _paths[id] = path;
        }

        StratDocument? document;
        try
        {
            document = Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            document = null;
        }

        if (document is null || document.Id != id)
        {
            // Listed (so Load can report it unreadable and Save declines to overwrite it) but not indexed.
            return;
        }

        document = Reconcile(document, path);
        lock (_gate)
        {
            _index[id] = StratIndexEntry.From(document);
        }
    }

    private void LoadIndex()
    {
        string? indexPath = IndexPath;
        if (indexPath is null)
        {
            return;
        }

        StratIndexFile? file = null;
        try
        {
            if (File.Exists(indexPath))
            {
                file = JsonSerializer.Deserialize(File.ReadAllText(indexPath), StratJsonContext.Default.StratIndexFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            file = null;
        }

        if (file?.Entries is null)
        {
            // Missing or corrupt: the strat files are the truth, so the index is read back off them.
            RebuildIndexFromDisk();
            return;
        }

        Dictionary<Guid, StratIndexEntry> rows = file.Entries.Where(e => e.Id != Guid.Empty).GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // Reconcile against the listing, which costs no reads for a known strat: a file the index does not
        // name, or names at an older revision than its log, is read; a row whose file is gone is dropped.
        bool changed = false;
        foreach ((Guid id, string path) in EnumerateStrats())
        {
            lock (_gate)
            {
                _paths[id] = path;
            }

            if (rows.Remove(id, out StratIndexEntry? row) && !LogIsAhead(path, row.Revision))
            {
                lock (_gate)
                {
                    _index[id] = row;
                }

                continue;
            }

            IndexFile(id, path);
            changed = true;
        }

        changed |= rows.Count > 0;
        int before = Index.Count;
        RecoverOrphanedLogs();
        changed |= Index.Count != before;
        if (changed)
        {
            SaveIndex();
        }
    }

    // A commit writes the log before the strat file, so a log newer than its strat is the only sign of a
    // crash between the two. Two stats per strat keep startup to index.json plus the listing; only a log that
    // is newer is read. A crash inside the timestamp resolution is still caught when the strat is opened.
    private static bool LogIsAhead(string stratPath, int revision)
    {
        try
        {
            string log = HistoryPathBeside(stratPath);
            if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) <= File.GetLastWriteTimeUtc(stratPath))
            {
                return false;
            }

            List<HistoryEntry> entries = ParseHistory(File.ReadLines(log));
            return entries.Count > 0 && entries[^1].Revision > revision;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Temp file plus replace, the config-root write idiom (<c>DemoCacheStore.WriteAtomic</c>), with this
    ///     store's own temp prefix so a stray file says whose it was.
    /// </summary>
    private static void WriteAtomic(string targetPath, string content)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".strat-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, null);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the write's own failure is what the caller hears about.
        }
    }

    private void RaiseChanged(Guid? id) => _post(() => Changed?.Invoke(id));

    private void Release(Guid id, StratSession session)
    {
        lock (_gate)
        {
            if (_checkedOut.TryGetValue(id, out StratSession? holder) && ReferenceEquals(holder, session))
            {
                _checkedOut.Remove(id);
            }
        }
    }

    private sealed class CheckOutScope(StratStore store, Guid id, StratSession session) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                store.Release(id, session);
            }
        }
    }
}

/// <summary>The outcome of a load: the strat after reconciliation, or why there is none.</summary>
/// <param name="Document">The strat, or null when absent or unreadable.</param>
/// <param name="Path">The file that was read, or null in memory or when the strat is unknown.</param>
/// <param name="IsUnreadable">A file exists and could not be read or parsed. Never overwritten.</param>
/// <param name="Issues">The validator's findings on load; the strat opens whatever they say.</param>
public sealed record StratLoadResult(StratDocument? Document, string? Path, bool IsUnreadable, IReadOnlyList<StratIssue> Issues)
{
    public static StratLoadResult Unreadable(Guid id, string? path) =>
        new(null, path, true, [new StratIssue(StratIssueSeverity.Refusal, "", $"strat {id} could not be read")]);
}

/// <summary>The outcome of a commit. <see cref="Reason" /> is what the tab's status line says when it failed.</summary>
public sealed record StratSaveResult(bool Saved, string? Reason, int Revision, IReadOnlyList<StratIssue> Issues)
{
    public static StratSaveResult Failed(string reason, IReadOnlyList<StratIssue> issues) => new(false, reason, 0, issues);
}
