#region

using System.Text;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The host's <see cref="IExtensionStores" />: each store the pack declares, resolved against the root its
///     descriptor names. The list is <see cref="PackContributions.AcceptedStores" />, so what is handed out is
///     what the pack may also delete.
/// </summary>
internal sealed class ExtensionStores : IExtensionStores
{
    private readonly string _extensionId;
    private readonly string? _configRoot;
    private readonly string? _cacheRoot;

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
        using StreamWriter writer = new(path, append: true, Utf8);
        writer.Write(line);
        writer.Write('\n');
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
