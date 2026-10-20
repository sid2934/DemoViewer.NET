namespace DemoViewer.NET.Services.MatchMeta;

/// <summary>
///     What a Steam replay's <c>&lt;demo&gt;.dem.info</c> says about its match: a
///     <c>CDataGCCStrike15_v2_MatchInfo</c>, read by field number because no generated type for it ships.
///     <para>
///         <see cref="ReservationId" />, <see cref="TeamScores" /> and <see cref="MatchResult" /> come from the
///         last round-stats entry: Valve writes the reservation id on the final round only.
///     </para>
/// </summary>
/// <param name="MatchTimeUtc">When the match was played (<c>matchtime</c>), or null when the file has none.</param>
/// <param name="ReservationId">The matchmaking reservation, the file name's first number, or null.</param>
/// <param name="TeamScores">The final score, in the file's team order, or empty.</param>
/// <param name="MatchResult">Valve's match result code from the last round, or null.</param>
public sealed record MatchInfo(DateTime? MatchTimeUtc, ulong? ReservationId, IReadOnlyList<int> TeamScores, int? MatchResult);

/// <summary>
///     A protobuf wire walk over <c>CDataGCCStrike15_v2_MatchInfo</c>: <c>matchtime</c> (2) at the top, and
///     per <c>roundstatsall</c> entry (5) <c>reservationid</c> (1), <c>match_result</c> (11) and
///     <c>team_scores</c> (12). Every other field is skipped by its wire type.
/// </summary>
public static class MatchInfoReader
{
    /// <summary>The reader's version: the <c>matchmeta</c> stamp's fingerprint, so a reader change re-reads every demo.</summary>
    public const string Version = "mi1";

    private const int MatchTimeField = 2;
    private const int RoundStatsAllField = 5;
    private const int ReservationIdField = 1;
    private const int MatchResultField = 11;
    private const int TeamScoresField = 12;

    /// <summary>Decodes a <c>.dem.info</c> file's bytes.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a well-formed protobuf message.</exception>
    public static MatchInfo Read(ReadOnlySpan<byte> bytes)
    {
        DateTime? time = null;
        ulong? reservation = null;
        int? result = null;
        List<int> scores = [];
        int at = 0;
        while (at < bytes.Length)
        {
            (int field, int wire) = Key(bytes, ref at);
            if (field == MatchTimeField && wire == 0)
            {
                ulong seconds = Varint(bytes, ref at);
                time = seconds > 0 ? DateTime.UnixEpoch.AddSeconds(seconds) : null;
            }
            else if (field == RoundStatsAllField && wire == 2)
            {
                ReadOnlySpan<byte> round = Length(bytes, ref at);
                (ulong? roundReservation, int? roundResult, List<int> roundScores) = RoundStats(round);
                reservation = roundReservation ?? reservation;
                result = roundResult ?? result;
                if (roundScores.Count > 0)
                {
                    scores = roundScores;
                }
            }
            else
            {
                Skip(bytes, ref at, wire);
            }
        }

        return new MatchInfo(time, reservation, scores, result);
    }

    private static (ulong? Reservation, int? Result, List<int> Scores) RoundStats(ReadOnlySpan<byte> bytes)
    {
        ulong? reservation = null;
        int? result = null;
        List<int> scores = [];
        int at = 0;
        while (at < bytes.Length)
        {
            (int field, int wire) = Key(bytes, ref at);
            switch (field, wire)
            {
                case (ReservationIdField, 0):
                    reservation = Varint(bytes, ref at);
                    break;
                case (MatchResultField, 0):
                    result = (int)Varint(bytes, ref at);
                    break;
                case (TeamScoresField, 0):
                    scores.Add((int)Varint(bytes, ref at));
                    break;
                case (TeamScoresField, 2):
                    // Packed: proto3 writers pack repeated scalars, Valve's proto2 writer does not.
                    ReadOnlySpan<byte> packed = Length(bytes, ref at);
                    for (int p = 0; p < packed.Length;)
                    {
                        scores.Add((int)Varint(packed, ref p));
                    }

                    break;
                default:
                    Skip(bytes, ref at, wire);
                    break;
            }
        }

        return (reservation, result, scores);
    }

    private static (int Field, int Wire) Key(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong key = Varint(bytes, ref at);
        int field = (int)(key >> 3);
        if (field == 0)
        {
            throw new InvalidDataException("field number 0 at offset " + at);
        }

        return (field, (int)(key & 7));
    }

    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (at >= bytes.Length)
            {
                throw new InvalidDataException("varint runs past the end");
            }

            byte b = bytes[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }

        throw new InvalidDataException("varint longer than 10 bytes");
    }

    private static ReadOnlySpan<byte> Length(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong length = Varint(bytes, ref at);
        if (length > (ulong)(bytes.Length - at))
        {
            throw new InvalidDataException("length-delimited field runs past the end");
        }

        ReadOnlySpan<byte> slice = bytes.Slice(at, (int)length);
        at += (int)length;
        return slice;
    }

    private static void Skip(ReadOnlySpan<byte> bytes, ref int at, int wire)
    {
        switch (wire)
        {
            case 0:
                Varint(bytes, ref at);
                break;
            case 1:
                Advance(bytes, ref at, 8);
                break;
            case 2:
                Length(bytes, ref at);
                break;
            case 5:
                Advance(bytes, ref at, 4);
                break;
            default:
                throw new InvalidDataException("unsupported wire type " + wire);
        }
    }

    private static void Advance(ReadOnlySpan<byte> bytes, ref int at, int count)
    {
        if (count > bytes.Length - at)
        {
            throw new InvalidDataException("fixed-width field runs past the end");
        }

        at += count;
    }
}
