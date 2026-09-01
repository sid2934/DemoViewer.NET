#region

using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Test helper: an empty demo-library service backed by a throwaway temp-path JSON, so constructing
///     <c>MainViewModel</c> in tests never loads the developer's real <c>%AppData%/DemoViewer.NET/library.json</c>
///     (which would trigger a background full-parse of their real demo folders, a multi-minute "hang").
/// </summary>
public static class TestLibraries
{
    public static DemoLibraryService Empty() =>
        new(null, Path.Combine(
            Path.GetTempPath(), "dvlib_test_" + Guid.NewGuid().ToString("N") + ".json"));

    /// <summary>
    ///     An empty library with a single entry pointing at <paramref name="filePath" />, enough to render one
    ///     demo card and exercise "the library has a demo" logic (e.g. the walkthrough gateway targeting the
    ///     first library card). The file should exist on disk if the code under test guards on <c>File.Exists</c>.
    /// </summary>
    public static DemoLibraryService WithEntry(string filePath)
    {
        DemoLibraryService lib = Empty();
        lib.Entries.Add(new DemoEntry
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Directory = Path.GetDirectoryName(filePath) ?? string.Empty,
            FileSizeBytes = 0,
            Modified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        return lib;
    }
}
