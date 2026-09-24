#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Services.Review;

/// <summary>
///     The Review Queue: one ordered list of clips from any demo, each <c>(demo, from, to, note)</c> with
///     a one-line question, grouped by title cards into sections. Every surface that finds moments puts
///     them here (the Reels tray, Result Cards, a Matrix cell's refs, a pick at the playhead) and the
///     Review tab walks them; the Reels tray is one client among those, reading back the clips it staged
///     (plan F8: the tray was already cross-demo, and this is its generalisation rather than a second
///     subsystem beside it).
///     <para>
///         <b>Threading.</b> UI thread only, like the Reels tray it replaced: every caller is a view
///         model reacting to a click, and <see cref="Changed" /> fires synchronously after each mutation
///         so a client re-reads a list that is already final.
///     </para>
///     <para>
///         <b>Persistence.</b> <c>review-queue.json</c> under the config root, written whole through
///         <see cref="DemoCacheStore.WriteAtomic" /> after every mutation (a queue is tens of rows, not
///         thousands). A null root keeps the queue for the session: the browser host and tests. A file
///         that cannot be read, or is at a newer schema, is refused and never overwritten.
///     </para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The plan's name for the feature; it is a queue a reviewer walks, not a Queue<T>.")]
public sealed class ReviewQueue
{
    /// <summary>The file under the config root.</summary>
    public const string FileName = "review-queue.json";

    private readonly List<ReviewEntry> _entries = [];
    private readonly string? _path;
    private bool _refused;

    /// <param name="configRoot">The app config root, or null for a session-only queue (the browser, tests).</param>
    public ReviewQueue(string? configRoot)
    {
        _path = configRoot is null ? null : Path.Combine(configRoot, FileName);
        Load();
    }

    /// <summary>True when nothing persists: the browser host, and tests without a root.</summary>
    public bool IsSessionOnly => _path is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>The queue in order, title cards and clips together. A copy: the caller may hold it.</summary>
    public IReadOnlyList<ReviewEntry> Entries => [.. _entries];

    /// <summary>The clips in order, without the title cards.</summary>
    public IReadOnlyList<ReviewEntry> Clips => [.. _entries.Where(e => e.Kind == ReviewEntryKind.Clip)];

    /// <summary>How many clips are queued.</summary>
    public int ClipCount => _entries.Count(e => e.Kind == ReviewEntryKind.Clip);

    /// <summary>Raised on the calling thread after every mutation that changed something.</summary>
    public event Action? Changed;

    /// <summary>The entry with this id, or null.</summary>
    /// <param name="id">The entry's id.</param>
    public ReviewEntry? Find(Guid id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    ///     The title of the section a clip sits in: the nearest title card above it, or null when the
    ///     clip is above every card.
    /// </summary>
    /// <param name="id">The clip's id.</param>
    public string? SectionOf(Guid id)
    {
        int index = IndexOf(id);
        for (int i = index - 1; i >= 0; i--)
        {
            if (_entries[i].Kind == ReviewEntryKind.Section)
            {
                return _entries[i].Title;
            }
        }

        return null;
    }

    /// <summary>
    ///     Appends entries at the end, under a new title card when <paramref name="sectionTitle" /> is
    ///     given. A clip already queued (same demo, same range, same highlight) is skipped, so sending a
    ///     result set twice does not double it; a title card with no clip left after the skip is not
    ///     added either.
    /// </summary>
    /// <param name="entries">Clips and title cards, in the order they should appear.</param>
    /// <param name="sectionTitle">A title card to open the batch with, or null.</param>
    /// <param name="sectionSubtitle">The line under that title.</param>
    /// <returns>How many clips were added.</returns>
    public int Add(IEnumerable<ReviewEntry> entries, string? sectionTitle = null, string sectionSubtitle = "")
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<ReviewEntry> batch = [];
        int clips = 0;
        foreach (ReviewEntry entry in entries)
        {
            if (entry.Kind == ReviewEntryKind.Clip)
            {
                if (_entries.Any(e => SameClip(e, entry)) || batch.Any(e => SameClip(e, entry)))
                {
                    continue;
                }

                clips++;
            }

            batch.Add(entry.Id == Guid.Empty ? entry with { Id = Guid.NewGuid() } : entry);
        }

        if (batch.Count == 0 || (clips == 0 && sectionTitle is not null))
        {
            return 0;
        }

        if (sectionTitle is not null)
        {
            _entries.Add(ReviewEntry.Section(sectionTitle, sectionSubtitle));
        }

        _entries.AddRange(batch);
        Commit();
        return clips;
    }

