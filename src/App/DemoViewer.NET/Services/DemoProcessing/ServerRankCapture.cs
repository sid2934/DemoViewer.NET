#region

using System.Collections.Concurrent;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>One account's row of a <c>CCSUsrMsg_ServerRankUpdate</c>, joined to its SteamID64.</summary>
/// <param name="SteamId64">The account id plus <see cref="ServerRankCapture.SteamId64Base" />.</param>
/// <param name="RankOld">The rank before the match; 0 for an unranked account.</param>
/// <param name="RankNew">The rank after the match; 0 for an unranked account.</param>
/// <param name="RankChange">What the update says moved.</param>
/// <param name="Wins">Wins after the match.</param>
/// <param name="RankType">The rank system: 11 premier, 12 competitive.</param>
public sealed record ServerRankUpdate(ulong SteamId64, int RankOld, int RankNew, float RankChange, int Wins, int RankType);

/// <summary>
///     The rank update a Valve server sends once at the end of a matchmaking demo (net message 352). The engine
///     has no decoder for it, so a read reports it through <see cref="ParseOptions.OnUnknownMessage" /> with no
///     bytes: the capture flags the frame, holds it when the read yields it, and reads its payload back from
///     the file afterwards. A few KB per demo.
/// </summary>
public sealed class ServerRankCapture
{
    /// <summary><c>CS_UM_ServerRankUpdate</c>.</summary>
    public const int MessageType = 352;

    /// <summary>What a Valve account id is offset by to make a SteamID64.</summary>
    public const ulong SteamId64Base = 76561197960265728UL;

    private readonly ConcurrentDictionary<int, byte> _flagged = new();
    private readonly List<DemoFrame> _held = [];
    private readonly Func<DemoFrame, byte[]> _payloadOf;

    /// <param name="payloadOf">A held frame's decompressed payload.</param>
    public ServerRankCapture(Func<DemoFrame, byte[]> payloadOf)
    {
        _payloadOf = payloadOf ?? throw new ArgumentNullException(nameof(payloadOf));
    }

    /// <summary>A capture reading payloads back out of the demo's bytes.</summary>
    public static ServerRankCapture Over(byte[] demo) => new(frame => DownstreamUtilities.GetDecompressedPayload(frame, demo));

    /// <summary>A capture reading payloads back from the file at <paramref name="path" />, one seek per held frame.</summary>
    public static ServerRankCapture OverFile(string path) => new(frame =>
    {
        byte[] raw = new byte[frame.PayloadLength];
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Position = frame.PayloadStart;
        file.ReadExactly(raw);
        return frame.IsCompressed ? Snappier.Snappy.DecompressToArray(raw) : raw;
    });

    /// <summary><paramref name="options" /> reporting 352 to this capture, beside any callback they already carry.</summary>
    public ParseOptions Attach(ParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Action<UnknownMessageInfo>? inner = options.OnUnknownMessage;
        return options with
        {
            OnUnknownMessage = info =>
            {
                inner?.Invoke(info);
                if (info.TypeId == MessageType)
                {
                    _flagged[info.FrameNumber] = 0;
                }
            }
        };
    }

    /// <summary>
    ///     Holds <paramref name="frame" /> when it carried a 352. The decode worker reports a frame before the
    ///     read yields it.
    /// </summary>
    public void Observe(DemoFrame frame)
    {
        if (!_flagged.IsEmpty && _flagged.ContainsKey(frame.FrameNumber))
        {
            _held.Add(frame);
        }
    }

    /// <summary>The rows of every held update, in the order the demo sent them.</summary>
    public IReadOnlyList<ServerRankUpdate> Decode()
    {
        List<ServerRankUpdate> rows = [];
        foreach (DemoFrame frame in _held)
        {
            foreach (DownstreamUtilities.InnerMessageSlice slice in DownstreamUtilities.ExtractInnerMessageSlices(frame, _payloadOf(frame)))
            {
                if (slice.TypeId == MessageType)
                {
                    rows.AddRange(Rows(CCSUsrMsg_ServerRankUpdate.Parser.ParseFrom(slice.Bytes)));
                }
            }
        }

        return rows;
    }

    /// <summary>A decoded update's rows, each account joined to its SteamID64.</summary>
    public static IEnumerable<ServerRankUpdate> Rows(CCSUsrMsg_ServerRankUpdate message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.RankUpdate.Select(u => new ServerRankUpdate(SteamId64Base + (uint)u.AccountId, u.RankOld, u.RankNew,
            u.RankChange, u.NumWins, u.RankTypeId));
    }
}
