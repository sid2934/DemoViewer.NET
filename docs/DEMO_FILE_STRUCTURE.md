# CS2 Demo File Structure

Reference for how `.dem` files are laid out on disk, how each message type is encoded,
and how the game event and entity systems work. Implementation-agnostic.

---

## 1. File Layout Overview

```
┌─────────────────────────────────────────────────────┐
│  File Header  (16 bytes, raw binary — NOT protobuf) │
├─────────────────────────────────────────────────────┤
│  Frame 0  (DEM_FileHeader)                          │
│  Frame 1  (DEM_SendTables)                          │
│  Frame 2  (DEM_ClassInfo)                           │
│  Frame 3  (DEM_StringTables)                        │
│  Frame 4  (DEM_SignonPacket)  ──► inner net msgs    │
│  Frame 5  (DEM_SyncTick)                            │
│  Frame 6  (DEM_Packet)        ──► inner net msgs    │
│  Frame 7  (DEM_Packet)        ──► inner net msgs    │
│  …                                                  │
│  Frame N  (DEM_FullPacket)    ──► checkpoint        │
│  …                                                  │
│  Frame Z  (DEM_Stop)                                │
└─────────────────────────────────────────────────────┘
```

The file is a linear sequence of **frames**. Each frame encodes one top-level demo command.
The two most common frames (`DEM_Packet` / `DEM_SignonPacket`) embed a second layer of
**net messages**.

---

## 2. Raw File Header (16 bytes)

| Offset | Size | Type    | Field                                                  |
|--------|------|---------|--------------------------------------------------------|
| 0      | 8    | ASCII   | Magic: `"PBDEMS2\0"`                                   |
| 8      | 4    | int32LE | Spawngroups stream offset                              |
| 12     | 4    | int32LE | `CDemoFileInfo` frame offset (seek target for summary) |

Frames begin immediately after byte 16.

The file header is *not* a protobuf message. The `CDemoFileHeader` protobuf (see §4) is
delivered as the payload of the first frame (`DEM_FileHeader`, command = 1).

---

## 3. Frame Binary Layout

Each frame is a fixed-format record:

| Offset | Size | Type     | Field       |
|--------|------|----------|-------------|
| 0      | 1    | uint8    | `cmd_type`  |
| 1      | 4    | int32LE  | `tick`      |
| 5      | 4    | uint32LE | `data_size` |
| 9      | N    | bytes    | `data`      |

### `cmd_type` byte

```
Bit 6 (mask 0x40 = 64) — DEM_IsCompressed flag
Bits 0–5               — EDemoCommands value (0–18)
```

If `DEM_IsCompressed` is set, the `data` bytes are **Snappy**-compressed; decompress
before protobuf parsing.

### `tick`

The demo tick at which this frame was recorded. Ticks start at −1 (pre-game / signon phase),
reach 0 at `DEM_SyncTick`, then count up monotonically through the match.

### `data`

A protobuf-encoded message whose concrete type is determined by `cmd_type & 0x3F`.
See §4 for the full mapping.

---

## 4. Top-Level Command Types (`EDemoCommands`)

| Value | Name                      | Protobuf type              | Notes |
|-------|---------------------------|----------------------------|-------|
| 0     | `DEM_Stop`                | `CDemoStop` (empty)        | End of demo |
| 1     | `DEM_FileHeader`          | `CDemoFileHeader`          | First frame; server/map metadata |
| 2     | `DEM_FileInfo`            | `CDemoFileInfo`            | Playback duration / tick count summary |
| 3     | `DEM_SyncTick`            | `CDemoSyncTick`            | Marks tick 0; signon phase ends here |
| 4     | `DEM_SendTables`          | `CDemoSendTables`          | Flattened serializer schema for entity fields |
| 5     | `DEM_ClassInfo`           | `CDemoClassInfo`           | Entity class registry |
| 6     | `DEM_StringTables`        | `CDemoStringTables`        | Initial string table snapshot |
| 7     | `DEM_Packet`              | `CDemoPacket`              | Normal per-tick packet; contains inner net messages |
| 8     | `DEM_SignonPacket`        | `CDemoPacket`              | Same layout as `DEM_Packet`; emitted during signon phase |
| 9     | `DEM_ConsoleCmd`          | `CDemoConsoleCmd`          | Server-side console command string |
| 10    | `DEM_CustomData`          | `CDemoCustomData`          | Game-specific opaque blob |
| 11    | `DEM_CustomDataCallbacks` | `CDemoCustomDataCallbacks` | |
| 12    | `DEM_UserCmd`             | `CDemoUserCmd`             | Client user command blob |
| 13    | `DEM_FullPacket`          | `CDemoFullPacket`          | Seek checkpoint: full entity snapshot + packet |
| 14    | `DEM_SaveGame`            | `CDemoSaveGame`            | |
| 15    | `DEM_SpawnGroups`         | `CDemoSpawnGroups`         | |
| 16    | `DEM_AnimationData`       | `CDemoAnimationData`       | |
| 17    | `DEM_AnimationHeader`     | `CDemoAnimationHeader`     | |
| 18    | `DEM_Recovery`            | `CDemoRecovery`            | |
| 64    | `DEM_IsCompressed`        | *(bitmask only)*           | OR'd with the above values |

