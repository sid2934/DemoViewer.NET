namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>How the last write of a demo's facet ended.</summary>
public enum DemoDataState
{
    /// <summary>Not written yet, or marked for a rebuild.</summary>
    Pending,

    /// <summary>Written. Current when its schema and fingerprint match what the extension expects.</summary>
    Written,

    /// <summary>The extension's work on the demo failed. Kept out of the backlog until the extension clears it.</summary>
    Failed
}

/// <summary>
///     What the store knows about one facet of one demo, from its own index: reading a stamp opens no file.
/// </summary>
/// <param name="DemoPath">
///     A path of the demo: where it was when the facet was last touched, or another path the library has it at
///     once that one left. Not the facet's key; <paramref name="Sha256" /> is.
/// </param>
/// <param name="Sha256">The demo's content hash, the facet's key, or null when the library did not know it yet.</param>
/// <param name="Facet">The facet.</param>
/// <param name="Schema">The payload shape the facet was written at; 0 when it never was.</param>
/// <param name="Fingerprint">What the facet's freshness keys on, or null when it was marked for a rebuild.</param>
/// <param name="State">How the last write ended.</param>
/// <param name="WrittenAtTicks">When the facet was last written, in UTC ticks; 0 when it never was.</param>
/// <param name="Count">The extension's own count for the facet (rows, items), or 0.</param>
public sealed record DemoDataStamp(
    string DemoPath,
    string? Sha256,
    string Facet,
    int Schema,
    string? Fingerprint,
    DemoDataState State,
    long WrittenAtTicks,
    int Count)
{
    /// <summary>True when the facet was written at <paramref name="schema" /> under <paramref name="fingerprint" />.</summary>
    /// <param name="schema">The payload shape the extension reads.</param>
    /// <param name="fingerprint">What the extension would write now.</param>
    public bool IsCurrent(int schema, string? fingerprint) =>
        State == DemoDataState.Written && Schema == schema && string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal);
}

/// <summary>One facet of one demo, as written.</summary>
/// <param name="Facet">
///     The facet: a name for one kind of per-demo data, such as <c>"rounds"</c>. Letters, digits, '.', '-' and
///     '_' only.
/// </param>
/// <param name="Schema">The payload's shape. A reader asking for another schema reads the facet as absent.</param>
/// <param name="Fingerprint">
///     What the payload was computed from (a ruleset identity, a detector version). A reader asking for another
///     fingerprint reads the facet as absent.
/// </param>
/// <param name="Payload">The facet's main content.</param>
public sealed record DemoDataWrite(string Facet, int Schema, string? Fingerprint, ReadOnlyMemory<byte> Payload)
{
    /// <summary>
    ///     Further named contents under the same stamp, such as a large secondary table. They are written before
    ///     <see cref="Payload" />, and the stamp after both, so a crash part way leaves the facet absent rather
    ///     than half written. Part names follow the facet name rule.
    /// </summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Parts { get; init; } =
        new Dictionary<string, ReadOnlyMemory<byte>>();

    /// <summary>The extension's own count for the facet, kept on the stamp.</summary>
    public int Count { get; init; }
}

/// <summary>One facet's content as read back, with the header it was written under.</summary>
/// <param name="Schema">The schema it was written at.</param>
/// <param name="Fingerprint">The fingerprint it was written under.</param>
/// <param name="ExtensionVersion">The extension's version that wrote it.</param>
/// <param name="WrittenAtTicks">When it was written, in UTC ticks.</param>
/// <param name="Content">The payload, or the part asked for.</param>
public sealed record DemoDataRecord(int Schema, string? Fingerprint, string? ExtensionVersion, long WrittenAtTicks, byte[] Content);

