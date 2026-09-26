#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     The user's edits to each team's Dossier (plan.md §3, Dossier Editing And Export): stars, rewritten
///     lines, left-out lines, free notes and the summary, filed per team.
///     <para>
///         <b>Persistence.</b> <c>dossier-notes.json</c> beside <c>teams.json</c>, written whole through
///         <see cref="DemoCacheStore.WriteAtomic" /> after every mutation, as <see cref="VetoHistoryStore" />
///         does. A null config root (the browser, tests) keeps the notes for the session; a file that cannot
///         be read, or is at a newer schema, is refused and never overwritten.
///     </para>
///     <para><b>Threading.</b> UI thread only: every caller is a view model reacting to a click or an edit.</para>
/// </summary>
public sealed class DossierNotesStore
{
    /// <summary>The file under the config root.</summary>
    public const string FileName = "dossier-notes.json";

    /// <summary>The key prefix of a user-written finding.</summary>
    public const string NoteKeyPrefix = "note:";

    private readonly string? _path;
    private readonly List<DossierTeamNotes> _teams = [];
    private bool _refused;

    /// <param name="configRoot">The app config root, or null for a session-only store (the browser, tests).</param>
    public DossierNotesStore(string? configRoot)
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

    /// <summary>The key of a user-written note.</summary>
    /// <param name="id">The note's id.</param>
    public static string NoteKey(Guid id) => NoteKeyPrefix + id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The team's notes; an empty record (not stored) when the user has done nothing yet.</summary>
    /// <param name="teamId">The team.</param>
    public DossierTeamNotes For(Guid teamId) =>
        _teams.FirstOrDefault(t => t.TeamId == teamId) ?? new DossierTeamNotes { TeamId = teamId };

    /// <summary>Stars or unstars a finding.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The finding's key.</param>
    /// <param name="starred">True to put it on the one-pager.</param>
    public void SetStarred(Guid teamId, string key, bool starred)
    {
        ArgumentNullException.ThrowIfNull(key);
        DossierTeamNotes notes = Ensure(teamId);
        if (Toggle(notes.Starred, key, starred))
        {
            Commit();
        }
    }

    /// <summary>
    ///     Rewrites a generated finding. Null, blank, or the generated text itself drops the edit, so a
    ///     finding whose numbers later change is not pinned to the old wording by an edit that changed nothing.
    /// </summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The finding's key.</param>
    /// <param name="text">The new text.</param>
    /// <param name="generated">The finding's generated text, which an edit equal to it is not kept over.</param>
    public void SetEdit(Guid teamId, string key, string? text, string generated)
    {
        ArgumentNullException.ThrowIfNull(key);
        DossierTeamNotes notes = Ensure(teamId);
        string? trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, generated, StringComparison.Ordinal))
        {
            if (notes.Edits.Remove(key))
            {
                Commit();
            }

            return;
        }

        if (notes.Edits.TryGetValue(key, out string? old) && string.Equals(old, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        notes.Edits[key] = trimmed;
        Commit();
    }

    /// <summary>Leaves a generated finding out of both forms, or brings it back.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The finding's key.</param>
    /// <param name="hidden">True to leave it out.</param>
    public void SetHidden(Guid teamId, string key, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(key);
        DossierTeamNotes notes = Ensure(teamId);
        if (Toggle(notes.Hidden, key, hidden))
        {
            Commit();
        }
    }

    /// <summary>Brings back every left-out finding of the team.</summary>
    /// <param name="teamId">The team.</param>
    public void ClearHidden(Guid teamId)
    {
        DossierTeamNotes notes = Ensure(teamId);
        if (notes.Hidden.Count == 0)
        {
            return;
        }

        notes.Hidden.Clear();
        Commit();
    }

    /// <summary>Sets the summary paragraph.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="summary">The text; null or blank clears it.</param>
    public void SetSummary(Guid teamId, string? summary)
    {
        DossierTeamNotes notes = Ensure(teamId);
        string value = summary?.Trim() ?? "";
        if (string.Equals(notes.Summary, value, StringComparison.Ordinal))
        {
            return;
        }

        notes.Summary = value;
        Commit();
    }

    /// <summary>Adds a note and returns its key, or null when the text is blank.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="text">The note.</param>
    public string? AddNote(Guid teamId, string? text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
        {
            return null;
        }

        DossierNote note = new() { Text = value };
        Ensure(teamId).Notes.Add(note);
        Commit();
        return NoteKey(note.Id);
    }

    /// <summary>Rewrites a note; blank text leaves it as it was (removing is its own action).</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The note's key.</param>
    /// <param name="text">The new text.</param>
    public void EditNote(Guid teamId, string key, string? text)
    {
        string value = text?.Trim() ?? "";
        DossierTeamNotes notes = Ensure(teamId);
        if (value.Length == 0 || notes.Notes.FirstOrDefault(n => NoteKey(n.Id) == key) is not { } note
                              || string.Equals(note.Text, value, StringComparison.Ordinal))
        {
            return;
        }

        note.Text = value;
        Commit();
    }

    /// <summary>Removes a note and its star.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The note's key.</param>
    public void RemoveNote(Guid teamId, string key)
    {
        DossierTeamNotes notes = Ensure(teamId);
        if (notes.Notes.RemoveAll(n => NoteKey(n.Id) == key) == 0)
        {
            return;
        }

        notes.Starred.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
        Commit();
    }

    // Adds or removes a key; true when the list changed.
    private static bool Toggle(List<string> keys, string key, bool present)
    {
        bool has = keys.Contains(key, StringComparer.Ordinal);
        if (has == present)
        {
            return false;
        }

        if (present)
        {
            keys.Add(key);
        }
        else
        {
            keys.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
        }

        return true;
    }

    private DossierTeamNotes Ensure(Guid teamId)
    {
        if (_teams.FirstOrDefault(t => t.TeamId == teamId) is { } found)
        {
            return found;
        }

        DossierTeamNotes created = new() { TeamId = teamId };
        _teams.Add(created);
        return created;
    }

    private void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            DossierNotesFile? file = JsonSerializer.Deserialize<DossierNotesFile>(File.ReadAllText(_path), DossierNotesFile.JsonOptions);
            if (file is null || file.SchemaVersion > DossierNotesFile.CurrentSchema)
            {
                Refuse($"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            foreach (DossierTeamNotes team in file.Teams)
            {
                team.Edits = new Dictionary<string, string>(team.Edits, StringComparer.Ordinal);
                _teams.Add(team);
            }
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
        _teams.Clear();
    }

    private void Save()
    {
        if (_path is null || _refused)
        {
            return;
        }

        try
        {
            DossierNotesFile file = new() { Teams = [.. _teams] };
            DemoCacheStore.WriteAtomic(_path, JsonSerializer.Serialize(file, DossierNotesFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory notes stand for the session and the next mutation retries.
        }
    }
}