### Key frames in detail

**`DEM_FileHeader` → `CDemoFileHeader`**
Contains server name, client name, map name, game directory, build number,
demo version GUID, and `server_start_tick`.

**`DEM_SendTables` → `CDemoSendTables`**
Contains a size-prefixed `CSVCMsg_FlattenedSerializer` blob. The size prefix is a
uvarint immediately before the protobuf bytes. This message defines every networked
entity field: name, type, encoding parameters, and inheritance hierarchy. It must be
processed before any `svc_PacketEntities` message can be decoded.

**`DEM_ClassInfo` → `CDemoClassInfo`**
Maps integer class IDs to string class names (e.g. `5 → "CCSPlayerController"`).
Required to determine which serializer applies to each entity.

**`DEM_StringTables` → `CDemoStringTables`**
An initial snapshot of all string tables at the start of recording. The `userinfo`
table (see §7) is typically present here. Subsequent mutations arrive as
`svc_CreateStringTable` / `svc_UpdateStringTable` net messages inside `DEM_Packet`.

**`DEM_FullPacket` → `CDemoFullPacket`**
Written periodically (roughly every 64 ticks) as a seek checkpoint. Contains:
- `string_table: CDemoStringTables` — full string table snapshot at this tick
- `packet: CDemoPacket` — full entity state (not a delta; all fields transmitted)

Used by demo players to implement random-access seeking without replaying from the start.

---

## 5. `CDemoPacket` — Inner Net Message Multiplexing

`DEM_Packet` and `DEM_SignonPacket` both deliver a `CDemoPacket` whose single `data`
field is a binary stream of zero or more **net messages** packed end-to-end:

```
CDemoPacket.data:
┌──────────────────────────────────────────────┐
│  msg[0]:  type_id (uvarint)                  │
│           size    (uvarint)                  │
│           payload (protobuf, [size] bytes)   │
│  msg[1]:  type_id (uvarint)                  │
│           size    (uvarint)                  │
│           payload (protobuf, [size] bytes)   │
│  …                                           │
└──────────────────────────────────────────────┘
```

`type_id` maps to one of two enums:

- **`NET_Messages`** (NEM_*, ids 0–15): base network messages
- **`SVC_Messages`** (svc_*, ids 40–77): server-to-client service messages
- **`Bidirectional_Messages`** (bi_*, ids 16–19): sent in both directions; notably
  `bi_GameEvent` (id 18) carries decoded CS2 game events

### Common inner net messages

