#region

using System.Text;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Whole-file writes that a crash cannot tear: the bytes go to a hidden temporary file beside the target,
///     are flushed to the disk, and the file then replaces it. A reader sees the previous file or the new one,
///     never half of either, after a process crash or a power loss alike. Every store of the app that writes a
///     file under the config or cache root writes through here.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Replaces <paramref name="path" /> with <paramref name="content" />. Missing folders are created.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">Its whole new content.</param>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, stream => stream.Write(content));
    }

    /// <summary>Replaces <paramref name="path" /> with <paramref name="content" /> as UTF-8 without a byte order mark.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">Its whole new content.</param>
    public static void WriteAllText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        WriteAllBytes(path, Utf8.GetBytes(content));
    }

    /// <summary>Replaces <paramref name="path" /> with whatever <paramref name="write" /> puts in the stream it is handed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="write">Writes the whole new content. A throw leaves the previous file.</param>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        string temp = TempFor(path);
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                // On disk before the rename: a rename can reach the disk before the data it points at.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>As <see cref="WriteAllBytes" />, writing asynchronously.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">Its whole new content.</param>
    /// <param name="cancellationToken">Stops the write before the file is replaced.</param>
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        string temp = TempFor(path);
        try
        {
            FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // Beside the target, so the move is a rename on the same volume.
    private static string TempFor(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private static void TryDelete(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is harmless; the original failure, if any, is already on its way out.
        }
    }
}
