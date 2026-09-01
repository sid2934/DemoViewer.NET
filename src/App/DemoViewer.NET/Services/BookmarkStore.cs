#region

using System.Text.Json;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Best-effort disk persistence for <see cref="Bookmark" />s (F8.5 / A4).
///     <para>
///         File I/O does NOT work in the WASM/browser sandbox, so every method short-circuits when
///         <see cref="System.OperatingSystem.IsBrowser()" /> is true: bookmarks stay in-memory there.
///         A runtime check is used rather than a <c>#if BROWSER</c> define because the same
///         <c>DemoViewer.NET</c> assembly is compiled once and shared by both the desktop and browser
///         hosts; the define would need a csproj flag and would silently no-op in the wrong host.
///     </para>
///     Desktop persists to <c>%AppData%/DemoViewer.NET/SessionState.json</c>.
/// </summary>
public sealed class BookmarkStore
{
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true
    };

    private readonly string? _path;

    /// <summary>Initializes a new <see cref="BookmarkStore" /> instance.</summary>
    public BookmarkStore()
    {
        if (OperatingSystem.IsBrowser())
        {
            return; // no filesystem on WASM
        }

        _path = AppPaths.BookmarksFile;
    }

    /// <summary>Loads persisted bookmarks, or an empty list if none / unavailable.</summary>
    public List<Bookmark> Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(_path)) ?? [];
        }
        catch
        {
            return []; // bookmark restore is best-effort
        }
    }

    /// <summary>Persists <paramref name="bookmarks" />. No-op on WASM or on I/O failure.</summary>
    public void Save(IReadOnlyList<Bookmark> bookmarks)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(bookmarks, _writeOptions));
        }
        catch
        {
            // Persistence is best-effort; never crash the app on a write failure.
        }
    }
}
