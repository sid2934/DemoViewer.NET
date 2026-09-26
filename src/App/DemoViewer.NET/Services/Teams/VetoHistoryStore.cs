#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     The optional, user-entered veto history the Map Pool Record's "Done" line asks for (plan.md §3,
///     Map Pool Record; F12, D5): manual entry only, no scraping, filed per opponent team.
///     <para>
///         <b>Persistence.</b> <c>veto-history.json</c> beside <c>teams.json</c>, written whole through
///         <see cref="DemoCacheStore.WriteAtomic" /> after every mutation — the same small-file rule
///         <see cref="Review.ReviewQueue" /> follows. A null config root (the browser, tests) keeps the
///         history for the session; a file that cannot be read, or is at a newer schema, is refused and
///         never overwritten.
///     </para>
///     <para><b>Threading.</b> UI thread only: every caller is a view model reacting to a click.</para>
/// </summary>
public sealed class VetoHistoryStore
{
    /// <summary>The file under the config root.</summary>
    public const string FileName = "veto-history.json";

    private readonly List<VetoEntry> _entries = [];
    private readonly string? _path;
    private bool _refused;

    /// <param name="configRoot">The app config root, or null for a session-only store (the browser, tests).</param>
    public VetoHistoryStore(string? configRoot)
    {
        _path = configRoot is null ? null : Path.Combine(configRoot, FileName);
        Load();
    }

    /// <summary>True when nothing persists: the browser host, and tests without a root.</summary>
    public bool IsSessionOnly => _path is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>Raised on the calling thread after every mutation that changed something.</summary>
    public event Action? Changed;

    /// <summary>The opponent's steps, in the order the user gave them.</summary>
    /// <param name="opponentTeamId">The opponent the history is filed under.</param>
    public IReadOnlyList<VetoEntry> For(Guid opponentTeamId) =>
        [.. _entries.Where(e => e.OpponentTeamId == opponentTeamId).OrderBy(e => e.Order)];

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
            DemoCacheStore.WriteAtomic(_path, JsonSerializer.Serialize(file, VetoHistoryFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory history stands for the session and the next mutation retries.
        }
    }
}
