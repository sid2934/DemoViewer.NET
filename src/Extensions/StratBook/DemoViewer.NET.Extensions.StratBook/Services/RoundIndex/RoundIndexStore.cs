#region

using System.Text;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>
///     Each demo's round index and its positions, kept as the Strat Book's per-demo data: one facet whose main
///     payload is the <see cref="RoundIndexDocument" /> and whose <see cref="PositionsPart" /> is the
///     <see cref="RoundPositionsDocument" />, both under one stamp. The stamp says whether a demo's index is
///     current for its map's fingerprint; it is read from the store's index, so asking opens no file.
///     <para>
///         The host writes the positions part first, the index second and the stamp last, so a crash between
///         any two leaves "not indexed", never an index whose cards have nothing to draw. A file that does not
///         parse reads as absent and its demo is indexed again. A demo that leaves the library loses its data.
///     </para>
/// </summary>
public sealed class RoundIndexStore
{
    /// <summary>The facet's name in the per-demo data.</summary>
    public const string Facet = "round-index";

    /// <summary>The positions part's name.</summary>
    public const string PositionsPart = "positions";

    /// <summary>The index document's shape. Part of the stamp and of the fingerprint, so a bump re-indexes alone.</summary>
    public const int Schema = 1;

    /// <param name="data">The extension's per-demo data.</param>
    public RoundIndexStore(IExtensionDemoData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    /// <summary>The per-demo data the facet lives in.</summary>
    public IExtensionDemoData Data { get; }

    /// <summary>Raised on the UI thread when a demo's stamp moved, with its path, or null for many.</summary>
    public event Action<string?>? Changed
    {
        add => Data.Changed += value;
        remove => Data.Changed -= value;
    }

    /// <summary>A demo's stamp, or null when it was never indexed.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public DemoDataStamp? Stamp(string demoPath) => Data.Stamp(demoPath, Facet);

    /// <summary>Every demo's stamp.</summary>
    public IReadOnlyList<DemoDataStamp> Stamps() => Data.Stamps(Facet);

    /// <summary>Is the demo's index current under <paramref name="fingerprint" />?</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="fingerprint">The fingerprint in force for the demo's map.</param>
    public bool IsCurrent(string demoPath, string fingerprint) => Stamp(demoPath)?.IsCurrent(Schema, fingerprint) ?? false;

    /// <summary>Does the demo want indexing? A failed demo does not: retry is the user's call.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="fingerprint">The fingerprint in force for the demo's map.</param>
    public bool Needs(string demoPath, string fingerprint) =>
        Stamp(demoPath) is not { State: DemoDataState.Failed } && !IsCurrent(demoPath, fingerprint);

    /// <summary>True when the demo's last index failed.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public bool IsFailed(string demoPath) => Stamp(demoPath) is { State: DemoDataState.Failed };

    /// <summary>When the demo's index was written (UTC ticks), or 0: the Watched Situations watermark.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public long ComputedAtTicks(string demoPath) => Stamp(demoPath)?.WrittenAtTicks ?? 0;

    /// <summary>
    ///     Writes a demo's index and positions under <paramref name="fingerprint" /> and stamps them, the row count
    ///     on the stamp. Throws on an I/O failure: the evaluator marks the demo failed from it.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="document">The index.</param>
    /// <param name="positions">The positions the cards draw.</param>
    /// <param name="fingerprint">The fingerprint the index was built under.</param>
    /// <returns>The new stamp, or null when nothing is kept.</returns>
    public DemoDataStamp? Write(string demoPath, RoundIndexDocument document, RoundPositionsDocument positions, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(positions);
        return Data.Write(demoPath, new DemoDataWrite(Facet, Schema, fingerprint, Encoding.UTF8.GetBytes(document.Serialize()))
        {
            Parts = new Dictionary<string, ReadOnlyMemory<byte>> { [PositionsPart] = positions.SerializeUtf8() },
            Count = document.RowCount
        });
    }

    /// <summary>A demo's index whatever fingerprint it was built under, or null when there is none or it does not parse.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public RoundIndexDocument? TryRead(string demoPath) =>
        TryReadText(demoPath) is { } json ? RoundIndexDocument.TryDeserialize(json) : null;

    /// <summary>A demo's index text, or null when there is none.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public string? TryReadText(string demoPath) =>
        Data.ReadAny(demoPath, Facet) is { Schema: Schema } record ? Encoding.UTF8.GetString(record.Content) : null;

    /// <summary>
    ///     A demo's positions, or null when there are none, they do not parse, they were built under another
    ///     fingerprint than <paramref name="expectedFingerprint" />, or they name another demo's hash than
    ///     <paramref name="sha256" />. Stale positions read as absent, so a card shows the placeholder rather than
    ///     positions the current rows were not built from.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="expectedFingerprint">The fingerprint in force, or null to accept any.</param>
    /// <param name="sha256">The record's hash, or null when neither side has one.</param>
    public RoundPositionsDocument? TryReadPositions(string demoPath, string? expectedFingerprint = null, string? sha256 = null)
    {
        if (Data.ReadAny(demoPath, Facet, PositionsPart) is not { Schema: Schema } record
            || RoundPositionsDocument.TryDeserialize(record.Content) is not { } positions)
        {
            return null;
        }

        if (expectedFingerprint is not null && !string.Equals(positions.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        if (sha256 is not null && positions.Demo.Sha256 is { } written && !string.Equals(written, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return positions;
    }

    /// <summary>Marks the demo's index failed; it leaves the backlog until the user retries it.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void MarkFailed(string demoPath) => Data.MarkFailed(demoPath, Facet);

    /// <summary>Lifts a failed demo back to pending.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void ClearFailed(string demoPath) => Data.ClearFailed(demoPath, Facet);

    /// <summary>Marks every demo's index for a rebuild; the old rows keep answering until each is rebuilt.</summary>
    public void InvalidateAll() => Data.Invalidate(Facet);

    /// <summary>Forgets a demo's index and positions.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void Delete(string demoPath) => Data.Delete(demoPath, Facet);
}
