namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     A directory tree reachable through the filesystem. That covers a local folder, a mounted
///     network share and a UNC path, which is the shape the repo already uses for the
///     <c>cs2-assets/</c> cache staged off a NAS share: configure a path, copy bytes down, keep the
///     copy local and gitignored.
///     <para>
///         Two candidate paths per demo, probed with <see cref="File.Exists" /> and nothing else:
///         <c>&lt;root&gt;/&lt;dir&gt;/&lt;file&gt;</c> then <c>&lt;root&gt;/&lt;file&gt;</c>. The
///         directory is never enumerated, so an unavailable mount can never present itself as an
///         empty corpus.
///     </para>
/// </summary>
public sealed class DirectoryDemoSource : IDemoSource
{
    private readonly string _root;

    /// <param name="root">The directory the corpus is laid out under. A <c>file://</c> URI is accepted.</param>
    public DirectoryDemoSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Normalize(root);
    }

    /// <inheritdoc />
    public string Description => _root;

    /// <inheritdoc />
    public bool TryCopyTo(CorpusDemo demo, string destination)
    {
        string? found = Candidates(demo).FirstOrDefault(File.Exists);
        if (found is null)
        {
            return false;
        }

        // Copy, never link: removing a tree that links demos/ deletes the link's target.
        File.Copy(found, destination, true);
        return true;
    }

    /// <summary>
    ///     Turns one configured source string into a source. A <c>file://</c> URI or a plain path
    ///     yields a <see cref="DirectoryDemoSource" />; any other scheme throws, because silently
    ///     ignoring a configured source would read as "nothing to fetch from" and skip tests that
    ///     the configuration says should run.
    /// </summary>
    public static IDemoSource FromSpecifier(string specifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specifier);
        string trimmed = specifier.Trim();
        int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && !trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            // The SCHEME only, never the entry: an entry like s3://bucket/x?token=... would put a
            // credential into a message that is printed for every corpus test in the run.
            throw new NotSupportedException(
                $"a demo source entry uses the unsupported scheme '{trimmed[..scheme]}'. Only a " +
                "directory path, a file:// URI or an http(s) FileBrowser instance is implemented.");
        }

        return new DirectoryDemoSource(trimmed);
    }

    private IEnumerable<string> Candidates(CorpusDemo demo)
    {
        if (demo.Dir is not null)
        {
            yield return Path.Combine(_root, demo.Dir, demo.File);
        }

        yield return Path.Combine(_root, demo.File);
    }

    private static string Normalize(string root)
    {
        string trimmed = root.Trim();
        return trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
               && Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
               && uri.IsFile
            ? uri.LocalPath
            : trimmed;
    }
}
