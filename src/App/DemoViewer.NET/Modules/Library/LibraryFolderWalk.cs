#region

using System.Diagnostics;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>One directory's demos and subdirectories, as the library's walk reads them.</summary>
/// <param name="Demos">The <c>*.dem</c> files directly in the directory, canonical path first.</param>
/// <param name="Subdirectories">The directories directly in it that the walk descends into.</param>
internal sealed record LibraryDirectoryListing(
    IReadOnlyList<(string Path, long Size, DateTime Modified)> Demos,
    IReadOnlyList<string> Subdirectories);

/// <summary>The blocking file-system calls the library's folder walk makes, one directory at a time.</summary>
internal interface ILibraryFolderReader
{
    /// <summary>The registered folder's real path.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder is not there.</exception>
    string ResolveRoot(string folder);

    /// <summary>One directory, not recursive.</summary>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory could not be listed.</exception>
    LibraryDirectoryListing Read(string directory);
}

/// <summary>The real file system.</summary>
internal sealed class FileSystemLibraryFolderReader : ILibraryFolderReader
{
    public static FileSystemLibraryFolderReader Instance { get; } = new();

    // IgnoreInaccessible is off on purpose: a directory that cannot be read must fail the read, so the walk
    // records it as unreached instead of listing it as empty.
    private static readonly EnumerationOptions _options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.System,
        ReturnSpecialDirectories = false
    };

    public string ResolveRoot(string folder)
    {
        string root = DemoLibraryService.CanonicalizeDirectory(folder);
        return Directory.Exists(root) ? root : throw new DirectoryNotFoundException(root);
    }

    public LibraryDirectoryListing Read(string directory)
    {
        List<(string, long, DateTime)> demos = [];
        List<string> subdirectories = [];
        foreach (FileSystemInfo item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", _options))
        {
            if (item is DirectoryInfo dir)
            {
                // A directory symlink can loop back on the tree.
                if ((dir.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    subdirectories.Add(dir.FullName);
                }

                continue;
            }

            // "._name.dem" is a macOS AppleDouble sidecar written on SMB, exFAT and NFS copies, not a demo.
            if (!item.Name.EndsWith(".dem", StringComparison.OrdinalIgnoreCase)
                || item.Name.StartsWith("._", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                string canonical = DemoLibraryService.CanonicalizePath(item.FullName);
                FileInfo file = string.Equals(canonical, item.FullName, StringComparison.Ordinal)
                    ? (FileInfo)item
                    : new FileInfo(canonical);
                demos.Add((canonical, file.Length, file.LastWriteTime));
            }
            catch (IOException)
            {
                // vanished mid-listing
            }
        }

        return new LibraryDirectoryListing(demos, subdirectories);
    }
}

/// <summary>
///     One registered folder's walk, a directory at a time. <see cref="Next" /> names the next blocking call,
///     <see cref="Execute" /> makes it and touches no walk state, and <see cref="Complete" /> applies its
///     answer, so a caller may make the call on another thread and give up on it.
/// </summary>
internal sealed class LibraryRootWalk
{
    private readonly List<(string Path, long Size, DateTime Modified)> _demos = [];
    private readonly Stack<string> _pending = new();
    private readonly ILibraryFolderReader _reader;
    private readonly List<(string Directory, string Reason)> _unreachedDirectories = [];
    private readonly Stopwatch _clock = new();

    public LibraryRootWalk(string folder, ILibraryFolderReader reader)
    {
        Folder = folder;
        _reader = reader;
    }

    /// <summary>The folder as registered.</summary>
    public string Folder { get; }

    /// <summary>The folder's real path, once resolved.</summary>
    public string? Root { get; private set; }

    /// <summary>True once nothing is left to read.</summary>
    public bool Done { get; private set; }

    /// <summary>True when the folder is not there at all: a detached volume or a deleted folder.</summary>
    public bool Missing { get; private set; }

    /// <summary>Why the folder as a whole was not reached; null when it was.</summary>
    public string? UnreachedReason { get; private set; }

    /// <summary>True when the whole walk completed, with any unreadable subdirectories listed apart.</summary>
    public bool Reached => Done && UnreachedReason is null;

    public IReadOnlyList<(string Path, long Size, DateTime Modified)> Demos => _demos;

    /// <summary>Subdirectories that could not be read; rows under them are not evidence of anything.</summary>
    public IReadOnlyList<(string Directory, string Reason)> UnreachedDirectories => _unreachedDirectories;

    /// <summary>Wall time from the first call to the walk finishing.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>The next call to make, or null when the walk is done.</summary>
    public Read? Next()
    {
        if (Done)
        {
            return null;
        }

        _clock.Start();
        if (Root is null)
        {
            return new Read(Folder, true);
        }

        if (_pending.TryPop(out string? directory))
        {
            return new Read(directory, false);
        }

        Finish();
        return null;
    }

    /// <summary>Makes the blocking call. Safe on any thread; it reads no walk state.</summary>
    public Answer Execute(Read read)
    {
        try
        {
            return read.ResolvesRoot
                ? new Answer(_reader.ResolveRoot(read.Directory), null, null)
                : new Answer(null, _reader.Read(read.Directory), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever a read throws, the directory was not listed.
            return new Answer(null, null, ex);
        }
    }

    /// <summary>Applies an answer to the read <see cref="Next" /> handed out.</summary>
    public void Complete(Read read, Answer answer)
    {
        if (read.ResolvesRoot)
        {
            if (answer.Root is { } root)
            {
                Root = root;
                _pending.Push(root);
                return;
            }

            Missing = answer.Error is DirectoryNotFoundException;
            GiveUp(Missing ? "the folder is not there" : answer.Error?.Message ?? "it could not be resolved");
            return;
        }

        if (answer.Listing is { } listing)
        {
            _demos.AddRange(listing.Demos);
            foreach (string sub in listing.Subdirectories)
            {
                _pending.Push(sub);
            }

            return;
        }

        string reason = answer.Error?.Message ?? "it could not be listed";
        if (string.Equals(read.Directory, Root, StringComparison.Ordinal))
        {
            GiveUp(reason);
        }
        else
        {
            _unreachedDirectories.Add((read.Directory, reason));
        }
    }

    /// <summary>Ends the walk with the folder as a whole counted as not reached.</summary>
    public void GiveUp(string reason)
    {
        UnreachedReason ??= reason;
        Finish();
    }

    private void Finish()
    {
        Done = true;
        _clock.Stop();
    }

    /// <summary>A blocking call the walk needs: the root's resolution, or one directory's listing.</summary>
    internal sealed record Read(string Directory, bool ResolvesRoot);

    /// <summary>What the call returned, or the error it threw.</summary>
    internal sealed record Answer(string? Root, LibraryDirectoryListing? Listing, Exception? Error);
}

/// <summary>How the library's folder walk waits on the file system.</summary>
/// <param name="SliceBudget">How long one queue slice keeps reading before it yields the lane.</param>
/// <param name="InJobWait">How long a slice waits on one read before it ends and waits outside the queue.</param>
/// <param name="NoAnswer">How long one read may go unanswered before its folder counts as not reached.</param>
internal sealed record LibraryScanTiming(TimeSpan SliceBudget, TimeSpan InJobWait, TimeSpan NoAnswer)
{
    // An automount answers its first read once the mount is up, which takes seconds, not tens of them.
    public static LibraryScanTiming Default { get; } =
        new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(20));
}
