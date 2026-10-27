#region

using System.Text;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The host's <see cref="IExtensionStores" />: each store the pack declares, resolved against the root its
///     descriptor names, and the extension's own two folders. The declared list is
///     <see cref="PackContributions.AcceptedStores" />, so what is handed out is what the pack may also delete.
/// </summary>
internal sealed class ExtensionStores : IExtensionStores
{
    private readonly string _extensionId;
    private readonly string? _configRoot;
    private readonly string? _cacheRoot;
    private readonly Lazy<ExtensionFolder?> _ownCache;

    /// <param name="extensionId">The extension the stores belong to.</param>
    /// <param name="configRoot">The config root, or null for a build with no filesystem.</param>
    /// <param name="cacheRoot">The cache root, or null for a build with no filesystem.</param>
    /// <param name="declared">The extension's stores, every refused one already dropped.</param>
    public ExtensionStores(string extensionId, string? configRoot, string? cacheRoot, IReadOnlyList<StoreDescriptor> declared)
    {
        _extensionId = extensionId;
        _configRoot = configRoot;
        _cacheRoot = cacheRoot;
        Declared = declared;
        _ownCache = new Lazy<ExtensionFolder?>(OpenOwnCache);
    }

    /// <summary>The stores, in declaration order.</summary>
    public IReadOnlyList<StoreDescriptor> Declared { get; }

    public IExtensionFolder? Open(string storeId)
    {
        if (Resolve(storeId) is not { } full)
        {
            return null;
        }

        return File.Exists(full)
            ? throw new InvalidOperationException($"Store '{storeId}' of '{_extensionId}' is a file; OpenFile opens it.")
            : new ExtensionFolder(full);
    }

    public IExtensionFile? OpenFile(string storeId)
    {
        if (Resolve(storeId) is not { } full)
        {
            return null;
        }

        return Directory.Exists(full)
            ? throw new InvalidOperationException($"Store '{storeId}' of '{_extensionId}' is a folder; Open opens it.")
            : new ExtensionFile(full);
    }

    public IExtensionFolder? Own(StoreRoot root)
    {
        if (root == StoreRoot.Cache)
        {
            return _ownCache.Value;
        }

        return _configRoot is { } config ? new ExtensionFolder(OwnFolder(config)) : null;
    }

    // The cache side is handed out through a Lazy because opening it runs the move below, and that must
    // happen before any read of the folder: a pack that read it first would find nothing, fall back to the
    // layout an older build used, and write a copy the move then declines to overwrite.
    private ExtensionFolder? OpenOwnCache()
    {
        if (_cacheRoot is not { } cache)
        {
            return null;
        }

        string own = OwnFolder(cache);
        string files = System.IO.Path.Combine(own, ExtensionFolders.OwnCacheDirectoryName);
        MoveIntoFilesFolder(own, files);
        return new ExtensionFolder(files);
    }

    private string OwnFolder(string root) =>
        System.IO.Path.Combine(root, ExtensionFolders.DataDirectoryName, ExtensionFolders.SafeName(_extensionId));

    // Builds through v0.9.0 handed the extension its cache folder itself, so what they wrote sits beside the
    // per-demo data the host keeps there. Every such entry moves down into the folder handed out now, except
    // one whose target is already taken: that is the newer copy and overwriting it would lose user work. A
    // sweep rather than a one-shot, so a run a crash cut short finishes on the next one.
    private void MoveIntoFilesFolder(string own, string files)
    {
        if (!Directory.Exists(own))
        {
            return;
        }

        string[] entries;
        try
        {
            // The whole listing up front, not an enumerator: the loop renames entries out of this folder and
            // creates one inside it, and readdir under concurrent modification may skip an entry. A skipped
            // lineups file is the loss this move exists to prevent.
            entries = Directory.GetFileSystemEntries(own);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Swallowed rather than thrown: the folder is handed out through a Lazy, which would cache the
            // throw and fail every later Own(Cache) inside the extension's own factories.
            NotMoved(own, e);
            return;
        }

        foreach (string entry in entries)
        {
            string name = System.IO.Path.GetFileName(entry);
            if (string.Equals(name, ExtensionDemoDataStore.DirectoryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ExtensionFolders.OwnCacheDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = System.IO.Path.Combine(files, name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(files);
                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, target);
                }
                else
                {
                    File.Move(entry, target);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                NotMoved(name, e);
            }
        }
    }

    private void NotMoved(string entry, Exception e) =>
        AppLog.ExtensionCacheEntryNotMoved(DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory), _extensionId, entry, e.Message);