| ID | Name                      | Description |
|----|---------------------------|-------------|
| 4  | `net_SetConVar`           | Server convars |
| 6  | `net_SignonState`         | Connection state transitions |
| 16 | `bi_ReliableMessage`      | Reliable delivery wrapper |
| 17 | `bi_UnreliableMessage`    | Unreliable delivery wrapper |
| 18 | `bi_GameEvent`            | CS2 game event (see §8) |
| 19 | `bi_GameEventList`        | Game event schema (see §8) |
| 40 | `svc_ServerInfo`          | Map name, tick interval, max clients |
| 41 | `svc_FlattenedSerializer` | Entity field schema |
| 42 | `svc_ClassInfo`           | Entity class list |
| 44 | `svc_CreateStringTable`   | Creates a named string table |
| 45 | `svc_UpdateStringTable`   | Patches entries in a string table |
| 46 | `svc_VoiceInit`           | Voice codec info |
| 47 | `svc_VoiceData`           | Encoded voice audio chunk |
| 48 | `svc_Print`               | Console print string |
| 49 | `svc_Sounds`              | Sound events |
| 50 | `svc_SetView`             | Observer camera entity |
| 51 | `svc_ClearAllStringTables`| Wipe and recreate all string tables |
| 53 | `svc_BSPDecal`            | Permanent decal placed on world |
| 55 | `svc_PacketEntities`      | Delta-compressed entity state update |
| 56 | `svc_Prefetch`            | Pre-cache a sound |
| 60 | `svc_PeerList`            | Player peer addresses |
| 62 | `svc_HLTVStatus`          | GOTV relay info |
| 63 | `svc_ServerSteamID`       | Server Steam ID |
| 70 | `svc_FullFrameSplit`      | Splits an oversized full-frame across ticks |
| 72 | `svc_UserMessage`         | Wraps a higher-level user message |
| 74 | `svc_Broadcast_Command`   | Server broadcast console command |
| 76 | `svc_UserCmds`            | Per-player user commands for this tick |

---

## 6. Entity System

### CDemoSendTables and the Flattened Serializer

Before any entity data can be decoded, the field schema must be parsed from
`DEM_SendTables`. The outer `CDemoSendTables.data` is a size-prefixed blob:

```
CDemoSendTables.data:
  [uvarint: byte length of serializer proto]
  [CSVCMsg_FlattenedSerializer: protobuf bytes]
```

`CSVCMsg_FlattenedSerializer` contains:
- **Serializers**: named field groups, each describing one entity class.
  A serializer may extend another (inheritance).
- **Fields** (`ProtoFlattenedSerializerField_t`): each specifies:
  - `var_name` — field name (e.g. `"m_iHealth"`)
  - `var_type` — type string (e.g. `"int32"`, `"CHandle<CCSPlayerPawn>"`, `"float32"`)
  - `bit_count`, `low_value`, `high_value` — encoding parameters for floats
  - `encode_flags` — bitmask controlling quantisation and coordinate encoding
  - `field_serializer_name` / `field_serializer_version` — for nested types
  - `send_node` — path in the send table hierarchy

The type string determines the decoder to use. Common types:

| Type string pattern          | Decoding |
|------------------------------|----------|
| `bool`                       | 1 bit |
| `uint8`, `uint16`, `uint32`, `uint64` | varint |
| `int8`, `int16`, `int32`, `int64`     | signed varint |
| `float32`                    | 32-bit float or quantised float |
| `Vector`, `QAngle`           | 3 × float (coord-encoded) |
| `CHandle<T>`, `CStrongHandle<T>` | 32-bit entity handle |
| `char[N]` / `CUtlString`     | length-prefixed or fixed string |
| `CNetworkedQuantizedFloat`   | quantised per `bit_count`/`low_value`/`high_value` |

### svc_PacketEntities — Entity Delta Encoding

`svc_PacketEntities` (`CSVCMsg_PacketEntities`) carries the entity state update for one
tick. Its `entity_data` field is a custom bitfield stream (not protobuf) encoded as follows:

**Entity record format** (repeated for each changed entity):
1. **Entity index delta** — variable-length encoded index offset from the previous entity
2. **Update flags** — 2 bits: create / update / delete / leave-PVS
3. For **create**: class ID + serial number + full baseline field list
4. For **update**: field path list + new field values (only dirty fields transmitted)
5. For **delete**: entity is removed from the active set

**Field path encoding** uses a Huffman-coded path description. A field path is a sequence
of integers that identifies a (possibly nested) field within the serializer tree. Paths are
prefix-coded using a well-known Huffman table shared between client and server.

**Field value encoding** follows the decoder specified in the serializer field definition.
Floats may be full 32-bit, coord-compressed, or quantised. Integers are bit-packed to
their declared `bit_count`.

The `is_delta` flag in `CSVCMsg_PacketEntities` distinguishes full-baseline packets
(all fields transmitted) from delta packets (only changed fields).

### Entity Handles

Entity handles are 32-bit values:

```
Bits 0–14  — entity index (max 16383 entities)
Bits 15–29 — serial number (distinguishes reused slots)
Bits 30–31 — reserved / type flags
```

