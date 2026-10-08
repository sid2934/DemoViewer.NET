#region

using System.Globalization;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     The optional, user-entered veto history the Map Pool Record's "Done" line asks for:
///     manual entry only, no scraping, filed per opponent team.
///     <para>
///         <b>Persistence.</b> <c>veto-history.json</c> beside <c>teams.json</c>, written whole through
///         <see cref="AtomicFile" /> after every mutation, the same small-file rule
///         <see cref="DemoViewer.NET.Services.Review.ReviewQueue" /> follows. A null path (the browser, tests) keeps the
///         history for the session; a file that cannot be read, or is at a newer schema, is refused and
///         never overwritten.
///     </para>
///     <para><b>Threading.</b> UI thread only: every caller is a view model reacting to a click.</para>
/// </summary>
public sealed class VetoHistoryStore
{
    /// <summary>The file's name.</summary>
    public const string FileName = "veto-history.json";

    private readonly List<VetoEntry> _entries = [];
    private readonly string? _path;
    private bool _refused;

    /// <param name="path">The <see cref="FileName" /> file, or null for a session-only store (the browser, tests).</param>
    public VetoHistoryStore(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>True when nothing persists: the browser host, and tests without a file.</summary>
    public bool IsSessionOnly => _path is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>Every team this store holds something for; Team Identity keeps these through a rebuild.</summary>
    public IReadOnlyList<Guid> TeamIds => [.. _entries.Select(e => e.OpponentTeamId).Distinct()];

    /// <summary>Raised on the calling thread after every mutation that changed something.</summary>
    public event Action? Changed;

    /// <summary>The opponent's steps, in the order the user gave them.</summary>
    /// <param name="opponentTeamId">The opponent the history is filed under.</param>
    public IReadOnlyList<VetoEntry> For(Guid opponentTeamId) =>
        [.. _entries.Where(e => e.OpponentTeamId == opponentTeamId).OrderBy(e => e.Order)];

    /// <summary>
    ///     Drops every step and re-reads the file: the recovery after it is deleted out from under the
    ///     store by the "delete extension data" action, so a later mutation does not resave what the delete
    ///     just removed.
    /// </summary>
    public void Reload()
    {
        _entries.Clear();
        _refused = false;
        FileProblem = null;
        Load();
        Changed?.Invoke();
    }

    /// <summary>Appends one step. The caller sets <see cref="VetoEntry.Order" />.</summary>
    /// <param name="entry">The step to append.</param>
    public void Add(VetoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes one step by id. No-op when it is not there.</summary>
    /// <param name="id">The step's id.</param>
    public void Remove(Guid id)
    {
        if (_entries.RemoveAll(e => e.Id == id) > 0)
        {
            Save();
            Changed?.Invoke();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            VetoHistoryFile? file = JsonSerializer.Deserialize<VetoHistoryFile>(File.ReadAllText(_path), VetoHistoryFile.JsonOptions);
            if (file is null || file.SchemaVersion > VetoHistoryFile.CurrentSchema)
            {
                Refuse($"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            _entries.AddRange(file.Entries);
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
            VetoHistoryFile file = new() { Entries = [.. _entries] };
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, VetoHistoryFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory history stands for the session and the next mutation retries.
        }
    }
}
