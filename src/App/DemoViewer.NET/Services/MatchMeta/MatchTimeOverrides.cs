#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.MatchMeta;

/// <summary>
///     The user's own match time for a demo: <see cref="MatchTimeSource.UserOverride" /> on the record's
///     <see cref="DemoMatchMeta" />. The override lives on the record, so it follows the row's content key
///     when the row re-keys and joins another path of the same bytes; <see cref="MatchMetaPass.Resolve" />
///     never replaces it.
/// </summary>
public static class MatchTimeOverrides
{
    /// <summary>The format the hero's field reads and shows, local time.</summary>
    public const string InputFormat = "yyyy-MM-dd HH:mm";

    /// <summary>Sets the match time of the demo at <paramref name="path" />.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    /// <param name="utc">When the match was played.</param>
    public static void Set(DemoCacheStore store, string path, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(store);
        long ticks = utc.ToUniversalTime().Ticks;
        store.UpdateExisting(path, r =>
        {
            r.MatchMeta ??= new DemoMatchMeta();
            r.MatchMeta.MatchTimeUtcTicks = ticks;
            r.MatchMeta.MatchTimeSource = MatchTimeSource.UserOverride;
        });
    }

    /// <summary>Drops the override of the demo at <paramref name="path" /> and dates it from its own sources again.</summary>
    /// <param name="store">The demo cache.</param>
    /// <param name="path">The demo's path.</param>
    public static void Clear(DemoCacheStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.UpdateExisting(path, r =>
        {
            if (r.MatchMeta is not { MatchTimeSource: MatchTimeSource.UserOverride } meta)
            {
                return;
            }

            meta.MatchTimeUtcTicks = null;
            meta.MatchTimeSource = MatchTimeSource.Unknown;
            r.PackStamps.RemoveAll(s => s.Id == MatchMetaPass.StampId);
        });
        if (store.TryLoadRecord(path, false) is { } record)
        {
            new MatchMetaPass(store).Run(record, CancellationToken.None);
        }
    }

    /// <summary>Reads a local date and time as typed in the hero's field.</summary>
    /// <param name="text">The text, <see cref="InputFormat" /> or a date alone.</param>
    /// <param name="utc">The same moment in UTC.</param>
    public static bool TryParse(string? text, out DateTime utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(text?.Trim(), [InputFormat, "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        return true;
    }

    /// <summary>A match time as the hero's field shows it, local time.</summary>
    /// <param name="utcTicks">The time, UTC ticks.</param>
    public static string Format(long utcTicks) =>
        new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString(InputFormat, CultureInfo.InvariantCulture);
}
