#region

using System.Globalization;
using CS2DemoKit.Parser;
using Google.Protobuf;
using Snappier;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>What one emitted candidate file turned out to be.</summary>
internal sealed class TrimResult
{
    public required string Path { get; init; }
    public required TrimVariant Variant { get; init; }
    public required TrimWindow Window { get; init; }

    /// <summary>Source frame indices copied into the output, in emission order (excludes the tail frames).</summary>
    public required IReadOnlyList<int> EmittedSourceFrames { get; init; }

    public required long BytesWritten { get; init; }
    public required int SetupFrameCount { get; init; }
    public required int WindowFrameCount { get; init; }
    public required int DroppedFrameCount { get; init; }
    public required int RewrittenFrameCount { get; init; }
    public required StripStats Strip { get; init; }
    public required string FileInfoBefore { get; init; }
    public required string FileInfoAfter { get; init; }

    /// <summary>The player_team seatings synthesized into the output (empty = none injected).</summary>
    public IReadOnlyList<TeamSample> SynthesizedTeams { get; init; } = [];

    /// <summary>
    ///     Rewritten frames whose stripped payload did NOT get smaller under Snappy and were therefore
    ///     written uncompressed. Every frame in both reference demos' packet streams is compressed, so a
    ///     non-zero count means the output mixes compressed and uncompressed frames — a confound when a
    ///     candidate is tested in CS2.
    /// </summary>
    public int LeftUncompressed { get; set; }

    /// <summary>Encoder-identity tally over the window's packets — only populated for rewriting variants.</summary>
    public int IdentityExact { get; set; }
    public int IdentityShorter { get; set; }
    public int IdentityMismatch { get; set; }
    public int IdentityFirstDivergentFrame { get; set; } = -1;
}