A handle value of `0xFFFFFFFF` (or equivalent invalid sentinel) means "no entity".
When an entity is destroyed and its slot is reused, the serial number increments,
so stale handles from before the reuse compare as not equal.

---

## 7. String Tables

String tables are named key-value stores maintained by the server. Each table has a
name (e.g. `"userinfo"`, `"modelprecache"`) and up to 4096 entries. Entries can hold
string data and/or binary data.

### Lifecycle

1. **`DEM_StringTables`** — initial snapshot of all tables (raw protobuf, no delta encoding)
2. **`svc_CreateStringTable`** — creates a named table with a specified max entry count;
   may contain an initial set of entries encoded in a bitfield stream
3. **`svc_UpdateStringTable`** — patches existing entries; entries are bit-packed and
   may include string diffs (prefix compression) and binary data blobs
4. **`DEM_FullPacket`** — contains a fresh full snapshot of all tables (same format as step 1)

### String Table Entry Encoding (svc_Create/UpdateStringTable)

The `string_data` blob inside `CSVCMsg_CreateStringTable` and `CSVCMsg_UpdateStringTable`
is a bitfield stream (not protobuf):

```
[num_changed_entries: varint]
for each changed entry:
  [index delta: variable-length bits]
  [has_name_delta: 1 bit]
    if set: [prefix_length: 5 bits] [suffix: null-terminated string]
  [has_user_data: 1 bit]
    if set and fixed_userdata_size > 0: [user_data: fixed_userdata_size bytes]
    if set and fixed_userdata_size == 0: [user_data_length: bits] [user_data: bytes]
```

### `userinfo` String Table

The `userinfo` table (index 0 by convention) holds one entry per player slot (0–63).
Each entry's user-data is a fixed-size binary structure describing the player:

| Offset | Size | Field |
|--------|------|-------|
| 0      | 128  | Steam authentication ticket (opaque) |
| 128    | 32   | Player name (null-terminated UTF-8) |
| 160    | 4    | Userid (int32, the in-game `userid` value used in game events) |
| 164    | 32   | Steam ID string (null-terminated) |
| 196    | 8    | SteamID64 (uint64, little-endian) |
| 204    | 1    | IsBot flag |
| …      | …    | Additional fields (HLTV, fakebot, etc.) |

The string key for each entry is the player name; the slot index corresponds directly
to the player's controller entity index.

---

## 8. Game Events

CS2 game events are carried via `bi_GameEvent` (bidirectional message id 18), which
wraps a `CMsgSource1LegacyGameEvent`. This is a continuation of the Source 1 game event
system (the protobuf message names retain "Legacy" to distinguish them from newer systems).

### Schema Transmission

The event schema is transmitted at demo start via `bi_GameEventList` (bidirectional
message id 19), which contains a `CMsgSource1LegacyGameEventList`. It must be processed
before any `bi_GameEvent` can be fully decoded.

`CMsgSource1LegacyGameEventList` contains a list of `descriptor_t` entries, each of which
specifies:
- `eventid` (int32) — numeric event identifier
- `name` (string) — event name (e.g. `"player_death"`)
- `keys` — ordered list of field definitions, each with:
  - `name` (string) — field name
  - `type` (int32) — field value type (see table below)

The ordering of `keys` is significant: the i-th key in `descriptor_t.keys` corresponds
to the i-th key value in `CMsgSource1LegacyGameEvent.keys`.

### Event Delivery

Each `CMsgSource1LegacyGameEvent` carries:
- `eventid` (int32) — matches a `descriptor_t.eventid` from the schema
- `event_name` (string) — may be empty if `eventid` is set
- `server_tick` (int32) — the server tick at which the event fired (preferred over frame tick)
- `keys` — ordered list of `key_t` values; each has one of the typed value fields set

### Field Value Types

| Type int | Proto field     | C# equivalent | Notes |
|----------|-----------------|----------------|-------|
| 1        | `val_string`    | `string`       | Null-terminated UTF-8 |
| 2        | `val_float`     | `float`        | IEEE 754 single |
| 3        | `val_long`      | `int` (int32)  | Also used for pawn entity handles |
| 4        | `val_short`     | `short` (int16)| Player slots, entity IDs |
| 5        | `val_byte`      | `byte`         | |
| 6        | `val_bool`      | `bool`         | |
| 7        | `val_uint64`    | `ulong`        | SteamID64, weapon item IDs |

