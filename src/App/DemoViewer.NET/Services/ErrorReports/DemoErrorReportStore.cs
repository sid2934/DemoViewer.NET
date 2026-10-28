#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.ErrorReports;

/// <summary>
///     Reads and writes a demo's <see cref="DemoErrorReport" /> on its cache record, the way
///     <see cref="MatchMeta.MatchTimeOverrides" /> does for a user's match time. One report per demo: opening
///     the window loads the existing one rather than starting a second.
///     <para>
///         <b>Storing in the cache has a consequence worth knowing.</b> Clearing the cache, or the orphan
///         sweep expiring a demo the user deleted, takes the report with it. That is the price of following
///         the demo's content key, which is what makes a report survive the file being moved or re-indexed.
///     </para>
/// </summary>
public static class DemoErrorReportStore
{
    /// <summary>The demo's report, or null when it has none or the stored one is at a newer schema.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    public static DemoErrorReport? TryLoad(DemoCacheStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.TryLoadRecord(path, false)?.ErrorReport is not { } report)
        {
            return null;
        }

        // A payload a newer app wrote is not ours to show or to overwrite, the rule every sidecar reader here
        // follows.
        return report.Schema > DemoErrorReport.CurrentSchema ? null : report;
    }

    /// <summary>True when the demo already carries a report this app can read.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    public static bool Exists(DemoCacheStore store, string path) => TryLoad(store, path) is not null;

    /// <summary>
    ///     Writes the demo's report, creating the record when the demo is not yet cached
    ///     (<see cref="DemoCacheStore.UpdateExisting" /> makes a fresh one rather than dropping the write).
    ///     <see cref="DemoErrorReport.CreatedUtcTicks" /> is kept from the stored report so an edit does not
    ///     re-date it.
    /// </summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    /// <param name="notes">What the user wrote.</param>
    /// <param name="snapshot">The diagnostic snapshot taken for this save.</param>
    /// <param name="nowUtc">The save time.</param>
    public static void Save(DemoCacheStore store, string path, string notes, DemoErrorReportSnapshot snapshot,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);
        long ticks = nowUtc.ToUniversalTime().Ticks;
        store.UpdateExisting(path, r =>
        {
            if (r.ErrorReport is { } existing && existing.Schema > DemoErrorReport.CurrentSchema)
            {
                return;
            }

            long created = r.ErrorReport is { CreatedUtcTicks: > 0 } prior ? prior.CreatedUtcTicks : ticks;
            r.ErrorReport = new DemoErrorReport
            {
                Schema = DemoErrorReport.CurrentSchema,
                Notes = notes,
                CreatedUtcTicks = created,
                UpdatedUtcTicks = ticks,
                SubmittedUtcTicks = r.ErrorReport?.SubmittedUtcTicks,
                Snapshot = snapshot
            };
        });
    }

    /// <summary>Drops the demo's report.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    public static void Delete(DemoCacheStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.UpdateExisting(path, r => r.ErrorReport = null);
    }
}
