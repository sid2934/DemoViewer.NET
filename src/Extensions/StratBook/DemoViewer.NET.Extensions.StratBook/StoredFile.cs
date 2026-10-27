namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     One file a store reads and writes whole: a file of the extension's own folders in the app, or a plain path
///     in tests. A write replaces the file or leaves the previous one. Reads and writes block, so call them off
///     the UI thread where the file is large.
/// </summary>
public sealed class StoredFile
{
    private readonly Func<byte[]?> _read;
    private readonly Action<byte[]> _write;

    private StoredFile(string name, Func<byte[]?> read, Action<byte[]> write)
    {
        Name = name;
        _read = read;
        _write = write;
    }

    /// <summary>The file's name, for messages to the user.</summary>
    public string Name { get; }

    /// <summary>The file at <paramref name="path" />.</summary>
    public static StoredFile At(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new StoredFile(Path.GetFileName(path), () => File.Exists(path) ? File.ReadAllBytes(path) : null,
            bytes => AtomicFile.WriteAllBytes(path, bytes));
    }

    /// <summary>
    ///     The file at <paramref name="relativePath" /> under one of the extension's own folders, or null when
    ///     the host has no folders and handed one out. When the file does not exist yet and
    ///     <paramref name="copyFrom" /> returns bytes, those are written there first and read back, so data an
    ///     older build kept elsewhere moves once.
    /// </summary>
    /// <param name="folder">The extension's own folder, as <see cref="IExtensionStores.Own" /> returned it.</param>
    /// <param name="relativePath">The file under that folder.</param>
    /// <param name="copyFrom">Reads the file where an older build kept it; null when there is nothing to copy.</param>
    public static StoredFile? In(IExtensionFolder? folder, string relativePath, Func<byte[]?>? copyFrom = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        if (folder is null)
        {
            return null;
        }

        return new StoredFile(Path.GetFileName(relativePath), () =>
        {
            byte[]? bytes = folder.Read(relativePath);
            if (bytes is not null || copyFrom?.Invoke() is not { } legacy)
            {
                return bytes;
            }

            folder.WriteAtomic(relativePath, legacy);
            return legacy;
        }, bytes => folder.WriteAtomic(relativePath, bytes));
    }

    /// <summary>
    ///     The one file of a declared store, or null when the host has no folders and handed out no store.
    /// </summary>
    /// <param name="store">The declared store, as <see cref="IExtensionStores.OpenFile" /> returned it.</param>
    public static StoredFile? In(IExtensionFile? store) =>
        store is null ? null : new StoredFile(Path.GetFileName(store.Path), store.Read, bytes => store.WriteAtomic(bytes));

    /// <summary>The file's contents, or null when it does not exist.</summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    public byte[]? Read()
    {
        byte[]? bytes = _read();

        // Strip a UTF-8 BOM. These files are user-editable and several editors write one, and the
        // JSON readers downstream take bytes: Utf8JsonReader treats a BOM as content and throws,
        // where the File.ReadAllText these paths used before silently removed it.
        if (bytes is { Length: >= 3 } && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes[3..];
        }

        return bytes;
    }

    /// <summary>Replaces the file with <paramref name="content" />.</summary>
    /// <exception cref="IOException">The file could not be written; the previous one stands.</exception>
    public void Write(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _write(content);
    }
}