    // The one path a store declares, under its root; null when the build has no roots.
    private string? Resolve(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        if (_configRoot is null && _cacheRoot is null)
        {
            return null;
        }

        StoreDescriptor store = Declared.FirstOrDefault(s => string.Equals(s.Id, storeId, StringComparison.Ordinal))
                                ?? throw new ArgumentException($"'{_extensionId}' declares no store '{storeId}'.", nameof(storeId));
        if (store.Paths.Count != 1 || store.Paths[0].Contains('*', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Store '{storeId}' declares {store.Paths.Count} paths or a pattern; only a store of one file or folder opens.", nameof(storeId));
        }

        if ((store.Root == StoreRoot.Config ? _configRoot : _cacheRoot) is not { } root)
        {
            return null;
        }

        return PackDataRemover.ResolveSafe(root, store.Paths[0])
               ?? throw new InvalidOperationException($"Store '{storeId}' of '{_extensionId}' names a path outside its root.");
    }
}

/// <summary>
///     A store's folder. Writes go through <see cref="AtomicFile" />, so a crash leaves the previous file;
///     <see cref="Append" /> is the one member that writes in place.
/// </summary>
internal sealed class ExtensionFolder : IExtensionFolder
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _root;

    /// <param name="root">The folder, absolute.</param>
    public ExtensionFolder(string root)
    {
        _root = System.IO.Path.GetFullPath(root);
    }

    public string Path => _root;

    public void Create() => Directory.CreateDirectory(_root);

    public bool FileExists(string relativePath) => File.Exists(ResolveFile(relativePath));

    public bool DirectoryExists(string relativePath) => Directory.Exists(ResolveFolder(relativePath));

    public void SetLastWriteTimeUtc(string relativePath, DateTime value)
    {
        string path = ResolveFile(relativePath);
        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, value);
        }
    }

    public DateTime? LastWriteTimeUtc(string relativePath)
    {
        string path = ResolveFile(relativePath);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern)
    {
        ArgumentNullException.ThrowIfNull(searchPattern);
        string folder = ResolveFolder(relativePath);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, searchPattern).Select(f => System.IO.Path.GetRelativePath(_root, f))
            : [];
    }

    public IEnumerable<string> EnumerateDirectories(string relativePath)
    {
        string folder = ResolveFolder(relativePath);
        return Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder).Select(d => System.IO.Path.GetRelativePath(_root, d))
            : [];
    }

    public byte[]? Read(string relativePath)
    {
        string path = ResolveFile(relativePath);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public Stream? OpenRead(string relativePath)
    {
        string path = ResolveFile(relativePath);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content) => AtomicFile.WriteAllBytes(ResolveFile(relativePath), content.ToArray());

    public void WriteAtomic(string relativePath, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        AtomicFile.Write(ResolveFile(relativePath), write);
    }

    public void Append(string relativePath, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string path = ResolveFile(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // Opened for append and never rewritten. A log whose last line was torn by a crash gets a
        // newline first, so the torn line stays alone and is skipped rather than swallowing this one.
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Utf8.GetBytes((EndsTorn(path, stream.Position) ? "\n" : "") + line + "\n");
        stream.Write(bytes);
        stream.Flush(true);
    }

    public void Move(string fromRelativePath, string toRelativePath, bool overwrite)
    {
        string to = ResolveFile(toRelativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
        File.Move(ResolveFile(fromRelativePath), to, overwrite);
    }

    public void Delete(string relativePath)
    {
        string path = ResolveFile(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // The read handle must be FileShare.ReadWrite: the append handle above is still open with
    // FileShare.Read, and anything narrower here fails the whole append with an IOException.
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

    // A file's path inside the folder.
    private string ResolveFile(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return PackDataRemover.ResolveSafe(_root, relativePath)
               ?? throw new ArgumentException($"'{relativePath}' is not a file inside the store.", nameof(relativePath));
    }

    // A folder's path: the folder itself for an empty path, otherwise one inside it.
    private string ResolveFolder(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0 || relativePath == ".")
        {
            return _root;
        }

        return PackDataRemover.ResolveSafe(_root, relativePath)
               ?? throw new ArgumentException($"'{relativePath}' is not a folder inside the store.", nameof(relativePath));
    }
}

/// <summary>A store's one file. Writes go through <see cref="AtomicFile" />, so a crash leaves the previous file.</summary>
internal sealed class ExtensionFile : IExtensionFile
{
    /// <param name="path">The file, absolute.</param>
    public ExtensionFile(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public DateTime? LastWriteTimeUtc => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;

    public byte[]? Read() => File.Exists(Path) ? File.ReadAllBytes(Path) : null;

    public Stream? OpenRead() => File.Exists(Path) ? new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;

    public void WriteAtomic(ReadOnlySpan<byte> content) => AtomicFile.WriteAllBytes(Path, content.ToArray());

    public void WriteAtomic(Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        AtomicFile.Write(Path, write);
    }

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
