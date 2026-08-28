#region

using System.Globalization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using Google.Protobuf;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>One synthesized team assignment: the state a real <c>player_team</c> event would carry.</summary>
/// <param name="Slot">Player slot (controller entity index − 1) — the parser's <c>Players</c> key.</param>
/// <param name="Team">Team number at the sample point (2 = T, 3 = CT).</param>
/// <param name="PawnHandle">The controller's pawn handle for the <c>userid_pawn</c> key (0 when unknown).</param>
/// <param name="Name">Player name at the sample point — logging/verification only, not encoded.</param>
internal readonly record struct TeamSample(int Slot, int Team, long PawnHandle, string Name);

/// <summary>
///     Synthesizes the <c>player_team</c> events a trimmed demo cannot otherwise carry.
///     <para>
///         <b>Why:</b> GOTV does not emit <c>player_team</c> for the initial seating — measured on both
///         reference demos, the ONLY <c>player_team</c> events in the entire file are the halftime side
///         swap (10 events, all on one tick). Any trim that ends before halftime therefore contains
///         zero of them, <c>DemoParser</c>'s team post-pass (which is fed exclusively by these events)
///         leaves every <c>PlayerInfo.Team</c> at 0, and every downstream consumer — the analysis
///         layer's <c>PlayerTeamEdge</c>, the stats scoreboard's CT/T split, Match Overview — renders
///         all players on one team. The trimmer knows the truth (it already replays entity state), so
///         it writes the missing events into the output and the file becomes self-describing for any
///         reader, with no app-side special-casing.
///     </para>
///     <para>
///         Events are encoded against the demo's OWN <c>CMsgSource1LegacyGameEventList</c> descriptor
///         (key order and wire types taken from the descriptor, never assumed), exactly as a recorded
///         event would be. Teams are sampled at the first kept <c>round_freeze_end</c> — the start of
///         the trimmed match — via a sequential entity replay of the source; controller entity index − 1
///         is the player slot (verified against the userinfo table by name).
///     </para>
/// </summary>
internal static class TeamEventSynthesizer
{
    /// <summary>
    ///     Samples each seated player's team (2/3 only — spectators and the recorder are skipped) at
    ///     the source demo's first <c>round_freeze_end</c>. Returns an empty list when the demo has no
    ///     freeze-end (nothing worth trimming) or no controllers carry a team.
    /// </summary>
    public static IReadOnlyList<TeamSample> Sample(ParsedDemo demo)
    {
        // FrameNumber is per-fire transport, so this filters on the payload type but reads the
        // frame off the envelope.
        int stopFrame = demo.AllGameEvents
            .FirstOrDefault(e => e.Payload is RoundFreezeEndEvent)?.FrameNumber ?? -1;
        if (stopFrame < 0)
        {
            return [];
        }

        EntityTracker tracker = new();
        for (int i = 0; i <= stopFrame && i < demo.Frames.Count; i++)
        {
            tracker.AdvanceOneFrame(demo.Frames[i]);
        }

        List<TeamSample> samples = [];
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed()
                     .Where(t => t.Entity.ClassName.Contains("PlayerController", StringComparison.Ordinal))
                     .OrderBy(t => t.Index))
        {
            if (!entity.Fields.TryGetValue("m_iTeamNum", out object? teamObj))
            {
                continue;
            }

            int team = Convert.ToInt32(teamObj, CultureInfo.InvariantCulture);
            if (team is not (2 or 3))
            {
                continue; // spectators / the recorder — the source demo leaves them team 0 too
            }

            long pawn = entity.Fields.TryGetValue("m_hPlayerPawn", out object? pawnObj)
                ? unchecked((long)(uint)Convert.ToUInt64(pawnObj, CultureInfo.InvariantCulture))
                : 0;
            string name = entity.Fields.TryGetValue("m_iszPlayerName", out object? nameObj)
                ? nameObj?.ToString() ?? ""
                : "";
            samples.Add(new TeamSample(index - 1, team, pawn, name));
        }