/// <summary>
///     Emits a trimmed <c>.dem</c> file.
///     <para>
///         Output layout mirrors the source exactly: 16-byte file header, then setup frames, then the
///         contiguous retained window, then the tail (<c>DEM_SpawnGroups</c>, <c>DEM_FileInfo</c>) that
///         the file header's two offsets point at.
///     </para>
///     <para>
///         Frame ticks are <b>not</b> rebased. A checkpoint-entry trim therefore starts at the source's
///         absolute tick (e.g. 5 000), exactly as the retained frames' payloads believe. Rebasing the
///         header ticks alone would contradict the tick values embedded in the payloads.
///     </para>
/// </summary>
internal static class DemoTrimWriter
{
    /// <summary>Builds one candidate file.</summary>
    /// <param name="demo">Parsed source demo.</param>
    /// <param name="raw">Raw source bytes (the same buffer the parse was made from).</param>
    /// <param name="window">Retained frame range.</param>
    /// <param name="variant">Which rung of the ladder to emit.</param>
    /// <param name="outputPath">Destination file.</param>
    /// <param name="checkEncoderIdentity">
    ///     When true (and the variant rewrites payloads), every packet is additionally re-encoded with an
    ///     empty drop set and compared to the original — the gate that separates a broken bit writer from
    ///     a legitimately-broken strip.
    /// </param>
    /// <param name="teamPacketPayload">
    ///     Optional pre-built <c>CDemoPacket</c> payload carrying the synthesized <c>player_team</c>
    ///     seatings (see <see cref="TeamEventSynthesizer" />); injected as one uncompressed
    ///     <c>DEM_Packet</c> before the first packet frame that follows the game-event-list frame.
    ///     Null = no injection.
    /// </param>
    /// <param name="teamSamples">The seatings behind <paramref name="teamPacketPayload" /> — recorded on the result for verification.</param>
    /// <param name="teamPacketAfterFrameIndex">
    ///     Source frame index of the <c>CMsgSource1LegacyGameEventList</c> — the injected packet must
    ///     land after it or a sequential reader decodes the events schemaless (fields lost).
    /// </param>
    public static TrimResult Write(
        ParsedDemo demo, byte[] raw, TrimWindow window, TrimVariant variant,
        string outputPath, bool checkEncoderIdentity = true,
        byte[]? teamPacketPayload = null, IReadOnlyList<TeamSample>? teamSamples = null,
        int teamPacketAfterFrameIndex = -1)
    {
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        // DEM_Stop / DEM_SpawnGroups / DEM_FileInfo live AFTER DEM_Stop and so never appear in
        // demo.Frames — they are read straight out of the raw bytes (see DemoTail).
        DemoTail tail = DemoTail.Read(raw, demo);

        List<int> setup = [];
        if (window.EnteredAtCheckpoint)
        {
            for (int i = 0; i < window.EntryIndex; i++)
            {
                // Everything that is NOT part of the delta-encoded game stream: DEM_FileHeader,
                // DEM_ClassInfo, DEM_SendTables, DEM_StringTables, the DEM_SignonPacket run,
                // DEM_SyncTick, DEM_CustomDataCallbacks, ... The dropped stream frames are exactly the
                // ones the DEM_FullPacket checkpoint at EntryIndex restores state for.
                if (!DemoFormat.StreamFrameCommands.Contains(frames[i].Command))
                {
                    setup.Add(i);
                }
            }
        }

        List<int> windowFrames = [];
        int droppedFrames = 0;
        for (int i = window.EntryIndex; i <= window.EndIndex; i++)
        {
            if (variant.DroppedFrameCommands.Contains(frames[i].Command))
            {
                droppedFrames++;
                continue;
            }

            windowFrames.Add(i);
        }

        List<int> emitted = [.. setup, .. windowFrames];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        StripStats strip = default;
        int rewritten = 0, leftUncompressed = 0;
        int identityExact = 0, identityShorter = 0, identityMismatch = 0, firstDivergentFrame = -1;
        int spawnGroupsOffset = 0, fileInfoOffset;
        string fileInfoBefore = "(none)", fileInfoAfter = "(none)";

        using (FileStream fs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            DemoFormat.WriteFileHeader(fs, 0, 0); // patched at the end

            // The synthesized player_team packet goes immediately BEFORE the first packet frame that
            // FOLLOWS the game-event-list frame — a sequential reader loads the event schema at the
            // list, so an earlier position decodes the events schemaless (name kept, fields lost;
            // measured on the pro demo, whose animation stream frames precede the signon's list).
            // Stamped with the following frame's own tick so the tick sequence stays monotone. See
            // TeamEventSynthesizer for why the events must be synthesized at all.
            bool teamPacketPending = teamPacketPayload is { Length: > 0 };

            foreach (int i in emitted)
            {
                DemoFrame frame = frames[i];
                if (teamPacketPending
                    && i > teamPacketAfterFrameIndex
                    && frame.Command is "DEM_Packet" or "DEM_FullPacket")
                {
                    DemoFormat.WriteFrame(fs, DemoFormat.CommandIdByName["DEM_Packet"], frame.ServerTick,
                        false, teamPacketPayload);
                    teamPacketPending = false;
                }

                if (variant.RewritesPayloads && IsPacketFrame(frame.Command))
                {
                    byte[] decompressed = DownstreamUtilities.GetDecompressedPayload(frame, raw);
                    if (checkEncoderIdentity)
                    {
                        AccumulateIdentity(frame, decompressed, i,
                            ref identityExact, ref identityShorter, ref identityMismatch, ref firstDivergentFrame);
                    }

                    byte[] newPayload = StripFramePayload(frame, decompressed, variant.StrippedInnerTypeIds,
                        out StripStats frameStrip);
                    strip += frameStrip;
                    rewritten++;
                    if (!WritePayloadFrame(fs, frame, newPayload))
                    {
                        leftUncompressed++;
                    }
                }
                else
                {
                    // Verbatim: header varints + (possibly compressed) payload, byte for byte.
                    fs.Write(raw, frame.RawStart, frame.RawLength);
                }
            }

            // Tail, in the source's own order: DEM_Stop, DEM_SpawnGroups, DEM_FileInfo. All three are
            // re-headered at the trim's last tick — the source copies carry the FULL demo's final tick,
            // which would leave the file claiming a tick range its frames no longer cover.
            if (tail.Stop is { } stop)
            {
                DemoFormat.WriteFrame(fs, stop.CommandId, window.EndTick, false, ReadOnlySpan<byte>.Empty);
            }

            if (tail.SpawnGroups is { } spawnGroups)
            {
                spawnGroupsOffset = (int)fs.Position;
                DemoFormat.WriteFrame(fs, spawnGroups.CommandId, window.EndTick, spawnGroups.Compressed,
                    raw.AsSpan(spawnGroups.PayloadStart, spawnGroups.PayloadLength));
            }

            fileInfoOffset = (int)fs.Position;
            WriteFileInfo(fs, demo, raw, tail, emitted, frames, window, out fileInfoBefore, out fileInfoAfter);

            fs.Flush();
            fs.Position = 0;
            DemoFormat.WriteFileHeader(fs, fileInfoOffset, spawnGroupsOffset);
        }

        return new TrimResult
        {
            Path = outputPath,
            Variant = variant,
            Window = window,
            EmittedSourceFrames = emitted,
            BytesWritten = new FileInfo(outputPath).Length,
            SetupFrameCount = setup.Count,
            WindowFrameCount = windowFrames.Count,
            DroppedFrameCount = droppedFrames,
            RewrittenFrameCount = rewritten,
            Strip = strip,
            FileInfoBefore = fileInfoBefore,
            FileInfoAfter = fileInfoAfter,
            LeftUncompressed = leftUncompressed,
            IdentityExact = identityExact,
            IdentityShorter = identityShorter,
            IdentityMismatch = identityMismatch,
            IdentityFirstDivergentFrame = firstDivergentFrame,
            SynthesizedTeams = teamSamples ?? []
        };
    }

