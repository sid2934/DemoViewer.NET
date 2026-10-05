#region

using System.Text;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Whole-file writes that a crash cannot tear: the bytes go to a hidden temporary file beside the target,
///     which then replaces it. A reader sees the previous file or the new one, never half of either. Every
///     store of the extension that writes a file writes through here.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Replaces <paramref name="path" /> with <paramref name="content" />. Missing folders are created.</summary>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, stream => stream.Write(content));
    }

    /// <summary>Replaces <paramref name="path" /> with <paramref name="content" /> as UTF-8 without a byte order mark.</summary>
    public static void WriteAllText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        WriteAllBytes(path, Utf8.GetBytes(content));
    }

    /// <summary>Replaces <paramref name="path" /> with whatever <paramref name="write" /> puts in the stream. A throw leaves the previous file.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        // Beside the target, so the move is a rename on the same volume.
        string temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
            }

            File.Move(temp, full, overwrite: true);
        }
        finally
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
                // A stray temp file is harmless; the write's own failure, if any, is already on its way out.
            }
        }
    }
}
