#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>A demo's first-frame header, what the library shows before the full parse.</summary>
internal sealed record LibraryDemoHeader(string? MapName, string? ServerName, string? DemoVersion);

/// <summary>The blocking header read the library makes for each demo it has not indexed.</summary>
internal interface ILibraryHeaderReader
{
    /// <summary>The demo's header, or null when the file is not a readable CS2 demo.</summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    LibraryDemoHeader? Read(string path);
}

/// <summary>The real file system.</summary>
internal sealed class FileSystemLibraryHeaderReader : ILibraryHeaderReader
{
    public static FileSystemLibraryHeaderReader Instance { get; } = new();

    public LibraryDemoHeader? Read(string path) =>
        DownstreamUtilities.TryReadQuickInfo(path, out DownstreamUtilities.DemoQuickInfo info)
            ? new LibraryDemoHeader(info.MapName, info.ServerName, info.DemoVersion)
            : null;
}