### Player Identification in Events — `userid` vs `userid_pawn`

CS2 game events use two distinct player reference fields:

**`userid`** (type `val_short`):
The **controller entity slot index** — the index of the `CCSPlayerController` entity
that owns the player. This is the same value stored in the `userinfo` string table entry
for the player's slot, and is used to look up the player in entity state. It is *not*
a simple 0-based index into a player list; it is an entity handle index.

**`userid_pawn`** (type `val_long`):
The **pawn entity handle** — a 32-bit entity handle (index + serial, see §6) pointing to
the `CCSPlayerPawn` entity. This handle is suitable for direct entity state lookup.

Most events carry both `userid` and `userid_pawn`. Exceptions:
- `bomb_pickup` — omits `userid`; only `userid_pawn` is present
- `player_jump` — omits `userid_pawn`; only `userid` is present
- `player_avenged_teammate` — uses `avenger_id` / `avenged_player_id` (both `val_short`)
- Non-player events (inferno, defuser) — carry entity IDs, not player references

### Common Game Events

#### Round Lifecycle

| Event name              | Key fields | Notes |
|-------------------------|------------|-------|
| `round_freeze_end`      | *(none)*   | Buy time over; live play begins |
| `round_end`             | `winner` (short), `reason` (short), `message` (string), `legacy` (short), `player_count` (short), `nomusic` (short) | Winner: 2=T, 3=CT |
| `round_officially_ended`| `reason` (short) | Round fully concluded; safe to collect stats |
| `round_prestart`        | *(none)*   | Fires just before round counter increments |
| `round_poststart`       | *(none)*   | Round initialisation complete; players live |
| `cs_round_start_beep`   | *(none)*   | Audio cue for round start |
| `buytime_ended`         | *(none)*   | Buy phase over |
| `halftime`              | *(none)*   | Halftime reached |
| `announce_phase_end`    | *(none)*   | Match phase finished |
| `cs_match_end_restart`  | *(none)*   | Match-end restart |

#### Player Actions

| Event name              | Key fields | Notes |
|-------------------------|------------|-------|
| `player_death`          | `userid`, `userid_pawn`, `attacker`, `assister`, `weapon` (string), `headshot` (bool), `penetrated` (short), `dominated` (bool), `revenge` (bool), `noscope` (bool), `thrusmoke` (bool), `attackerblind` (bool), `assistedflash` (bool), `distance` (float), `dmg_health` (short), `dmg_armor` (short) | |
| `player_hurt`           | `userid`, `userid_pawn`, `attacker`, `health` (short), `armor` (short), `dmg_health` (short), `dmg_armor` (short), `weapon` (string), `hitgroup` (short) | |
| `player_blind`          | `userid`, `userid_pawn`, `attacker`, `blind_duration` (float) | |
| `player_spawn`          | `userid`, `userid_pawn`, `teamnum` (short) | |
| `player_team`           | `userid`, `userid_pawn`, `team` (short), `oldteam` (short), `silent` (bool), `isbot` (bool) | |
| `player_connect`        | `userid`, `name` (string), `index` (short), `xuid` (uint64) | Fired when player connects |
| `player_connect_full`   | `userid`, `name` (string), `index` (short), `xuid` (uint64) | Fired when fully loaded |
| `player_disconnect`     | `userid`, `name` (string), `reason` (short) | |
| `player_footstep`       | `userid`, `userid_pawn` | |
| `player_jump`           | `userid` *(only)* | No `userid_pawn` |
| `player_avenged_teammate`| `avenger_id` (short), `avenged_player_id` (short) | |

#### Weapons

