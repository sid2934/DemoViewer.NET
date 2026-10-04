#region

using System.IO.Compression;
using System.Security.Cryptography;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     The filesystem half of <see cref="ExtensionUpdateService" /> (strat-book-plugin.md §7.10): the paths
///     under <c>&lt;config root&gt;/extensions/</c>, the zip checks, the bounded extraction and the deletes.
///     Everything it writes sits under <c>extensions/.staging/</c> until the final rename; everything it
///     deletes sits under <c>extensions/</c>, and a reparse point is never followed, the rule
///     <c>PackDataRemover</c> and <see cref="Loading.ExtensionLoader" /> apply.
/// </summary>
public static class ExtensionStaging
{
    /// <summary>The folder under <c>extensions/</c> that holds downloads in progress. The loader skips dot folders.</summary>
    public const string StagingDirectoryName = ".staging";

    /// <summary>A zip with more entries than this is refused.</summary>
    public const int MaxEntries = 4096;

    /// <summary>A zip whose entries would unpack to more than this, or whose download is larger, is refused.</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    /// <summary><c>&lt;extensions&gt;/.staging</c>.</summary>
    public static string StagingRoot(string extensionsDirectory) => Path.Combine(extensionsDirectory, StagingDirectoryName);

    /// <summary><c>&lt;extensions&gt;/.staging/&lt;id&gt;/&lt;version&gt;.zip.part</c>.</summary>
    public static string PartPath(string extensionsDirectory, string id, SemVersion version) =>
        Path.Combine(StagingRoot(extensionsDirectory), id, version + ".zip.part");

    /// <summary><c>&lt;extensions&gt;/.staging/&lt;id&gt;/&lt;version&gt;/</c>, where the zip is unpacked and judged.</summary>
    public static string ExtractPath(string extensionsDirectory, string id, SemVersion version) =>
        Path.Combine(StagingRoot(extensionsDirectory), id, version.ToString());

    /// <summary><c>&lt;extensions&gt;/&lt;id&gt;/&lt;version&gt;/</c>, the directory the loader reads.</summary>
    public static string InstalledPath(string extensionsDirectory, string id, SemVersion version) =>
        Path.Combine(extensionsDirectory, id, version.ToString());

    /// <summary>
    ///     Checks a finished download against what the feed promised: its length first, then its SHA-256.
    ///     Null when both match, else the reason in user terms.
    /// </summary>
    public static string? VerifyDownload(string partPath, long expectedSize, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(partPath);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        long length = new FileInfo(partPath).Length;
        if (length != expectedSize)
        {
            return $"the download is {length} bytes; the feed said {expectedSize}";
        }

        string actual;
        using (FileStream stream = new(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "the download does not match the feed's checksum";
    }

    /// <summary>
    ///     Unpacks <paramref name="zipPath" /> into <paramref name="targetDirectory" />, which must not exist
    ///     yet. Refuses, before writing anything, a zip with more than <see cref="MaxEntries" /> entries or
    ///     more than <see cref="MaxBytes" /> of content, and any entry whose name is rooted, carries a
    ///     <c>..</c> or empty segment, or resolves outside the target. Null on success, else the reason; the
    ///     target is removed on any refusal or error.
    /// </summary>
    public static string? Extract(string zipPath, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(zipPath);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        string target = Path.GetFullPath(targetDirectory);
        string targetWithSeparator = target.EndsWith(Path.DirectorySeparatorChar) ? target : target + Path.DirectorySeparatorChar;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > MaxEntries)
            {
                return $"the archive has {archive.Entries.Count} entries; the most allowed is {MaxEntries}";
            }

            long total = 0;
            List<(ZipArchiveEntry Entry, string Path, bool IsDirectory)> plan = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length < 0 || (total += entry.Length) > MaxBytes)
                {
                    return "the archive unpacks to more than the allowed size";
                }

                string shortName = Path.GetFileName(entry.FullName.TrimEnd('/', '\\'));
                if (IsUnixSymlink(entry))
                {
                    return $"the archive entry '{shortName}' is a link";
                }

                string? relative = SafeRelativePath(entry.FullName);
                if (relative is null)
                {
                    return $"the archive entry '{shortName}' has an unsafe path";
                }

                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                string full = Path.GetFullPath(Path.Combine(target, relative));
                if (!full.StartsWith(targetWithSeparator, StringComparison.Ordinal))
                {
                    return $"the archive entry '{Path.GetFileName(relative)}' resolves outside the extension folder";
                }

                // Two names that differ only by case are one file on macOS and Windows; which one wins
                // would depend on the order, so neither does.
                if (!seen.Add(relative))
                {
                    return $"the archive has two entries named '{Path.GetFileName(relative)}' differing only by case";
                }

                plan.Add((entry, full, isDirectory));
            }

