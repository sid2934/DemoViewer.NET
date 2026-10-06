#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>The reads the library makes to recognise a demo by its bytes.</summary>
internal interface ILibraryContentReader
{
    /// <summary>
    ///     The whole file's content hash and the fingerprint from the same read, as
    ///     <see cref="DemoContentFingerprint.TryComputeWithContentHash" /> takes them.
    /// </summary>
    (string? Sha256, DemoContentFingerprint? Fingerprint) HashAndFingerprint(string path, TimeProvider time);

    /// <summary>The fingerprint alone, as <see cref="DemoContentFingerprint.TryCompute" /> takes it.</summary>
    DemoContentFingerprint? Fingerprint(string path, TimeProvider time);
}

/// <summary>The real file system, with the default fingerprint window.</summary>
internal sealed class FileSystemLibraryContentReader : ILibraryContentReader
{
    public static FileSystemLibraryContentReader Instance { get; } = new();

    public (string? Sha256, DemoContentFingerprint? Fingerprint) HashAndFingerprint(string path, TimeProvider time) =>
        DemoContentFingerprint.TryComputeWithContentHash(path, time);

    public DemoContentFingerprint? Fingerprint(string path, TimeProvider time) =>
        DemoContentFingerprint.TryCompute(path, time);
}