    /// <summary>Puts a title card at <paramref name="index" />, clamped; at the end when null.</summary>
    /// <param name="title">The card's title.</param>
    /// <param name="index">Where it goes, or null for the end.</param>
    /// <returns>The card.</returns>
    public ReviewEntry AddSection(string title, int? index = null)
    {
        ReviewEntry card = ReviewEntry.Section(title);
        _entries.Insert(Math.Clamp(index ?? _entries.Count, 0, _entries.Count), card);
        Commit();
        return card;
    }

    /// <summary>Removes entries by id; ids not in the queue are ignored.</summary>
    /// <param name="ids">The entries to remove.</param>
    /// <returns>How many were removed.</returns>
    public int Remove(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        HashSet<Guid> doomed = [.. ids];
        int removed = _entries.RemoveAll(e => doomed.Contains(e.Id));
        if (removed > 0)
        {
            Commit();
        }

        return removed;
    }

    /// <summary>Empties the queue.</summary>
    public void Clear()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        _entries.Clear();
        Commit();
    }

    /// <summary>Moves one entry by <paramref name="delta" /> places, clamped to the queue.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="delta">Signed places; negative is up.</param>
    public void Move(Guid id, int delta)
    {
        int from = IndexOf(id);
        if (from >= 0)
        {
            MoveTo(id, from + delta);
        }
    }

    /// <summary>Moves one entry to an absolute position, clamped to the queue.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="index">Zero-based destination.</param>
    public void MoveTo(Guid id, int index)
    {
        int from = IndexOf(id);
        if (from < 0)
        {
            return;
        }

        int to = Math.Clamp(index, 0, _entries.Count - 1);
        if (to == from)
        {
            return;
        }

        ReviewEntry entry = _entries[from];
        _entries.RemoveAt(from);
        _entries.Insert(to, entry);
        Commit();
    }

    /// <summary>
    ///     Re-orders a subset of the queue among the positions it already holds: the entries named keep
    ///     their slots as a set and take the given order within them, and every other entry stays where
    ///     it is. This is how a client that sees only its own clips (the Reels tray reordering its
    ///     groups) moves them without disturbing what other surfaces queued around them.
    /// </summary>
    /// <param name="orderedIds">The subset in its new order; ids not in the queue are ignored.</param>
    public void ReorderSubset(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        List<ReviewEntry> ordered = [.. orderedIds.Select(Find).OfType<ReviewEntry>().Distinct()];
        HashSet<Guid> members = [.. ordered.Select(e => e.Id)];
        List<int> slots = [.. Enumerable.Range(0, _entries.Count).Where(i => members.Contains(_entries[i].Id))];
        bool changed = false;
        for (int i = 0; i < slots.Count; i++)
        {
            if (_entries[slots[i]].Id != ordered[i].Id)
            {
                _entries[slots[i]] = ordered[i];
                changed = true;
            }
        }

        if (changed)
        {
            Commit();
        }
    }

    /// <summary>Replaces one entry through an edit; the id and kind are kept whatever the edit returns.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="edit">The new value from the old.</param>
    /// <returns>False when the id is not in the queue.</returns>
    public bool Update(Guid id, Func<ReviewEntry, ReviewEntry> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        int index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        ReviewEntry old = _entries[index];
        ReviewEntry updated = edit(old) with { Id = old.Id, Kind = old.Kind };
        if (updated == old)
        {
            return true;
        }

        _entries[index] = updated;
        Commit();
        return true;
    }

    /// <summary>Sets a clip's one-line question.</summary>
    /// <param name="id">The clip.</param>
    /// <param name="question">The question; trimmed, and one line.</param>
    public bool SetQuestion(Guid id, string question) =>
        Update(id, e => e with { Question = OneLine(question) });

    /// <summary>Sets a clip's note, or a title card's subtitle.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="note">The note; trimmed, and one line.</param>
    public bool SetNote(Guid id, string note) => Update(id, e => e with { Note = OneLine(note) });

    /// <summary>Renames a title card.</summary>
    /// <param name="id">The card.</param>
    /// <param name="title">The title; trimmed, and one line.</param>
    public bool SetTitle(Guid id, string title) => Update(id, e => e with { Title = OneLine(title) });

    /// <summary>
    ///     A clip from a tag instance: the shape a Matrix cell and a <c>TagQuery.Find</c> result hand the
    ///     queue. The ref names the demo by hash, so the path comes from the cache index; a hash no
    ///     indexed demo carries has nothing to open and returns null (the caller says "demo not in
    ///     library" rather than queueing a dead link).
    /// </summary>
    /// <param name="instance">The located instance.</param>
    /// <param name="pathForSha256">Hash to path, the cache's <c>TryGetIndexBySha256</c>.</param>
    /// <param name="tickRate">The demo's tick rate when the caller knows it, else 0.</param>
    public static ReviewEntry? FromTag(TagInstanceRef instance, Func<string, string?> pathForSha256, int tickRate = 0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(pathForSha256);
        if (pathForSha256(instance.Sha256) is not { Length: > 0 } path)
        {
            return null;
        }

        string note = instance.Round is int round
            ? string.Create(CultureInfo.InvariantCulture, $"{instance.Code} · round {round}")
            : instance.Code;
        return ReviewEntry.Clip(path, instance.FromTick, instance.ToTick, note, ReviewSources.Tag, tickRate,
            instance.Sha256);
    }

    // Two clips are the same when a second send could only duplicate the first: the demo (paths compare
    // the way the library does, case-insensitively), the range and the highlight identity. The note and
    // the question are not identity; a reviewer may have written one already.
    private static bool SameClip(ReviewEntry a, ReviewEntry b) =>
        a.Kind == ReviewEntryKind.Clip && b.Kind == ReviewEntryKind.Clip
                                       && a.FromTick == b.FromTick && a.ToTick == b.ToTick
                                       && string.Equals(a.DemoPath, b.DemoPath, StringComparison.OrdinalIgnoreCase)
                                       && Equals(a.Highlight, b.Highlight);

    private static string OneLine(string? text) =>
        (text ?? "").ReplaceLineEndings(" ").Trim();

    private int IndexOf(Guid id) => _entries.FindIndex(e => e.Id == id);

    private void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    // ── The file ──────────────────────────────────────────────────────────────

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            ReviewQueueFile? file = JsonSerializer.Deserialize<ReviewQueueFile>(File.ReadAllText(_path), ReviewQueueFile.JsonOptions);
            if (file is null || file.SchemaVersion > ReviewQueueFile.CurrentSchema)
            {
                Refuse($"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            // An entry with no id cannot be named by any mutation; give it one rather than drop it.
            _entries.AddRange(file.Entries.Select(e => e.Id == Guid.Empty ? e with { Id = Guid.NewGuid() } : e));
        }
        catch (Exception ex)
        {
            Refuse($"{FileName} could not be read: {ex.Message}");
        }
    }

    private void Refuse(string problem)
    {
        _refused = true;
        FileProblem = problem;
        _entries.Clear();
    }

    private void Save()
    {
        if (_path is null || _refused)
        {
            return;
        }

        try
        {
            ReviewQueueFile file = new() { Entries = [.. _entries] };
            DemoCacheStore.WriteAtomic(_path, JsonSerializer.Serialize(file, ReviewQueueFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory queue stands for the session and the next mutation retries.
        }
    }
}
