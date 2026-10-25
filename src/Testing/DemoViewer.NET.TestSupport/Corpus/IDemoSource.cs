namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     Somewhere a corpus demo's bytes can be copied from: a local directory, a mounted network
///     share, a UNC path, and later a cloud bucket.
///     <para>
///         <b>A source only copies bytes. It never decides identity.</b> The layer names the
///         destination, hashes what arrives, compares it against the manifest and moves the result
///         into place. A source that could skip verification would make a wrong demo look right,
///         so the contract keeps hashing out of the implementation's reach.
///     </para>
///     <para>
///         <b>A source is never listed.</b> It is probed for one named file at a time. The mount at
///         <c>//192.168.1.7/Games</c> has previously appeared EMPTY at the wrong moment, so
///         "source unavailable" must never be readable as "source empty": no caller can observe a
///         zero-length listing and conclude a demo is gone.
///     </para>
///     <para><b>No source may link.</b> Copy only, never a symlink, junction or hard link.</para>
/// </summary>
public interface IDemoSource
{
    /// <summary>
    ///     How this source names itself in a skip message. It is read by a developer looking at a
    ///     failed resolution, so it should be the configured location verbatim.
    /// </summary>
    string Description { get; }

    /// <summary>
    ///     Copies <paramref name="demo" />'s bytes to <paramref name="destination" />, which the
    ///     caller owns and has already made a parent directory for.
    ///     <para>
    ///         Returns false when this source does not hold the demo, which is an ordinary outcome
    ///         and moves resolution on to the next source. Throws when the source holds it and the
    ///         copy failed, which the layer records against this source by name.
    ///     </para>
    /// </summary>
    bool TryCopyTo(CorpusDemo demo, string destination);
}
