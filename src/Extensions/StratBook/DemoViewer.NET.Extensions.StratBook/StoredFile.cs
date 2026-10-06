#region

using DemoViewer.NET.Extensions.Sdk;

#endregion

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
    ///     The file at <paramref name="relativePath" /> under one of the extension's folders. When it does not
    ///     exist yet and <paramref name="copyFrom" /> returns bytes, those are written there first and read back,
    ///     so data an older build kept elsewhere moves once. The browser build keeps nothing.
    /// </summary>
    /// <param name="storage">The extension's files.</param>
    /// <param name="root">Which of the extension's folders.</param>
    /// <param name="relativePath">The file under that folder.</param>
    /// <param name="copyFrom">Reads the file where an older build kept it; null when there is nothing to copy.</param>
    public static StoredFile In(IExtensionStorage storage, StoreRoot root, string relativePath, Func<byte[]?>? copyFrom = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        return new StoredFile(Path.GetFileName(relativePath), () =>
        {
            byte[]? bytes = storage.ReadAsync(root, relativePath).GetAwaiter().GetResult();
            if (bytes is not null || copyFrom?.Invoke() is not { } legacy)
            {
                return bytes;
            }

            storage.WriteAtomicAsync(root, relativePath, legacy).GetAwaiter().GetResult();
            return legacy;
        }, bytes => storage.WriteAtomicAsync(root, relativePath, bytes).GetAwaiter().GetResult());
    }

    /// <summary>The file's contents, or null when it does not exist.</summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    public byte[]? Read() => _read();

    /// <summary>Replaces the file with <paramref name="content" />.</summary>
    /// <exception cref="IOException">The file could not be written; the previous one stands.</exception>
    public void Write(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _write(content);
    }
}