| Event name              | Key fields | Notes |
|-------------------------|------------|-------|
| `weapon_fire`           | `userid`, `userid_pawn`, `weapon` (string), `silenced` (bool) | |
| `weapon_fire_on_empty`  | `userid`, `userid_pawn`, `weapon` (string) | Dry fire |
| `weapon_reload`         | `userid`, `userid_pawn` | |
| `weapon_zoom`           | `userid`, `userid_pawn` | Scope in/out toggle |
| `item_equip`            | `userid`, `userid_pawn`, `item` (string), `defindex` (short), `weptype` (short), `canzoom` (bool), `hassilencer` (bool), `issilenced` (bool), `ispainted` (bool) | |
| `item_pickup`           | `userid`, `userid_pawn`, `item` (string), `hasbackpack` (bool), `defindex` (short) | |
| `item_remove`           | `userid`, `userid_pawn`, `item` (string) | |
| `item_drop`             | `userid`, `userid_pawn`, `item` (string) | |
| `bullet_impact`         | `userid`, `userid_pawn`, `x` (float), `y` (float), `z` (float) | |
| `bullet_damage`         | `victim_pawn` (long), `attacker_pawn` (long), `distance` (float), `num_penetrations` (short), `no_scope` (bool), `in_air` (bool), `damage_dir_x/y/z` (float) | |

#### Bomb

| Event name              | Key fields | Notes |
|-------------------------|------------|-------|
| `bomb_pickup`           | `userid_pawn` *(only)* | No `userid` field |
| `bomb_dropped`          | `userid`, `userid_pawn`, `entindex` (long) | |
| `bomb_beginplant`       | `userid`, `userid_pawn`, `site` (short) | |
| `bomb_abortplant`       | `userid`, `userid_pawn`, `site` (short) | |
| `bomb_planted`          | `userid`, `userid_pawn`, `site` (short) | |
| `bomb_begindefuse`      | `userid`, `userid_pawn`, `haskit` (bool) | |
| `bomb_abortdefuse`      | `userid`, `userid_pawn` | |
| `bomb_defused`          | `userid`, `userid_pawn`, `site` (short) | |
| `bomb_exploded`         | `userid`, `userid_pawn`, `site` (short) | |
| `defuser_dropped`       | `entityid` (long) | No player reference |
| `defuser_pickup`        | `entityid` (long), `userid`, `userid_pawn` | |

#### Grenades and Fire

| Event name              | Key fields | Notes |
|-------------------------|------------|-------|
| `grenade_thrown`        | `userid`, `userid_pawn`, `weapon` (string) | Fires on release, before flight |
| `flashbang_detonate`    | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | |
| `hegrenade_detonate`    | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | |
| `smokegrenade_detonate` | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | |
| `smokegrenade_expired`  | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | |
| `molotov_detonate`      | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | |
| `inferno_startburn`     | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | Fire begins spreading |
| `decoy_detonate`        | `userid`, `userid_pawn`, `entityid` (short), `x`, `y`, `z` (float) | |
| `inferno_expire`        | `entityid` (short), `x`, `y`, `z` (float) | No player field; fire ran out |
| `inferno_extinguish`    | `entityid` (short), `x`, `y`, `z` (float) | No player field; fire extinguished |

#### Round MVP and Scoring

| Event name  | Key fields |
|-------------|------------|
| `round_mvp` | `userid`, `userid_pawn`, `reason` (short), `value` (short), `nomusic` (bool) |
| `other_death`| `otherid` (short), `othertype` (short), `attacker` (short), `weapon` (string), `weapon_itemid` (string), `weapon_fauxitemid` (string), `weapon_originalowner_xuid` (uint64), `headshot` (bool), `penetrated` (short), `noscope` (bool), `thrusmoke` (bool), `attackerblind` (bool) | Non-player entity death (e.g. chickens) |

---

## 9. Round Lifecycle

CS2 does not emit Source 1 `round_start` / `round_end` pairs directly. The canonical
event sequence for a round is:

```
round_prestart          — counter incremented; warmup-style reset
round_poststart         — players positioned; buy time begins
  [cs_round_start_beep] — audio cue
  [buytime_ended]       — buy phase over
  round_freeze_end      — freeze period over; live play begins
  …  (gameplay events: player_hurt, weapon_fire, bomb_beginplant, etc.)
  round_end             — winner and reason determined
round_officially_ended  — stats may now be safely collected
```

**`round_freeze_end`** is the authoritative "round live" marker. Tick counters for
round duration should be measured from this event.

**`round_officially_ended`** is the authoritative "round complete" marker. Events
between `round_end` and `round_officially_ended` (death animations, round-end sounds)
should generally not be attributed to game outcomes.

If the demo is cut short, `round_officially_ended` may be absent for the final round.