/// <summary>
///     The extension's per-demo data: content the extension computes for a demo and can always compute again,
///     kept by the host in the extension's own folder. Each facet of a demo carries a header (the schema,
///     the fingerprint, the extension's version and the demo's content hash), and the store keeps an index of
///     stamps so the extension can tell what is current without opening a file.
///     <para>
///         Data is keyed by the demo's content (<see cref="LibraryDemo.Sha256" />), never by its path. Every
///         method takes a path only to find the content the library has there. The store keeps to these rules:
///     </para>
///     <list type="bullet">
///         <item>
///             Every path in a demo's <see cref="LibraryDemo.Locations" /> reads and writes the same facet, so a
///             moved or renamed demo, a copy, or a second mount of the same share keeps its data.
///         </item>
///         <item>
///             A facet written before the library hashed the demo is held under its path, and is the content's
///             once the hash is known.
///         </item>
///         <item>
///             A path the library matched to a known demo without reading it in full reads as absent, and
///             <see cref="Stamp" /> and <see cref="Stamps" /> leave it out, until a full read confirms its bytes.
///             A path the library has found holding other bytes reads the data of those bytes, and the old
///             content keeps its own.
///         </item>
///         <item>
///             A hashed demo whose last path leaves the library, its file deleted or its folder removed or
///             offline, keeps its data while the library keeps the demo, and reads it again once a path to the
///             same bytes is back. The data goes when the library lets the demo go, once no folder has listed its
///             bytes for the grace period the user sets (two weeks by default), or with <see cref="Delete" />.
///             Nothing deletes one demo's data sooner. A demo that left before it was hashed loses its data at once.
///         </item>
///     </list>
///     <para>
///         Reads and writes touch the disk: call them from a pass, a job or another queue thread, never the UI
///         thread. <see cref="Stamp" /> and <see cref="Stamps" /> read the index only and are safe anywhere.
///         The browser build has no store: <see cref="IsAvailable" /> is false, reads find nothing and writes
///         keep nothing.
///     </para>
/// </summary>
public interface IExtensionDemoData
{
    /// <summary>False in the browser build, where nothing is kept.</summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     The stamp of <paramref name="facet" /> for the demo at <paramref name="demoPath" />, or null when there is
    ///     none or the path is only matched to known content by fingerprint and no full read has confirmed it yet.
    /// </summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    DemoDataStamp? Stamp(string demoPath, string facet);

    /// <summary>
    ///     Every stamp of <paramref name="facet" /> held at a confirmed path. A stamp whose demo is seen only at a
    ///     path matched by fingerprint is left out until a full read confirms that path.
    /// </summary>
    /// <param name="facet">The facet.</param>
    IReadOnlyList<DemoDataStamp> Stamps(string facet);

    /// <summary>
    ///     The facet's content when it was written at <paramref name="schema" /> under
    ///     <paramref name="fingerprint" /> for this demo's content; null otherwise, as if it were absent.
    /// </summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    /// <param name="schema">The shape the caller reads.</param>
    /// <param name="fingerprint">What the caller would write now.</param>
    /// <param name="part">A part named in <see cref="DemoDataWrite.Parts" />, or null for the main payload.</param>
    byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null);

    /// <summary>
    ///     The facet's content whatever schema and fingerprint it was written under, with its header, or null
    ///     when there is none for this demo's content. For showing old content while a rebuild is pending.
    /// </summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    /// <param name="part">A part named in <see cref="DemoDataWrite.Parts" />, or null for the main payload.</param>
    DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null);

    /// <summary>
    ///     Writes a facet of the demo whole, replacing what was there, and stamps it <see cref="DemoDataState.Written" />.
    ///     Returns the new stamp, or null when nothing is kept (the browser build).
    /// </summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="write">What to write.</param>
    /// <exception cref="IOException">The disk refused the write; the previous content and stamp stay.</exception>
    DemoDataStamp? Write(string demoPath, DemoDataWrite write);

    /// <summary>Marks the facet <see cref="DemoDataState.Failed" />, keeping whatever content it had.</summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    void MarkFailed(string demoPath, string facet);

    /// <summary>Turns a <see cref="DemoDataState.Failed" /> facet back to <see cref="DemoDataState.Pending" />. Nothing otherwise.</summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    void ClearFailed(string demoPath, string facet);

    /// <summary>
    ///     Marks the facet of every demo, or of <paramref name="demoPath" /> alone, for a rebuild: the stamp
    ///     loses its fingerprint and a failure turns pending. The content stays readable through
    ///     <see cref="ReadAny" /> until it is written again.
    /// </summary>
    /// <param name="facet">The facet.</param>
    /// <param name="demoPath">One demo, or null for every demo.</param>
    void Invalidate(string facet, string? demoPath = null);

    /// <summary>Sets the extension's count on the facet's stamp, adding a pending stamp when there is none.</summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    /// <param name="count">The new count.</param>
    void SetCount(string demoPath, string facet, int count);

    /// <summary>Removes the facet of the demo, content and stamp.</summary>
    /// <param name="demoPath">Any path of the demo.</param>
    /// <param name="facet">The facet.</param>
    void Delete(string demoPath, string facet);

    /// <summary>
    ///     Raised on the UI thread after stamps changed: with the demo's path, or with null when many demos
    ///     changed at once (a rebuild, a delete of the extension's data).
    /// </summary>
    event Action<string?>? Changed;
}
