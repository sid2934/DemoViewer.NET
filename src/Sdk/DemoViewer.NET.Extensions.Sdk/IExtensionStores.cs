namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The stores the extension declared in <see cref="IExtension.Stores" />, each opened as a folder it can
///     work in. A declared store is where the extension keeps data a user is meant to find, or
///     data an older build left where the user has it; <see cref="IExtensionContext.Storage" /> is for
///     everything else. The host resolves each store from its descriptor, so no path the extension builds
///     reaches another extension's files or the app's own.
/// </summary>
public interface IExtensionStores
{
    /// <summary>
    ///     The store declaring one folder that <paramref name="storeId" /> names, as that folder, or null on a
    ///     build with no filesystem. The folder need not exist yet.
    /// </summary>
    /// <param name="storeId">The <see cref="StoreDescriptor.Id" /> the extension declared.</param>
    /// <exception cref="ArgumentException">
    ///     No store of that id was declared, or the store declares several paths or a pattern, which cannot
    ///     be opened as one folder.
    /// </exception>
    /// <exception cref="InvalidOperationException">The store's path is a file; <see cref="OpenFile" /> opens it.</exception>
    IExtensionFolder? Open(string storeId);

    /// <summary>
    ///     The store declaring one file that <paramref name="storeId" /> names, as that file, or null on a build
    ///     with no filesystem. The file need not exist yet.
    /// </summary>
    /// <param name="storeId">The <see cref="StoreDescriptor.Id" /> the extension declared.</param>
    /// <exception cref="ArgumentException">
    ///     No store of that id was declared, or the store declares several paths or a pattern, which cannot
    ///     be opened as one file.
    /// </exception>
    /// <exception cref="InvalidOperationException">The store's path is a folder; <see cref="Open" /> opens it.</exception>
    IExtensionFile? OpenFile(string storeId);
}

/// <summary>
///     The one file of a declared store. A write replaces it whole or leaves the previous one.
/// </summary>
public interface IExtensionFile
{
    /// <summary>Where the file is on disk, for handing to a process or the file manager.</summary>
    string Path { get; }

    /// <summary>True when the file exists.</summary>
    bool Exists { get; }

    /// <summary>When the file was last written, UTC, or null when it does not exist.</summary>
    DateTime? LastWriteTimeUtc { get; }

    /// <summary>The whole file, or null when it does not exist.</summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    byte[]? Read();

    /// <summary>A read-only stream over the file, or null when it does not exist.</summary>
    /// <exception cref="IOException">The file exists and could not be opened.</exception>
    Stream? OpenRead();

    /// <summary>
    ///     Replaces the file with <paramref name="content" />: the bytes go to a temporary file beside it, reach
    ///     the disk, and then take its place. Missing folders are created.
    /// </summary>
    /// <exception cref="IOException">The file could not be written; the previous one stands.</exception>
    void WriteAtomic(ReadOnlySpan<byte> content);

    /// <summary>
    ///     As <see cref="WriteAtomic(System.ReadOnlySpan{byte})" />, with <paramref name="write" /> producing the
    ///     content into the stream it is handed. A throw from it leaves the previous file.
    /// </summary>
    void WriteAtomic(Action<Stream> write);

    /// <summary>Deletes the file. Nothing happens when it does not exist.</summary>
    void Delete();
}

/// <summary>
///     The folder of a declared store. Every path is relative to the folder, with '/' or '\' between
///     segments, and a path that is empty, rooted, carries a ".." segment or resolves outside the folder throws
///     <see cref="ArgumentException" />. Every write replaces the file whole or leaves the previous one, apart
///     from <see cref="Append" />, which adds to the end of a file in place.
/// </summary>
public interface IExtensionFolder
{
    /// <summary>
    ///     Where the folder is on disk, for handing to a process or the file manager; everything else goes
    ///     through the members below.
    /// </summary>
    string Path { get; }

    /// <summary>Creates the folder on disk when it is not there yet, so the user has somewhere to drop files.</summary>
    /// <exception cref="IOException">The folder could not be created.</exception>
    void Create();

    /// <summary>True when <paramref name="relativePath" /> is a file that exists.</summary>
    bool FileExists(string relativePath);

    /// <summary>When <paramref name="relativePath" /> was last written, UTC, or null when there is no such file.</summary>
    DateTime? LastWriteTimeUtc(string relativePath);

    /// <summary>
    ///     The files in the folder at <paramref name="relativePath" /> (empty for this folder) whose names match
    ///     <paramref name="searchPattern" />, as paths relative to this folder, in no particular order. A missing
    ///     folder yields nothing.
    /// </summary>
    /// <param name="relativePath">The folder to list, or empty for this one.</param>
    /// <param name="searchPattern">A name pattern with '*' and '?', such as <c>*.json</c>.</param>
    IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern);

    /// <summary>
    ///     The folders in the folder at <paramref name="relativePath" /> (empty for this folder), as paths
    ///     relative to this folder, in no particular order. A missing folder yields nothing.
    /// </summary>
    /// <param name="relativePath">The folder to list, or empty for this one.</param>
    IEnumerable<string> EnumerateDirectories(string relativePath);

    /// <summary>The whole file at <paramref name="relativePath" />, or null when there is none.</summary>
    /// <exception cref="IOException">The file exists and could not be read.</exception>
    byte[]? Read(string relativePath);

    /// <summary>A read-only stream over the file at <paramref name="relativePath" />, or null when there is none.</summary>
    /// <exception cref="IOException">The file exists and could not be opened.</exception>
    Stream? OpenRead(string relativePath);

    /// <summary>
    ///     Replaces the file at <paramref name="relativePath" /> with <paramref name="content" />: the bytes go to
    ///     a temporary file beside it, reach the disk, and then take its place. Missing folders are created.
    /// </summary>
    /// <exception cref="IOException">The file could not be written; the previous one stands.</exception>
    void WriteAtomic(string relativePath, ReadOnlySpan<byte> content);

    /// <summary>
    ///     As <see cref="WriteAtomic(string, System.ReadOnlySpan{byte})" />, with <paramref name="write" /> producing
    ///     the content into the stream it is handed. A throw from it leaves the previous file.
    /// </summary>
    void WriteAtomic(string relativePath, Action<Stream> write);

    /// <summary>
    ///     Adds <paramref name="line" /> and a '\n' to the end of the file at <paramref name="relativePath" />,
    ///     creating it and its folders when missing. Not atomic: a crash can leave a partial last line, which is
    ///     the trade an append-only log makes.
    /// </summary>
    void Append(string relativePath, string line);

    /// <summary>Moves the file at <paramref name="fromRelativePath" /> to <paramref name="toRelativePath" />, creating missing folders.</summary>
    /// <param name="fromRelativePath">The file to move.</param>
    /// <param name="toRelativePath">Where it goes.</param>
    /// <param name="overwrite">True replaces a file already there; false throws <see cref="IOException" /> instead.</param>
    void Move(string fromRelativePath, string toRelativePath, bool overwrite);

    /// <summary>Deletes the file at <paramref name="relativePath" />. Nothing happens when there is none.</summary>
    void Delete(string relativePath);
}