            Directory.CreateDirectory(target);
            foreach ((ZipArchiveEntry entry, string full, bool isDirectory) in plan)
            {
                if (isDirectory)
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using Stream source = entry.Open();
                using FileStream destination = new(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                CopyBounded(source, destination, entry.Length);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            DeleteTree(targetDirectory);
            return "the archive could not be unpacked: " + ex.GetType().Name;
        }
    }

    /// <summary>
    ///     Reads the <c>extension.json</c> at the root of <paramref name="directory" /> and checks it names
    ///     <paramref name="id" /> and <paramref name="version" /> and that the assembly it names is beside
    ///     it. The manifest when every check passes; else null with the reason.
    /// </summary>
    public static ExtensionManifest? ReadStagedManifest(string directory, string id, SemVersion version, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string path = Path.Combine(directory, ExtensionManifest.FileName);
        if (!File.Exists(path))
        {
            problem = $"the archive has no {ExtensionManifest.FileName} at its root";
            return null;
        }

        ExtensionManifest manifest;
        try
        {
            manifest = ExtensionManifest.Parse(File.ReadAllText(path));
        }
        catch (ExtensionManifestException ex)
        {
            problem = $"the archive's {ExtensionManifest.FileName} is invalid: {ex.Message}";
            return null;
        }

        if (!string.Equals(manifest.Id, id, StringComparison.Ordinal) || manifest.Version != version)
        {
            problem = $"the archive is {manifest.Id} {manifest.Version}, not {id} {version}";
            return null;
        }

        if (!File.Exists(Path.Combine(directory, manifest.Assembly)))
        {
            problem = $"the archive has no '{manifest.Assembly}'";
            return null;
        }

        problem = null;
        return manifest;
    }

    /// <summary>
    ///     Removes <paramref name="directory" /> and everything under it, never following a reparse point and
    ///     never removing a <c>.dem</c> file. A file that will not delete is left, and the folder with it.
    /// </summary>
    public static void DeleteTree(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (IsReparsePoint(directory))
        {
            return;
        }

        if (File.Exists(directory))
        {
            TryDeleteFile(directory);
            return;
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            DeleteTree(child);
        }

        try
        {
            Directory.Delete(directory, false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     True when <paramref name="path" /> sits strictly under <paramref name="root" /> and is not a
    ///     reparse point; the guard every delete and the final rename run behind.
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        if (IsReparsePoint(path))
        {
            return false;
        }

        string rootFull = Path.GetFullPath(root);
        string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && full.Length > rootWithSeparator.Length;
    }

    // The high 16 bits of a Unix-made entry's attributes carry the st_mode; type bits 0xA000 are a symlink.
    // ZipArchive writes the link's target text as a file, so this is defence in depth, not a path.
    private static bool IsUnixSymlink(ZipArchiveEntry entry) => (((uint)entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    // Both separators are split on: a zip written on Windows may carry backslashes, and on Unix a
    // backslash is a legal file-name character, which is exactly how ".." hides.
    private static string? SafeRelativePath(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || Path.IsPathRooted(entryName) || entryName.Contains(':', StringComparison.Ordinal)
            || entryName.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        string[] segments = entryName.Split('/', '\\');
        List<string> kept = [];
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0 && i == segments.Length - 1)
            {
                continue; // a directory entry's trailing separator
            }

            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Trim().Length == 0)
            {
                return null;
            }

            kept.Add(segment);
        }

        return kept.Count == 0 ? null : Path.Combine(kept.ToArray());
    }

    // The entry's declared length was counted against MaxBytes; a stream that yields more is lying.
    private static void CopyBounded(Stream source, Stream destination, long declaredLength)
    {
        byte[] buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > declaredLength)
            {
                throw new InvalidDataException("the entry is longer than it declares");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (path.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
