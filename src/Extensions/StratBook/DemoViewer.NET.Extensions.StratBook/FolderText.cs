#region

using System.Text;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Text through an <see cref="IExtensionFolder" />, for the stores that work in a folder the host handed
///     them. The folder deals in bytes, and these files are user-editable: several editors write a UTF-8 byte
///     order mark, <c>Utf8JsonReader</c> treats one as content and throws, and the
///     <see cref="File.ReadAllText(string)" /> these stores read with before silently removed it. Every read
///     here strips it, the <see cref="StoredFile" /> rule, so a file a user touched in an editor still parses.
/// </summary>
internal static class FolderText
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The whole file as text, or null when there is none.</summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    public static string? Read(IExtensionFolder folder, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.Read(relativePath) is { } bytes ? Decode(bytes) : null;
    }

    /// <summary>
    ///     The file's lines with their endings dropped, or nothing when there is no such file. A final newline
    ///     ends the last line rather than starting an empty one, as <see cref="File.ReadAllLines(string)" /> has it.
    /// </summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    public static IReadOnlyList<string> ReadLines(IExtensionFolder folder, string relativePath)
    {
        if (Read(folder, relativePath) is not { Length: > 0 } text)
        {
            return [];
        }

        List<string> lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Replaces the file with <paramref name="text" /> as UTF-8 without a byte order mark.</summary>
    /// <exception cref="IOException">The file could not be written; the previous one stands.</exception>
    public static void Write(IExtensionFolder folder, string relativePath, string text)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(text);
        folder.WriteAtomic(relativePath, Utf8.GetBytes(text));
    }

    private static string Decode(byte[] bytes) =>
        bytes is { Length: >= 3 } && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);
}
