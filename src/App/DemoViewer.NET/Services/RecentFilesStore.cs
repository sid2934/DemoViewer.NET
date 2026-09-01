#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     One recently-opened demo: its absolute <paramref name="Path" />, the parsed <paramref name="MapName" />
///     when known at open time (else <c>null</c>), and the UTC instant it was opened. Persisted as-is in the
///     config file's <c>Recents</c> section; System.Text.Json round-trips the record through its
///     single parameterized constructor.
/// </summary>
/// <param name="Path">Absolute filesystem path of the demo that was opened.</param>
/// <param name="MapName">Parsed map name (e.g. <c>de_dust2</c>) if it was known when opened, else <c>null</c>.</param>
/// <param name="OpenedAtUtc">When the demo was opened (UTC).</param>
public sealed record RecentFile(string Path, string? MapName, DateTime OpenedAtUtc);

/// <summary>
///     Best-effort disk persistence for the most-recently-opened demos: the store the
///     Library landing binds its "recent files" strip to. The list is kept most-recent-first, capped
///     at <see cref="MaxRecent" />, and de-duplicated by path (a re-open moves the entry to the front rather
///     than adding a duplicate). Ordering is by insertion (front-insert on open), NOT by re-sorting
///     <see cref="RecentFile.OpenedAtUtc" />, so two opens within the same clock tick still order correctly.
///     <para>
///         Persistence is delegated to <see cref="SettingsService" />: recents are the <c>Recents</c>
///         section of the single consolidated config file (formerly the standalone
///         <c>recent-files.json</c>). The live most-recent-first list is always kept in memory here, so a
///         <c>null</c> settings service (the WASM/browser sandbox, no filesystem, or the designer / older
///         test path) simply means "recents stay in-memory for the life of the process"; the actual
///         write short-circuits inside <see cref="SettingsService.SaveRecents" />. A runtime check is used
///         rather than a <c>#if BROWSER</c> define because the same <c>DemoViewer.NET</c> assembly is
///         compiled once and shared by both hosts; mirrors <see cref="SessionStore" /> /
///         <see cref="BookmarkStore" />.
///     </para>
/// </summary>
public sealed class RecentFilesStore
{
    /// <summary>Maximum recents retained; older entries drop off the tail on each new open.</summary>
    public const int MaxRecent = 10;

    // Paths compare case-insensitively: macOS/Windows filesystems are case-insensitive and the library
    // indexer keys the same way, so re-opening the same file (any casing) de-dupes rather than duplicating.
    private static readonly StringComparer _pathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly List<RecentFile> _items;

    // The single serializer of the consolidated config file. Null → in-memory only (no persistence).
    private readonly SettingsService? _settings;

    /// <param name="settings">
    ///     The consolidated-config serializer that owns the <c>Recents</c> section. The real app
    ///     injects the singleton <see cref="SettingsService" />; a temp-dir-backed one is the test seam
    ///     (keeps tests out of the real config folder). Null → in-memory only, no persistence (the
    ///     designer / older-test path: matches the pre-consolidation WASM behavior).
    /// </param>
    public RecentFilesStore(SettingsService? settings = null)
    {
        _settings = settings;
        _items = Load();
    }

    /// <summary>The recents, most-recent-first. Live: mutated in place by <see cref="RecordOpen" /> / <see cref="Remove" />.</summary>
    public IReadOnlyList<RecentFile> Items => _items;

    /// <summary>Raised after the recents list changes (an open recorded, or a stale entry pruned).</summary>
    public event Action? Changed;

    /// <summary>
    ///     Records <paramref name="path" /> as the most-recently-opened demo: moves it to the front (de-duped
    ///     by path), caps the list to <see cref="MaxRecent" />, persists (best-effort), and raises
    ///     <see cref="Changed" />. No-op on a null/empty path.
    /// </summary>
    public void RecordOpen(string path, string? mapName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _items.RemoveAll(r => _pathComparer.Equals(r.Path, path));
        _items.Insert(0, new RecentFile(path, mapName, DateTime.UtcNow));
        if (_items.Count > MaxRecent)
        {
            _items.RemoveRange(MaxRecent, _items.Count - MaxRecent);
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>
    ///     Removes any recent entry for <paramref name="path" /> (used to prune a file that no longer exists
    ///     when the user tries to open it). Persists + raises <see cref="Changed" /> only when something was
    ///     actually removed. Returns <c>true</c> if an entry was pruned.
    /// </summary>
    public bool Remove(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (_items.RemoveAll(r => _pathComparer.Equals(r.Path, path)) == 0)
        {
            return false;
        }

        Save();
        Changed?.Invoke();
        return true;
    }

    // Restores the persisted recents (most-recent-first as written) from the consolidated config file's
    // Recents section, or an empty list if none / unavailable. SettingsService.LoadRecents also runs the
    // one-time import of a legacy recent-files.json.
    private List<RecentFile> Load()
    {
        List<RecentFile> loaded = _settings?.LoadRecents().ToList() ?? [];

        // Defensive: drop any malformed entries with a blank path, and honour the cap even if an
        // externally-edited file over-fills it.
        loaded.RemoveAll(r => r is null || string.IsNullOrEmpty(r.Path));
        if (loaded.Count > MaxRecent)
        {
            loaded.RemoveRange(MaxRecent, loaded.Count - MaxRecent);
        }

        return loaded;
    }

    // Persists the current list into the config file's Recents section. No-op when there is no settings
    // service (in-memory only) or on I/O failure (SettingsService.SaveRecents swallows write failures).
    private void Save() => _settings?.SaveRecents(_items);
}