**Winner codes** (from `round_end.winner`):
- `2` — Terrorist team wins
- `3` — Counter-Terrorist team wins

---

## 10. Protobuf Encoding

All payload bytes after the file header are encoded using the standard **proto2** binary
format. Key rules:

- Fields are tagged with `(field_number << 3) | wire_type`
- Wire types: 0=varint, 1=64-bit fixed, 2=length-delimited, 5=32-bit fixed
- Varints are little-endian base-128 (LEB128); negative numbers use 10-byte zig-zag or
  two's-complement encoding
- `DEM_IsCompressed` payloads are Snappy-compressed before the protobuf bytes

The inner net message stream inside `CDemoPacket.data` (§5) uses a separate
encoding: each message is preceded by a uvarint type ID and a uvarint byte length.

---

## 11. Typical Frame Sequence

```
Tick -1:  DEM_FileHeader     — server/map/client metadata
Tick -1:  DEM_SendTables     — entity schema (FlattenedSerializer)
Tick -1:  DEM_ClassInfo      — entity class IDs
Tick -1:  DEM_StringTables   — initial string table dump
Tick -1:  DEM_SignonPacket   — signon phase packets
            └─ svc_ServerInfo
            └─ svc_ClassInfo
            └─ svc_CreateStringTable (×N, including "userinfo")
            └─ svc_PacketEntities (full baseline; is_delta=false)
            └─ bi_GameEventList  — game event schema
Tick  0:  DEM_SyncTick       — marks start of game time
Tick  1:  DEM_Packet         — first normal game tick
            └─ svc_PacketEntities (delta)
            └─ bi_GameEvent (if any event occurred this tick)
Tick  2:  DEM_Packet
…
Tick 64:  DEM_FullPacket     — seek checkpoint
            └─ CDemoStringTables (full snapshot)
            └─ CDemoPacket (full entity state; is_delta=false)
…
Tick  N:  DEM_Stop           — end of recording
```

---

## 12. Entity Field Types and Wire Sizes

### Primitive type sizes

| Type string        | Wire encoding                  | Decoded size |
|--------------------|-------------------------------|--------------|
| `bool`             | 1 bit                          | 1 byte       |
| `uint8`            | 8-bit varint                   | 1 byte       |
| `uint16`           | Varint                         | 2 bytes      |
| `uint32`           | Varint                         | 4 bytes      |
| `uint64`           | Varint                         | 8 bytes      |
| `int8`–`int64`     | Signed varint                  | 1–8 bytes    |
| `float32`          | 32-bit IEEE 754 or quantised   | 4 bytes      |
| `Vector`           | 3 × coord-encoded float        | 12 bytes     |
| `QAngle`           | 3 × coord-encoded float        | 12 bytes     |
| `CHandle<T>`       | 32-bit (index + serial)        | 4 bytes      |
| `CStrongHandle<T>` | 64-bit                         | 8 bytes      |
| `char[N]`          | Fixed N bytes (null-padded)    | N bytes      |

### Selected entity sizes (representative networked field counts)

| Entity class            | Approx. networked fields | Notes |
|-------------------------|--------------------------|-------|
| `CBaseEntity`           | ~80                      | Base class; all entities extend this |
| `CBaseModelEntity`      | ~32                      | Adds model/render fields |
| `CCSPlayerController`   | ~92                      | Per-player score, name, SteamID |
| `C_CSPlayerPawn`        | ~121                     | Per-player physics, health, weapons |
| `CCSWeaponBase`         | ~51                      | Weapon state, ammo, skin |
| `CCSGameRules`          | ~189                     | Global game state, scores, timers |
| `CInferno`              | ~24 + arrays             | Fire positions array; grows with spread |

---

## 13. References

- `demo.proto` — top-level command types and protobuf messages
- `netmessages.proto` — `SVC_Messages` and `Bidirectional_Messages` inner net message types
- `cs_gameevents.proto` — game event list / event message protobuf definitions
- `cstrike15_usermessages.proto` — `ECstrike15UserMessages` type enum and payloads
- CS2 OpenDevDocs: https://sid2934.github.io/CS2-OpenDevDocs/
- GameTracking-CS2 Protobufs: https://github.com/SteamDatabase/GameTracking-CS2/tree/master/Protobufs
- demofile-net (reference implementation): https://github.com/saul/demofile-net
