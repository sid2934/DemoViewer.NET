#region

using System.Globalization;
using System.Text.RegularExpressions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.MatchMeta;

/// <summary>
///     The <c>matchmeta</c> record pass: a demo's match time and Valve identity triple, from its file name,
///     the Steam replays folder's <c>&lt;demo&gt;.dem.info</c> beside it, and the file's write time. Reads no
///     demo. Runs once per demo under <see cref="MatchInfoReader.Version" />; what it captured from a
///     <c>.dem.info</c> stays on the record after the demo moves away from it.
/// </summary>
public sealed partial class MatchMetaPass : IRecordPass
{
    /// <summary>The pass id and the stamp it writes.</summary>
    public const string StampId = "matchmeta";

    private readonly Func<string, byte[]?> _readInfo;
    private readonly DemoCacheStore _store;
    private readonly Func<string, DateTime?> _writeTimeUtc;

    /// <param name="store">The demo cache the pass writes into.</param>
    /// <param name="readInfo">A <c>.dem.info</c> file's bytes, or null when there is none. The file system by default.</param>
    /// <param name="writeTimeUtc">A demo's write time, or null when the file is gone. The file system by default.</param>
    public MatchMetaPass(DemoCacheStore store, Func<string, byte[]?>? readInfo = null, Func<string, DateTime?>? writeTimeUtc = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readInfo = readInfo ?? ReadFile;
        _writeTimeUtc = writeTimeUtc ?? WriteTimeOf;
    }

    public string Id => StampId;

    public bool Wants(DemoCacheIndexEntry entry) =>
        entry.NeedsPack(StampId, DemoMatchMeta.CurrentSchema, MatchInfoReader.Version);

    public void Run(DemoCacheRecord record, CancellationToken cancellationToken)
    {
        List<string> paths =
        [
            record.Path,
            .. record.Locations.Select(l => l.Path).Where(p => !string.Equals(p, record.Path, StringComparison.OrdinalIgnoreCase))
        ];
        (ulong Reservation, long Outcome, long Token)? named = null;
        MatchInfo? info = null;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            named ??= ParseName(path);
            info ??= TryReadInfo(path + ".info");
        }

        DateTime? written = _writeTimeUtc(record.Path);
        long now = DateTime.UtcNow.Ticks;
        _store.UpdateExisting(record.Path, r =>
        {
            r.MatchMeta = Resolve(r.MatchMeta, named, info, written ?? FromRecord(r.ModifiedTicks));
            r.SetStamp(new PackStamp(StampId, DemoMatchMeta.CurrentSchema, MatchInfoReader.Version) { ComputedAtTicks = now });
        });
    }

    /// <summary>
    ///     The match meta for a demo from what was found now and what an earlier run captured. The first
    ///     <see cref="MatchTimeSource" /> present wins; on a tie what was found now wins, so a reader fix applies.
    /// </summary>
    /// <param name="previous">What the record holds, or null.</param>
    /// <param name="named">The Valve file name's three numbers, or null when the name is not one.</param>
    /// <param name="info">The decoded <c>.dem.info</c>, or null when none was found.</param>
    /// <param name="written">The demo file's write time, or null when unknown.</param>
    public static DemoMatchMeta Resolve(DemoMatchMeta? previous, (ulong Reservation, long Outcome, long Token)? named, MatchInfo? info,
        DateTime? written)
    {
        (MatchTimeSource source, long? ticks) = info?.MatchTimeUtc is { } played ? (MatchTimeSource.ValveMatchInfo, played.Ticks)
            : written is { } time ? (MatchTimeSource.FileWriteTime, time.Ticks)
            : (MatchTimeSource.Unknown, (long?)null);
        if (previous is not null && previous.MatchTimeSource < source)
        {
            (source, ticks) = (previous.MatchTimeSource, previous.MatchTimeUtcTicks);
        }

        ulong? reservation = info?.ReservationId ?? named?.Reservation;
        return new DemoMatchMeta
        {
            MatchTimeUtcTicks = ticks,
            MatchTimeSource = source,
            ReservationId = reservation?.ToString(CultureInfo.InvariantCulture) ?? previous?.ReservationId,
            OutcomeId = named?.Outcome ?? previous?.OutcomeId,
            Token = named?.Token ?? previous?.Token
        };
    }

    /// <summary>A Valve file name's reservation id, outcome id and token, or null when the name is not one.</summary>
    /// <param name="path">The demo's path or file name.</param>
    public static (ulong Reservation, long Outcome, long Token)? ParseName(string path)
    {
        Match match = ValveName().Match(Path.GetFileName(path));
        return match.Success
               && ulong.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong reservation)
               && long.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long outcome)
               && long.TryParse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long token)
            ? (reservation, outcome, token)
            : null;
    }

    [GeneratedRegex(@"match730_(\d+)_(\d+)_(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ValveName();

    // A missing, unreadable or malformed file is no file: the time falls back to the write time.
    private MatchInfo? TryReadInfo(string path)
    {
        try
        {
            return _readInfo(path) is { } bytes ? MatchInfoReader.Read(bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // The record's write time is in the library's convention, local ticks.
    private static DateTime? FromRecord(long ticks) =>
        ticks > 0 ? new DateTime(ticks, DateTimeKind.Local).ToUniversalTime() : null;

    private static byte[]? ReadFile(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static DateTime? WriteTimeOf(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
}