    /// <summary>
    ///     Frames whose payload carries an inner-message bitstream. <c>DEM_SignonPacket</c> is
    ///     deliberately excluded: it is the initial-state path, it is tiny, and there is no size upside
    ///     to rewriting it — leaving it verbatim removes it as a suspect if a candidate fails in CS2.
    /// </summary>
    private static bool IsPacketFrame(string command) =>
        command is "DEM_Packet" or "DEM_FullPacket";

    private static void AccumulateIdentity(
        DemoFrame frame, byte[] decompressed, int frameIndex,
        ref int exact, ref int shorter, ref int mismatch, ref int firstDivergentFrame)
    {
        ByteString? data = ExtractPacketData(frame, decompressed);
        if (data is null)
        {
            return;
        }

        switch (PacketRewriter.CheckEncoderIdentity(data.Span, out _))
        {
            case IdentityOutcome.Exact:
                exact++;
                break;
            case IdentityOutcome.ExactPrefixShorter:
                shorter++;
                break;
            default:
                mismatch++;
                if (firstDivergentFrame < 0)
                {
                    firstDivergentFrame = frameIndex;
                }

                break;
        }
    }

    private static ByteString? ExtractPacketData(DemoFrame frame, byte[] decompressed) =>
        string.Equals(frame.Command, "DEM_FullPacket", StringComparison.Ordinal)
            ? CDemoFullPacket.Parser.ParseFrom(decompressed).Packet?.Data
            : CDemoPacket.Parser.ParseFrom(decompressed).Data;