        return samples;
    }

    /// <summary>
    ///     Finds the source demo's <c>player_team</c> descriptor in its
    ///     <c>CMsgSource1LegacyGameEventList</c>, or <c>null</c> when the demo carries none (the
    ///     synthesis is then skipped — inventing a descriptor would desync from the real schema).
    ///     <paramref name="listFrameIndex" /> is the frame carrying the list: the synthesized packet
    ///     must be injected AFTER it, because a sequential reader (our own parser included) loads the
    ///     event schema when it reaches that frame — events seen earlier decode with a null
    ///     descriptor, keeping their name but losing every field. Measured: the pro demo interleaves
    ///     animation stream frames before the signon's event list, which is exactly the ordering that
    ///     bites a naive inject-at-first-stream-frame rule.
    /// </summary>
    public static CMsgSource1LegacyGameEventList.Types.descriptor_t? FindDescriptor(
        ParsedDemo demo, out int listFrameIndex)
    {
        for (int i = 0; i < demo.Frames.Count; i++)
        {
            foreach (NetMessage message in demo.Frames[i].InnerMessages)
            {
                if (message.Payload is not CMsgSource1LegacyGameEventList list)
                {
                    continue;
                }

                CMsgSource1LegacyGameEventList.Types.descriptor_t? descriptor =
                    list.Descriptors.FirstOrDefault(d => d.Name == "player_team");
                if (descriptor is not null)
                {
                    listFrameIndex = i;
                    return descriptor;
                }
            }
        }

        listFrameIndex = -1;
        return null;
    }

    /// <summary>
    ///     Builds the full frame payload for one <c>DEM_Packet</c> carrying one
    ///     <c>GE_Source1LegacyGameEvent</c> (type 207) per sample, encoded per
    ///     <paramref name="descriptor" />. A packet frame's payload is a serialized
    ///     <c>CDemoPacket</c> whose <c>data</c> field holds the inner-message bitstream — writing the
    ///     bare bitstream instead "parses" as an EMPTY packet (protobuf is lenient), which is a
    ///     silent zero-event file, not an error.
    /// </summary>
    public static byte[] BuildPacketPayload(
        CMsgSource1LegacyGameEventList.Types.descriptor_t descriptor,
        IReadOnlyList<TeamSample> samples)
    {
        const int GeSource1LegacyGameEvent = 207;

        BitStreamWriter writer = new(64 * samples.Count + 16);
        foreach (TeamSample sample in samples)
        {
            byte[] payload = BuildEvent(descriptor, sample).ToByteArray();
            writer.WriteUBitVar(GeSource1LegacyGameEvent);
            writer.WriteUVarInt32((uint)payload.Length);
            writer.WriteBytes(payload);
        }

        return new CDemoPacket
        {
            Data = ByteString.CopyFrom(writer.ToArray())
        }.ToByteArray();
    }

    /// <summary>
    ///     One event, keys in the descriptor's own order with the descriptor's own wire types. Key
    ///     semantics: <c>silent</c> is set (no client-side "joined team" announcement on playback);
    ///     <c>oldteam</c> is 0 — this is a seating statement, not a switch.
    /// </summary>
    private static CMsgSource1LegacyGameEvent BuildEvent(
        CMsgSource1LegacyGameEventList.Types.descriptor_t descriptor, TeamSample sample)
    {
        CMsgSource1LegacyGameEvent msg = new()
        {
            Eventid = descriptor.Eventid,
            EventName = descriptor.Name
        };

        foreach (CMsgSource1LegacyGameEventList.Types.key_t key in descriptor.Keys)
        {
            long value = key.Name switch
            {
                "userid" => sample.Slot,
                "userid_pawn" => sample.PawnHandle,
                "team" => sample.Team,
                "oldteam" => 0,
                "silent" => 1,
                _ => 0 // disconnect, isbot, anything the schema grows later
            };

            CMsgSource1LegacyGameEvent.Types.key_t k = new()
            {
                Type = key.Type
            };
            // Mirror of GameEventDecoder.ExtractValue: each descriptor type reads exactly one val_*
            // field, so each must be WRITTEN to exactly that field.
            switch (key.Type)
            {
                case 1: k.ValString = value.ToString(CultureInfo.InvariantCulture); break;
                case 2: k.ValFloat = value; break;
                case 3: k.ValLong = (int)value; break;
                case 4: k.ValShort = (int)value; break;
                case 5: k.ValByte = (int)value; break;
                case 6: k.ValBool = value != 0; break;
                case 7: k.ValUint64 = (ulong)value; break;
                case 8: k.ValLong = (int)value; break; // entity/pawn handle (32-bit)
                case 9: k.ValShort = (int)value; break; // controller slot index (16-bit)
                default: k.ValLong = (int)value; break;
            }

            msg.Keys.Add(k);
        }

        return msg;
    }
}
