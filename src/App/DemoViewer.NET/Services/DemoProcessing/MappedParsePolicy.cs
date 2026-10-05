namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Decides whether a demo file is settled enough to memory-map. A mapped file truncated by another
///     process kills this one, so anything that may still be downloading or copying is read into a byte[].
/// </summary>
public static class MappedParsePolicy
{
    /// <summary>A file written more recently than this may still be in flight.</summary>
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(60);

    public static FileStat StatFile(string path)
    {
        FileInfo info = new(path);
        return new FileStat(info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    ///     True when the last write is at least <see cref="SettleWindow" /> old and a second stat, taken
    ///     just before mapping, still sees the same size and write time.
    /// </summary>
    public static bool IsSettled(string path, TimeProvider time, Func<string, FileStat> stat)
    {
        FileStat first = stat(path);
        if (time.GetUtcNow() - first.LastWriteUtc < SettleWindow)
        {
            return false;
        }

        FileStat second = stat(path);
        return second == first;
    }
}

public readonly record struct FileStat(long Length, DateTimeOffset LastWriteUtc);
