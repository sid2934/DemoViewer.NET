using System.IO.Compression;

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     Zips a staged extension directory the same way on every
///     machine: entries sorted ordinal by '/'-separated path, a fixed timestamp and Unix mode on every
///     entry, no directory entries. Two zips of the same tree must hash the same; a creation-time
///     timestamp or a umask-dependent permission bit would make that machine-dependent instead.
/// </summary>
public static class DeterministicZip
{
    // 1980-01-01, the DOS epoch ZipArchiveEntry.LastWriteTime floors to: the earliest value every
    // reader accepts, so it carries no information about when the zip was actually built.
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Regular file, rw-r--r-- (octal 100644), in the upper 16 bits of ExternalAttributes: the layout
    // Info-Zip and System.IO.Compression both read as a Unix mode, independent of the creating
    // process's umask.
    private const int RegularFileMode644 = 0b1000_0001_1010_0100 << 16;

    /// <summary>
    ///     Writes every file under <paramref name="directory" />, recursive, to <paramref name="outputPath" />.
    ///     Overwrites an existing file at <paramref name="outputPath" />.
    /// </summary>
    /// <exception cref="IOException">A reparse point was found; the signed tree may not contain one.</exception>
    public static void Write(string directory, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string root = Path.GetFullPath(directory);
        List<string> relatives = [];
        Walk(root, root, relatives);
        relatives.Sort(StringComparer.Ordinal);

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        using FileStream fileStream = new(outputPath, FileMode.Create, FileAccess.Write);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);
        foreach (string relative in relatives)
        {
            string fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedTimestamp;
            entry.ExternalAttributes = RegularFileMode644;
            using Stream entryStream = entry.Open();
            using FileStream source = File.OpenRead(fullPath);
            source.CopyTo(entryStream);
        }
    }

    // Manual walk, never Directory.EnumerateFiles(..., AllDirectories): the same reparse-point hazard
    // ExtensionSignature.Walk guards against, repeated here rather than shared, since the two walks
    // keep different files (this one keeps extension.sig, the digest walk skips it).
    private static void Walk(string dir, string root, List<string> relatives)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*"))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            string relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"'{relative}' is a link; the zipped tree may not contain one.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Walk(entry, root, relatives);
                continue;
            }

            relatives.Add(relative);
        }
    }
}
