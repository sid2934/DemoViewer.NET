namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     Each demo's grenade rows, kept as the Strat Book's per-demo data: one facet whose payload is the throw
///     log and whose fingerprint is the walker version, the row count on the stamp. A demo is current when its
///     stamp says it was written by the walker in force; the stamp is read from the store's index, so asking
///     opens no file. A file that does not decode, or names another demo's hash, reads as absent.
/// </summary>
public sealed class GrenadeStore
{
    /// <summary>The facet's name in the per-demo data.</summary>
    public const string Facet = "grenades";

    /// <summary>The rows' shape. A bump re-walks every demo's grenades and nothing else.</summary>
    public const int Schema = GrenadeSidecar.CurrentSchema;

    /// <param name="data">The extension's per-demo data.</param>
    public GrenadeStore(IExtensionDemoData data)
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

    /// <summary>A demo's stamp, or null when it was never walked.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public DemoDataStamp? Stamp(string demoPath) => Data.Stamp(demoPath, Facet);

    /// <summary>Every demo's stamp.</summary>
    public IReadOnlyList<DemoDataStamp> Stamps() => Data.Stamps(Facet);

    /// <summary>Are the demo's rows current under the walker in force?</summary>
    /// <param name="demoPath">The demo's path.</param>
    public bool IsCurrent(string demoPath) => Stamp(demoPath)?.IsCurrent(Schema, GrenadeWalker.Version) ?? false;

    /// <summary>Does the demo want a walk? A failed demo does not: retry is the user's call.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public bool Needs(string demoPath) => !IsFailed(demoPath) && !IsCurrent(demoPath);

    /// <summary>True when the demo's last walk failed.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public bool IsFailed(string demoPath) => Stamp(demoPath) is { State: DemoDataState.Failed };

    /// <summary>Writes a demo's rows as its throw log and stamps them current. Throws on an I/O failure.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="rows">The rows.</param>
    public void Write(string demoPath, GrenadeDocument rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Data.Write(demoPath, new DemoDataWrite(Facet, Schema, GrenadeWalker.Version, GrenadeThrowLog.Encode(rows))
        {
            Count = rows.Grenades.Count
        });
    }

    /// <summary>
    ///     A demo's rows, or null when the stamp does not say they are current, they do not decode, or they name
    ///     another demo's hash than <paramref name="sha256" />.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="sha256">The library's hash of the demo, or null when it has none.</param>
    public GrenadeDocument? TryReadRows(string demoPath, string? sha256) =>
        IsCurrent(demoPath)
        && Data.Read(demoPath, Facet, Schema, GrenadeWalker.Version) is { } bytes
        && GrenadeThrowLog.TryDecode(bytes) is { SchemaVersion: Schema } document
        && GrenadeSidecar.SameDemo(sha256, document.Demo.Sha256)
            ? document
            : null;

    /// <summary>Marks the demo's walk failed; it leaves the backlog until the user retries it.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void MarkFailed(string demoPath) => Data.MarkFailed(demoPath, Facet);

    /// <summary>Lifts a failed demo back to pending.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public void ClearFailed(string demoPath) => Data.ClearFailed(demoPath, Facet);
}