    /// <summary>
    ///     Decodes the frame's packet proto, re-encodes its inner bitstream without the dropped message
    ///     types, and re-serializes. Going through the generated parser (rather than hand-splicing the
    ///     proto wire) preserves every other field, including unknown ones.
    /// </summary>
    private static byte[] StripFramePayload(
        DemoFrame frame, byte[] decompressed, IReadOnlySet<int> dropTypeIds, out StripStats stats)
    {
        if (string.Equals(frame.Command, "DEM_FullPacket", StringComparison.Ordinal))
        {
            CDemoFullPacket full = CDemoFullPacket.Parser.ParseFrom(decompressed);
            if (full.Packet is { Data: { } fullData })
            {
                full.Packet.Data = ByteString.CopyFrom(PacketRewriter.Rewrite(fullData.Span, dropTypeIds, out stats));
            }
            else
            {
                stats = default;
            }

            return full.ToByteArray();
        }

        CDemoPacket packet = CDemoPacket.Parser.ParseFrom(decompressed);
        packet.Data = ByteString.CopyFrom(PacketRewriter.Rewrite(packet.Data.Span, dropTypeIds, out stats));
        return packet.ToByteArray();
    }

    /// <summary>
    ///     Writes a rewritten frame, re-compressing when that is actually smaller.
    ///     Returns whether the frame went out compressed.
    /// </summary>
    private static bool WritePayloadFrame(Stream fs, DemoFrame frame, byte[] payload)
    {
        int commandId = DemoFormat.CommandIdByName[frame.Command];
        byte[] compressed = Snappy.CompressToArray(payload);
        bool useCompressed = compressed.Length < payload.Length;
        DemoFormat.WriteFrame(fs, commandId, frame.ServerTick, useCompressed, useCompressed ? compressed : payload);
        return useCompressed;
    }

    /// <summary>
    ///     Re-emits <c>DEM_FileInfo</c> with playback totals that describe the trimmed file rather than
    ///     the source. Field <em>shape</em> is preserved — the source message is cloned and only values
    ///     change, so a demo without <c>game_info</c> does not gain one.
    ///     <para>
    ///         <c>playback_ticks</c> is set to the last retained frame's absolute tick (ticks are not
    ///         rebased), and <c>CGameInfo.cs.round_start_ticks</c> is filtered to the retained window so
    ///         CS2's scrubber cannot show markers for rounds the file no longer contains.
    ///     </para>
    /// </summary>
    private static void WriteFileInfo(
        Stream fs, ParsedDemo demo, byte[] raw, DemoTail tail,
        IReadOnlyList<int> emitted, IReadOnlyList<DemoFrame> frames,
        TrimWindow window, out string before, out string after)
    {
        before = "(none)";
        after = "(none)";
        if (tail.FileInfo is not { } fileInfoFrame)
        {
            return;
        }

        CDemoFileInfo source = CDemoFileInfo.Parser.ParseFrom(DemoTail.Payload(raw, fileInfoFrame));
        CDemoFileInfo trimmed = source.Clone();
        int playbackFrames = emitted.Count(i => frames[i].Command is "DEM_Packet" or "DEM_FullPacket");

        trimmed.PlaybackTicks = window.EndTick;
        trimmed.PlaybackFrames = playbackFrames;
        trimmed.PlaybackTime = window.EndTick * demo.TickInterval;

        if (trimmed.GameInfo?.Cs is { } cs)
        {
            List<int> retained = cs.RoundStartTicks.Where(t => t <= window.EndTick).ToList();
            cs.RoundStartTicks.Clear();
            cs.RoundStartTicks.AddRange(retained);
        }

        before = Describe(source);
        after = Describe(trimmed);
        DemoFormat.WriteFrame(fs, fileInfoFrame.CommandId, window.EndTick, false, trimmed.ToByteArray());
    }

    private static string Describe(CDemoFileInfo info) => string.Create(CultureInfo.InvariantCulture,
        $"ticks={info.PlaybackTicks} frames={info.PlaybackFrames} time={info.PlaybackTime:F2}s " +
        $"roundStartTicks={(info.GameInfo?.Cs is { } cs ? cs.RoundStartTicks.Count : 0)}");
}
