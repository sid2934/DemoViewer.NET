# Parser Architecture

A guided tour of `DemoViewer.NET.Parser` — the library that turns a raw `.dem`
byte buffer into typed CS2 demo data for the desktop app, analysis engine, and
CLI tooling.

## TL;DR

`DemoParser.Parse(ReadOnlyMemory<byte>)` consumes a CS2 demo file and returns a
`ParsedDemo`: a flat list of `DemoFrame`s plus enriched indexes (game events,
players, schema, server metadata, demo profile). The parser runs three passes
(sequential header scan, parallel proto parse, sequential enrichment), is
zero-allocation on the per-frame hot path for uncompressed payloads, and is
entity-state-agnostic — `svc_PacketEntities.entity_data` is left as opaque
bytes. Entity replay is a separate opt-in layer (`EntityTracker`) on top.

Start reading at [`src/Parser/DemoViewer.NET.Parser/DemoParser.cs:128`](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)
(the `Parse` method).

---

## 1. High-level architecture

```
                        ┌────────────────────────────────────────┐
                        │  Caller (UI / Analysis / CLI tools)    │
                        └────────────────────────────────────────┘
                                       │
                                       │  byte[]  →  ParsedDemo
                                       ▼
        ┌─────────────────────────────────────────────────────────────┐
        │                  DemoViewer.NET.Parser                      │
        │                                                             │
        │   DemoParser.Parse                                          │
        │     ├─ Pass 1: sequential header scan (LEB128, no allocs)   │
        │     ├─ Pass 2: parallel proto parse (Parallel.For + Snappy) │
        │     └─ Pass 3: sequential enrichment (events, players,      │
        │               schema, metadata, profile)                    │
        │                                                             │
        │   Output: ParsedDemo                                        │
        │     ├─ Frames: IReadOnlyList<DemoFrame>                     │
        │     │   └─ InnerMessages: NetMessage / GameEventMessage     │
        │     ├─ AllGameEvents                                        │
        │     ├─ Players, Schema, MapName, TickInterval, Profile…     │
        │     └─ (entity state is NOT populated here)                 │
        │                                                             │
        │   Opt-in: Entities/EntityTracker                            │
        │     - Replays Frames forward                                │
        │     - Decodes svc_PacketEntities entity_data bit stream     │
        │     - Maintains EntitySet (16 384 slots × EntityState)      │
        └─────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
   ┌────────────────────┐   ┌──────────────────────────┐   ┌─────────────────┐
   │ Avalonia UI        │   │ Analysis engine          │   │ CLI tools       │
   │ MainViewModel      │   │ DemoAnalyzer →           │   │ AnalysisBench   │
   │ — iterates Frames  │   │   DemoContext            │   │ EntityDecodeProbe│
   │ — builds Message   │   │   StateGraphEvaluator    │   │ DemoSourceDetails│
   │   Cards            │   │   rule chains (YAML)     │   │ EntityFieldDiff │
   │ — calls EntityTrkr │   │   EntityChangeScanner    │   │ Codegen         │
   │   on selection     │   │                          │   │                 │
   └────────────────────┘   └──────────────────────────┘   └─────────────────┘
```

### Where the parser ends

The parser library has no UI references, no analysis-engine references, and
does not own a "current tick" or "live entity state." It is a pure
input-bytes-to-typed-records function — every output is a value type or
immutable list. Consumers maintain replay state if they need it.

Entity-state replay lives in a **sibling project**,
`DemoViewer.NET.Parser.EntityTracking` at
`src/Parser/DemoViewer.NET.Parser.EntityTracking/`. It contains `EntityTracker`,
`EntityState`, `EntitySet`, `FieldDecoder` family, `FieldPath` family,
`HuffmanNode`. The split came out of the 2026-05 architecture review
(`docs/parser-architecture-backlog.md`): the parser owns "raw bytes → typed
shapes," and entity replay is downstream of that boundary.

`RuntimeSchema`, `RuntimeSerializer`, and `RuntimeField` stay in the parser
project at `src/Parser/DemoViewer.NET.Parser/Entities/` — they're wire-format
schema-interpretation types, not replay-time state. Pulling them out would
have meant the parser project needed `EntityTracking` to compile its
`CSVCMsg_FlattenedSerializer` interpretation. The kept-side and moved-side
share the `Entities/` directory name historically but live in different
namespaces now: `DemoViewer.NET.Parser.Entities` (Runtime*) vs
`DemoViewer.NET.Parser.EntityTracking` (everything else).

`EntityTracker` is constructed explicitly by callers and never invoked by
`DemoParser.Parse`. The project split makes this contract physical: parsing
(cheap, parallelisable, deterministic) and replay (sequential,
schema-dependent, mutable) are now separate assemblies.

---

## 2. The parse pipeline

`DemoParser.Parse` runs three passes ([DemoParser.cs:128](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)).
The pass naming is taken verbatim from the comment block at line 106.

### Pass 1 — sequential header scan

[`DemoParser.cs:139-177`](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)

Walks the input bytes from offset 16 (after the `"PBDEMS2\0"` magic + 8 bytes
of fixed-offset fields) and decodes each frame's three-varint header
(`command`, `tick`, `size`) using `Leb128Utils.ParseFrameHeader`. For every
frame it records a `FrameDescriptor` ([line 828](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)):

| Field | Purpose |
|---|---|
| `RawStart` | Byte offset of this frame in the .dem buffer |
| `HeaderLength` | Bytes consumed by the three varints |
| `Command` | `EDemoCommands` with the compressed flag stripped |
| `Tick` | Frame tick (game tick in CS2) |
| `RawPayloadSize` | Compressed-or-not payload size |
| `IsCompressed` | True iff bit 0x40 was set in the raw command varint |
| `RawPayload` | **Zero-copy** `ReadOnlyMemory<byte>` slice of the caller's buffer |

This pass is sequential because each frame's position depends on the previous
frame's `Size`. It is intentionally allocation-minimal — only the
`List<FrameDescriptor>` grows. Initial capacity is estimated as
`Math.Max(64, data.Length / 250)` to avoid `List<T>` resizing during the scan.

Loop terminates on:
- `DEM_Stop` command (no payload follows),
- truncated header (`ParseFrameHeader` returns `-1`),
- truncated payload (next frame would extend past EOF).

The frame-size varint is validated; `size < 0` (i.e. >2 GB after sign cast)
raises `InvalidDataException`.

### Pass 2 — parallel proto parse

[`DemoParser.cs:179-192`](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)

```csharp
DemoFrame[] results = new DemoFrame[frameDescs.Count];
Parallel.For(0, frameDescs.Count, i => {
    // decompress + parse proto + populate result slot
});
```

Each frame's protobuf decode is fully independent: there is no shared mutable
state, no cross-frame ordering requirement at this stage. The output array is
pre-sized to `frameDescs.Count`, so workers write to disjoint indexes without
locking.

The per-frame work:
1. **Snappy decompression** — performed here (in the worker) iff
   `IsCompressed`. `Snappy.DecompressToArray` is stateless and thread-safe.
   For uncompressed frames the payload is passed straight through as a
   zero-copy slice of the caller's buffer; no new heap buffer is allocated.
2. **ParseFrame** ([DemoParser.cs:522](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)) —
   wraps the payload in a single-segment `ReadOnlySequence<byte>` (a pure
   struct construction) and dispatches by `EDemoCommands` value to the
   appropriate generated `MessageParser<T>`.

Frame-type dispatch falls into three categories:

| Category | Examples | Handling |
|---|---|---|
| Direct-payload | `DEM_FileHeader`, `DEM_SendTables`, `DEM_ClassInfo`, `DEM_StringTables`, `DEM_FileInfo`, … | Whole payload is one proto message → wrapped in a single `NetMessage` with `DecompressedStart=0`. |
| Multiplexed | `DEM_Packet`, `DEM_SignonPacket` | Outer is `CDemoPacket`; inner `.data` is a bitstream of `(UBitVar typeId, UVarInt32 size, bytes payload)` triples. `ParseInnerMessages` walks the bitstream and emits one `NetMessage` per inner. |
| Checkpoint | `DEM_FullPacket` | Outer is `CDemoFullPacket`. Emits one `NetMessage` for the `CDemoStringTables` snapshot (entry 0), followed by inner messages parsed from the nested `CDemoPacket.data`. |

Inner-message ID dispatch is via `ParseNetMessage`
([DemoParser.cs:740](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)) — a
single `switch` on `typeId` covering `NET_Messages`, `Bidirectional_Messages`,
`EBaseGameEvents`, and `SVC_Messages`. Unknown IDs are silently skipped.

Parse failures call the `ParseFailed?` event (null by default) and the slot
becomes `null` in the message list. The pass never throws for a single bad
inner message.

#### Allocation discipline

- Inner-message payload bytes are read into pooled `ArrayPool<byte>` rentals
  ([DemoParser.cs:710](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)),
  passed to `Parser.ParseFrom`, then returned. Google.Protobuf copies all
  data out of the input during parse, so immediate return is safe.
- The proto name → string maps are `FrozenDictionary`s built once at class
  initialisation from `OriginalNameAttribute` reflection
  ([DemoParser.cs:29-37](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)).
- `ReadOnlySequence<byte>` wrapping is a pure struct operation, no heap
  allocation.

### Pass 3 — sequential enrichment

[`DemoParser.cs:208-328`](../src/Parser/DemoViewer.NET.Parser/DemoParser.cs)

Walks the populated `results[]` in order, accumulating per-demo metadata and
promoting raw game-event slots to typed ones. Sequential because:

- Some messages (e.g. game events without a preceding `EventList`) cannot be
  decoded without earlier-in-the-stream context.
- `RuntimeSchema` is built lazily from the first `CDemoSendTables`.
- Player team assignments come from `PlayerTeamEvent`s and must be applied
  last-write-wins in tick order.

Specific transforms applied during enrichment:

| Message type | Effect |
|---|---|
| `CDemoFileHeader` | Populates `MapName`, `ServerName`, `ClientName`, `GameDirectory`, `BuildNum`, `ServerStartTick`, `PatchVersion`, `DemoVersionName`, `DemoVersionGuid`, `Addons`. Sets `eventDecoder.ServerStartTick` for `serverTick → gameTick` translation. |
| `CDemoFileInfo` | If `PlaybackTicks > 0`, this becomes the authoritative `TickCount` (otherwise falls back to max-tick-seen). |
| `CSVCMsg_ServerInfo` | Overrides `TickInterval` (default `1/64`); fills `MapName` when header was missing. |
| `CDemoSendTables` | Builds the `RuntimeSchema` (once) via `TryExtractSchema` → `BitBuffer` to strip size prefix → `CSVCMsg_FlattenedSerializer.Parser.ParseFrom`. |
| `CMsgSource1LegacyGameEventList` | Loads the per-event-id key schema into `GameEventDecoder`. |
| `CMsgSource1LegacyGameEvent` | Decoded to a typed `GameEvent` record; the `NetMessage` slot in the list is **replaced in place** with a `GameEventMessage` that carries both the raw payload and the decoded event. |
| `CDemoStringTables`, `CSVCMsg_CreateStringTable`, `CSVCMsg_UpdateStringTable` | Fed into `StringTableProcessor` which extracts players from the `userinfo` table. |

Post-pass fix-ups:
- `f.GameTick = f.ServerTick` for every frame (line 302). In CS2 demos the
  per-frame tick varint already IS the game tick; this alias exists for
  clarity in downstream code.
- `PlayerInfo.Team` is filled in from the last `player_team` event per
  controller slot (lines 308-316).
- `DemoProfile` is computed via `DemoSourceClassifier.Classify` from the
  header strings unless a `profileOverride` was passed to `Parse`.

### Zero-copy slicing of input bytes

The caller controls the lifetime of the demo buffer. `DemoParser.Parse` takes
`ReadOnlyMemory<byte>` and the entire pipeline reads through it without
copying for uncompressed payloads. For compressed payloads, Snappy
decompression allocates a fresh `byte[]` per frame (worker-local; collected
after `ParseFrame` returns).

The `PayloadStart` / `PayloadLength` / `RawStart` / `RawLength` fields on
`DemoFrame` are byte offsets into the original demo buffer. Callers can hex-
dump a frame via `demoBytes[frame.RawStart .. frame.RawStart + frame.RawLength]`
without re-parsing.

`DownstreamUtilities.GetDecompressedPayload(frame, demoBytes)`
([DownstreamUtilities.cs](../src/Parser/DemoViewer.NET.Parser/DownstreamUtilities.cs))
is the on-demand decompressor: it accepts the original raw bytes (the parser
does not retain them) and either Snappy-inflates the slice or returns a copy
of the uncompressed bytes. The UI uses this to lazily produce frame payloads
for the hex view only when a frame is selected.

`DownstreamUtilities.ExtractInnerMessageBytes(frame, decompressedPayload)`
([DownstreamUtilities.cs](../src/Parser/DemoViewer.NET.Parser/DownstreamUtilities.cs))
is the dual: given a decompressed payload, walk the inner-message bitstream
and return one `byte[]?` per inner message (for hex highlighting in the UI).

### When Snappy decompression happens

| Stage | What | Why |
|---|---|---|
| Pass 1 | Never | Headers carry compression flag but no payload work. |
| Pass 2 | Compressed frames only, in worker thread | Independent and parallel — perfect work to push off-main. |
| On demand | `DownstreamUtilities.GetDecompressedPayload` | UI re-inflates on frame selection so the parser doesn't retain ~1 GB of decompressed buffers. |

---

## 3. The output types

### `ParsedDemo`

[`ParsedDemo.cs`](../src/Parser/DemoViewer.NET.Parser/ParsedDemo.cs) — the
top-level immutable output. Constructed only by `DemoParser.Enrich`. Fields:

| Member | Description |
|---|---|
| `Frames` | All `DemoFrame`s in recording order. |
| `AllGameEvents` | Flat tick-ordered list of decoded `GameEvent`s. |
| `Players` | `IReadOnlyDictionary<int, PlayerInfo>` keyed by **controller entity index** (== `userid` in events). |
| `Schema` | `RuntimeSchema?` from the first `DEM_SendTables` (null if absent). |
| `MapName`, `TickCount`, `TickInterval`, `TickRate`, `Duration` | Match-level metadata. |
| `ServerName`, `ClientName`, `GameDirectory`, `BuildNumber`, `ServerStartTick`, `PatchVersion`, `DemoVersionName`, `DemoVersionGuid`, `Addons` | Raw header fields. |
| `Profile` | `DemoProfile` (auto-classified or override). |

`TickRate` is derived as `Round(1 / TickInterval)`. `Duration` is
`TickCount × TickInterval` as a `TimeSpan`.

### `DemoFrame`

[`DemoFrame.cs`](../src/Parser/DemoViewer.NET.Parser/DemoFrame.cs) — one
top-level `EDemoCommands` entry. Fields are all `required init` (immutable
after construction except `GameTick`, which is set in pass 3):

| Member | Description |
|---|---|
| `Command` | Proto name like `"DEM_Packet"`. |
| `ServerTick` | Game tick in CS2 (see naming note below). |
| `GameTick` | Alias for `ServerTick`; populated in pass 3. |
| `FrameNumber` | Zero-based index in `ParsedDemo.Frames`. |
| `RawStart`, `RawLength` | Byte offsets in the raw `.dem` buffer (covers header + payload). |
| `HeaderLength`, `PayloadStart`, `PayloadLength` | Byte offsets/lengths within the frame. |
| `IsCompressed` | True iff payload was Snappy-compressed on disk. |
| `InnerMessages` | Read-only view of `NetMessage` sub-components. |

#### Inner-message shape per `Command`

| `Command` | Inner messages |
|---|---|
| `DEM_SyncTick`, `DEM_Stop` | Empty. |
| `DEM_FileHeader`, `DEM_SendTables`, `DEM_ClassInfo`, `DEM_StringTables`, `DEM_FileInfo`, `DEM_UserCmd`, `DEM_ConsoleCmd`, `DEM_CustomData`, … | One entry; `MessageTypeName == Command`; `DecompressedStart == 0`. |
| `DEM_Packet`, `DEM_SignonPacket` | The multiplexed `NET_/Bi/SVC/GE` net messages from `CDemoPacket.data`. |
| `DEM_FullPacket` | Entry 0 is the `CDemoStringTables` snapshot, entries 1..N are the nested `CDemoPacket.data` messages. |

#### Naming gotcha — `ServerTick`

Despite the name, `DemoFrame.ServerTick` is the **game tick** (gameplay
starts at 1). Pre-recording frames use a single large negative sentinel
(`-1 - server_start_tick`). The frame header's wire-format `0xFFFFFFFF`
sentinel decodes to `-1` via the `Tick = (int)tick` cast in `FrameHeader`.

The `GameTick` alias was added so downstream code can be explicit without
breaking the existing API. They always have the same value; either is fine.

### `NetMessage` and `GameEventMessage`

[`NetMessage.cs`](../src/Parser/DemoViewer.NET.Parser/NetMessage.cs)

```csharp
public class NetMessage {
    public string  MessageTypeName     { get; init; }   // "svc_PacketEntities", etc.
    public IMessage Payload             { get; init; }  // typed Google.Protobuf message
    public int?    DecompressedStart    { get; init; }  // for hex highlighting
    public int?    DecompressedLength   { get; init; }
}
```

Not `sealed` — see below.

`MessageTypeName` is the **lowercase snake_case** proto name (e.g.
`"svc_PacketEntities"`, not `"CSVCMsg_PacketEntities"`). The UI's accent-
brush logic prefix-matches on `net_/svc_/cs_/DEM_/CDem`, so the raw proto
name is what callers expect.

`Payload` is non-generic `IMessage` because the list holds heterogeneous
types. Cast or pattern-match to specific generated proto types as needed.

`DecompressedStart` / `DecompressedLength` are **byte-approximate** offsets
within the decompressed frame payload, intended for hex highlighting. For
multiplexed packet frames the inner-message bitstream is not byte-aligned, so
the start can drift by ±1 byte from the true bit position. See the comments
on `NetMessage.DecompressedStart` for the exact semantics.

[`GameEventMessage.cs`](../src/Parser/DemoViewer.NET.Parser/GameEvents/GameEventMessage.cs)
extends `NetMessage` with a single extra property: `GameEvent DecodedEvent`.
Every `CMsgSource1LegacyGameEvent` slot in `DemoFrame.MessageList` is
**replaced in place** during pass 3 with a `GameEventMessage` instance.
Callers can pattern-match:

```csharp
foreach (var msg in frame.InnerMessages) {
    if (msg is GameEventMessage gem) {
        switch (gem.DecodedEvent.Payload) {
            case PlayerDeathEvent pd: ...;
            case WeaponFireEvent  wf: ...;
        }
    }
}
```

### `GameEvent`

[`GameEvent.cs`](../src/Parser/Cs2DemoKit.Parser/GameEvents/GameEvent.cs)
is a **non-generic envelope**: the per-fire transport context, plus the typed
payload record the SDK materialised for it.

```csharp
public record GameEvent(
    string Name, int EventId, int FrameNumber,
    int ServerTick, int GameTick, object? Payload = null);
```

The payload records (`PlayerDeathEvent`, `WeaponFireEvent`, `PlayerHurtEvent`, …)
ship in the **`CS2OpenDev.Sdk.GameEvents`** package under the
`CS2OpenSchema.Events` namespace. They are not generated here — a new upstream
event arrives by bumping the package version. This replaced 272 locally
generated record types and the `Codegen --gameevents` flag that emitted them;
both are gone.

`Payload` is `object?` rather than generic because a demo's event stream is
heterogeneous and has to sit in one list. (The SDK's own `GameEventEnvelope<T>`
is generic, which is why we don't use it directly.) Pattern-match to reach a
specific event, as in the snippet above.

Two kinds of event carry a `null` payload and instead subclass `GameEvent`
directly, declaring their own fields:

- **Synthesized events** — fires the Analysis layer derives from entity state
  rather than the wire, e.g. `MolotovThrownEvent`.

(Until Sdk.GameEvents 4.0 there was a second local family — *supplementary*
events for wire fires the SDK had no record for. The SDK's curated supplement
now ships `ItemDropEvent`, `HalfTimeEvent` and `GameRestartEvent` as ordinary
payload records, so that layer is gone.)

Anything consuming an event has to handle both shapes. The rule throughout the
codebase is `Payload ?? fire`: the payload is the subject for a wire event, the
fire itself for a subclass. That single expression is what dispatch keys, type
gates, and compiled rule expressions all key on.

Events whose `EventId` has no schema entry fall back to `UnknownGameEvent`
carrying a `Dictionary<string,object>` of decoded fields.

The decoder ([`GameEventDecoder.cs`](../src/Parser/Cs2DemoKit.Parser/GameEvents/GameEventDecoder.cs))
extracts CS2-specific key types:
- Type 8 = entity/pawn handle (32-bit, in `val_long`),
- Type 9 = controller slot index (16-bit, in `val_short`).

Both are absent from the CS:GO proto spec; CS2 still emits them.

### `PayloadNode` (display tree)

`PayloadNode` is **not in the parser library**. It lives in
[`src/App/DemoViewer.NET/Models/PayloadNode.cs`](../src/App/DemoViewer.NET/Models/PayloadNode.cs)
along with `PayloadNodeBuilder.cs` and `ProtoIndex.cs`. The parser exposes
typed `IMessage` payloads; the UI walks them at display time and builds
tree nodes with byte ranges for hex highlighting.

(The parser's own `Models/` directory contains only `SubTickEvent.cs`,
`SubTickExtractor.cs`, `TickGroup.cs` — see
[`Models/SubTickExtractor.cs`](../src/Parser/DemoViewer.NET.Parser/Models/SubTickExtractor.cs).)

---

## 4. Bit-level primitives

Three load-bearing files implement the wire-format reading. A one-bit mistake
in any of them corrupts everything downstream, so change them with care and
run the parser suite afterwards.

### `Leb128Utils`

[`LEB128Utils.cs`](../src/Parser/DemoViewer.NET.Parser/LEB128Utils.cs) —
allocation-free ULEB128 utilities. The hot path is
`ParseFrameHeader(ReadOnlySpan<byte>, out FrameHeader)` which fully unrolls
the three frame-header varints into one method with a single bounds check at
the top (line 211-256). When ≥5 bytes remain, the JIT eliminates the
per-byte bounds checks; near the tail it falls back to `DecodeVarintSlow`.

Single-byte varint values (0–127, the common case) take a 2-line fast path
on every entry point (`TryReadUInt32`, `TryReadUInt64`, `TrySkip`). The
multi-byte cores are `NoInlining` to keep call sites compact.

Used by:
- `DemoParser` for frame headers + the byte-form `FindBytesField` helper.
- `DownstreamUtilities.Scan` for top-level field scanning (UI hex-view).

### `FrameHeader`

[`FrameHeader.cs`](../src/Parser/DemoViewer.NET.Parser/FrameHeader.cs) — a
`readonly struct` that holds `(Command, Tick, Size, IsCompressed)`. The
compressed flag (bit `0x40`) is stripped from the raw command varint at
decode time so the rest of the pipeline sees a clean `EDemoCommands`
value. Constructed only via `Leb128Utils.ParseFrameHeader`.

### `BitBuffer`

[`BitBuffer.cs`](../src/Parser/DemoViewer.NET.Parser/BitBuffer.cs) — a
`ref struct` bit-level reader over a `ReadOnlySpan<byte>`. Adapted verbatim
from demofile-net (MIT) with minor changes:
- Namespace adjusted,
- `Read3BitNormal` returns `System.Numerics.Vector3` (not Source's `SDK Vector`),
- `ReadBytes(int)` overload added.

Used pervasively in the entity-state layer (path-op Huffman reads, per-field
bit decoders) and by `DemoParser` for the inner-message bitstream
multiplexer. Bit-level correctness here is critical — every value type in
CS2's flattened serializer reads a specific number of bits, and a one-bit
misalignment cascades into garbage.

### `DownstreamUtilities`

[`DownstreamUtilities.cs`](../src/Parser/DemoViewer.NET.Parser/DownstreamUtilities.cs)
is the stable convenience-API surface for consumers (UI, probes, hex-view)
that need to extract or display the parser's intermediate bytes. None of it
is used by the parse pipeline itself.

It bundles:

- `GetDecompressedPayload(frame, demoBytes)` — Snappy-inflates a frame's
  payload on demand from the raw bytes the caller still owns.
- `ExtractInnerMessageBytes(frame, decompressedPayload)` — re-walks the
  inner-message bitstream to return one `byte[]?` per inner message.
- `Scan(byte[])` — scans the top-level fields of a protobuf-encoded byte
  array **without** deserialising the values, returning a `FieldSpan`
  (field number, wire type, start, end) per top-level field occurrence.
  Does not recurse into nested messages — the hex view re-invokes on
  sub-message bytes when the user expands a TreeView row.
- `TryGetPayloadRange` / `TryReadFixed32Value` / `TryReadFixed64Value` /
  `TryReadVarintValue` — sibling helpers for slicing out the value bytes
  of a `FieldSpan` for hex highlighting.

The scanner is used by the UI to compute exact byte ranges for hex
highlighting: given a field path through a decoded proto, walk back to the
wire bytes and produce a `(start, length)` range to highlight. Byte-range
correctness is load-bearing for visually-driven debugging.

---

## 5. The entity-state layer

Entity state is decoded by a separate, opt-in layer:
[`src/Parser/DemoViewer.NET.Parser/Entities/`](../src/Parser/DemoViewer.NET.Parser/Entities/).
`DemoParser.Parse` does not run it; nothing in the base parse pipeline depends
on it. Callers instantiate `EntityTracker` themselves and feed it the frames.

### Why separate from the main parse

1. **Cost.** Entity decoding is sequential by nature (every delta depends on
   the previous state of the entity) and CPU-intensive (each entity carries
   tens to hundreds of bit-level fields per tick). Forcing it into the parse
   path would block parallel decode of independent frames.
2. **Consumer choice.** The UI only needs entity state when a frame is
   selected (lazy seek). The analysis engine sometimes needs it
   (`DemoAnalyzer.BuildContext`) and sometimes doesn't
   (`BuildEventContext`, the fast path for event-only rules). CLI probes
   often want frames-only.
3. **POV-demo fragility.** Entity decoding can fail on POV / MM demos — see
   [the decode limitation below](#the-pov-demo-decode-limitation-resolved-kept-as-the-account-of-pov-wire-behaviour). Keeping it
   out of the base pipeline means game-event-derived stats (player_death,
   player_hurt, weapon_fire, …) remain correct regardless of entity-decode
   success.

### `RuntimeSchema` / `RuntimeSerializer` / `RuntimeField`

The schema describing every networked entity class. Parsed from
`CSVCMsg_FlattenedSerializer` (embedded in `CDemoSendTables`, size-prefixed:
read a uvarint, then `ParseFrom` the next N bytes).

| Type | Purpose |
|---|---|
| [`RuntimeSchema`](../src/Parser/DemoViewer.NET.Parser/Entities/RuntimeSchema.cs) | Top-level: symbol table + `(name, version) → RuntimeSerializer`. `GetSerializer(name)` is the lookup entry point. |
| [`RuntimeSerializer`](../src/Parser/DemoViewer.NET.Parser/Entities/RuntimeSerializer.cs) | One entity class. Carries `Name`, `Version`, `Fields[]`. |
| [`RuntimeField`](../src/Parser/DemoViewer.NET.Parser/Entities/RuntimeField.cs) | One field in a serializer: `TypeName`, `Encoder`, `BitCount`, `Low/High`, `EncodeFlags`, `ChildSerializerName`, `PolymorphicTypes`, `SendNode`, and a derived `FieldShape` (Atomic / Ptr / PolymorphicPtr / Vector / FixedArray / PlainStruct). |

Schema parsing is a three-pass operation
([`RuntimeSchema.Parse`](../src/Parser/DemoViewer.NET.Parser/Entities/RuntimeSchema.cs)):
all leaf `RuntimeField`s built first, then `RuntimeSerializer`s indexed,
then `ResolveChildSerializer` is called on every field so child references
resolve into actual `RuntimeSerializer` instances.

**Note on schema duplication.** The base parser's enrichment pass also calls
`TryExtractSchema` to populate `ParsedDemo.Schema`. `EntityTracker.ProcessSendTables`
([line 918](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs))
performs the same decoding when it sees the same `CDemoSendTables` frame.
This double-build is intentional: it keeps the parser stateless of the
entity replay (which makes the parse pass cleanly parallel) at the cost of
parsing the schema proto twice when both are active. The cost is negligible
(~few ms on real demos).

### `EntityTracker`

[`EntityTracker.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs)
— the stateful replay engine. 1 258 lines covering schema management,
class registry, instance baseline decoding, field-descriptor building,
field-path Huffman decode, per-field bit decode, and the decode-failure
diagnostic instrumentation.

State held:
- `_schema` — `RuntimeSchema?` (built on first `CDemoSendTables`).
- `_classIdToName` — `Dictionary<int, string>` (built from `CDemoClassInfo`).
- `_serverClassBits` — `(int)Math.Log2(serverInfo.MaxClasses) + 1`; the number
  of bits used on the wire to encode a class ID. **Only**
  `CSVCMsg_ServerInfo` writes this; `CDemoClassInfo` and `CSVCMsg_ClassInfo`
  intentionally do not (see comment at [line 556-565](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs)).
- `_instanceBaselines` — `Dictionary<int, byte[]>` (per-class baseline blobs
  from the `instancebaseline` string table).
- `_fieldDescs` — `Dictionary<string, List<FieldDescriptor>>` (cached
  per-class compiled decoder lists; built lazily on first entity-create).
- `CurrentEntities` — `EntitySet`, the live entity table (see below).
- `CurrentTick`, `CurrentFrameIndex` — last-processed positions.
- `LastEntityError` — most recent decode-failure ToString (null if healthy).
- `DeltaUnknownCount` — diagnostic counter of delta-on-unknown-entity events;
  non-zero signals a POV-style stream.

#### Seek modes

| Method | Behaviour | Use case |
|---|---|---|
| `Replay(frames)` | Process every frame in order. | Full-replay stats build (`DemoAnalyzer.BuildContext`). |
| `AdvanceTo(targetTick, frames)` | Process all frames with `tick <= targetTick`. | Tick-keyed seeking (rare; can hit multiple frames sharing a tick). |
| `AdvanceToIndex(frameIndex, frames)` | Process frames `[0..frameIndex]` inclusive. | Frame-accurate seeking — preferred over `AdvanceTo` because DEM_FullPacket frames can share ticks. |
| `AdvanceToIndexWithSnapshot(snapshotAt, frameIndex, frames)` | Advance to `snapshotAt`, snapshot all fields, then continue to `frameIndex`. | Pre-event reads (e.g. preHitHp before damage is applied). |
| `PeekEntityUpdates(msg)` | Read-only decode of a single `CSVCMsg_PacketEntities` returning a `List<EntityUpdateInfo>` without mutating `CurrentEntities`. | UI "show me the entity diff in this packet" without rewinding. |
| `SnapshotCurrentFields()` | Deep-copy the entire `EntitySet` to `Dictionary<int, Dictionary<string, object?>>`. | Anywhere a non-mutating live view is needed. |

`Replay`, `AdvanceTo`, and `AdvanceToIndex` all funnel into `ProcessFrame`
which iterates `frame.InnerMessages` and dispatches each `Payload` to the
right handler.

#### Important behaviour: `DEM_FullPacket` is a checkpoint, not new data

`ProcessFrame` ([line 570](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs))
**skips** `CSVCMsg_PacketEntities` inside `DEM_FullPacket` frames because
those packets re-deliver state we have already received from prior
`DEM_Packet` frames. Replaying them double-creates entities and cascades
into bit-misalignment ~5 packets later. This was one of the four compounding
bugs fixed in the May 2026 entity-decode correctness pass.

### `FieldPath` and `FieldPathEncoding`

CS2 entity deltas address fields by a **path** through the (nested) field
tree of an entity class. The path is a sequence of integers, capped at
**7 entries** ([`FieldPath.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldPath.cs)):

```csharp
private int _path0; ... private int _path6;  // 7 slots, struct-inlined
```

A 7-slot cap is what demofile-net uses (verified against its `FieldPath.cs`).
7 is sufficient for every observed real CS2 entity; an 8th-slot push
indicates upstream bit misalignment.

[`FieldPathEncoding.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldPathEncoding.cs)
defines **39 path-mutation opcodes** (`PlusOne`, `PushOneLeftDeltaZeroRightZero`,
`PopAllButOnePlusOne`, `NonTopoComplex`, `FieldPathEncodeFinish`, etc.) with
empirical frequencies. The decoder is a pre-built Huffman tree
([`HuffmanNode.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/HuffmanNode.cs)):
each op is read bit-by-bit until a leaf, then its `Reader` mutates the path
in place. The tree is built once at static-class init.

`FieldPathEncodeFinish` (frequency 25 474, the most common op) has a `null`
Reader — that's the sentinel meaning "no more field paths in this entity".

### `FieldDecoder` and `FieldDecoderFactory`

[`FieldDecoder.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldDecoder.cs)
defines three delegate shapes:

```csharp
internal delegate object?  FieldDecoder(ref BitBuffer buffer);  // boxed
internal delegate int      IntDecoder(ref BitBuffer buffer);    // unboxed
internal delegate float    FloatDecoder(ref BitBuffer buffer);  // unboxed
```

[`FieldDecoderFactory.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldDecoderFactory.cs)
inspects a `RuntimeField` and returns the right decoder. `TryCreateInt` /
`TryCreateFloat` are tried first for scalar types to avoid boxing on the
hot decode path; everything else falls back to the boxed `Create`.

The factory handles every CS2 wire-encoded scalar type:
- Integers — `bool`, `uint8`, `int32`, `CEntityIndex`, `CUtlStringToken`,
  `CGlobalSymbol`, `CPlayerSlot`, `GameTick`, etc.
- Floats — `float32`, `GameTime` (raw 32-bit), `CNetworkedQuantizedFloat`
  (bc<32 quantised, bc≥32 raw — a decode bug fixed during the POV-demo
  investigation).
- Complex (boxed) — strings, `Vector`/`QAngle`, `Color`, encoder-specific
  paths (`coord`, `simtime`, `runetime`).

### `EntitySet` and `EntityState`

[`EntitySet.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntitySet.cs) —
a fixed `EntityState?[16384]` (matching Source's `MAX_EDICTS = 1 << 14`).
Enumeration helpers: `All()`, `AllInPvs()`, `OfClass(className)`,
`AllIndexed()`, `Snapshot()`.

`GetOrCreate(index, className, serial)` reuses an existing slot **only when
the class name matches** — a slot reused with a different class would point
at the wrong serializer and silently misalign the wire bit stream.

[`EntityState.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityState.cs)
holds one entity's networked fields. Storage is split across three
dictionaries to avoid boxing on the hot path:

```csharp
Dictionary<string, object?> _fields;       // complex types
Dictionary<string, int>     _intFields;    // typed
Dictionary<string, float>   _floatFields;  // typed
```

`Fields` (the public read API) merges them into a fresh dictionary on each
call — display-only, never use on hot paths. `Get<T>` / `TryGet<T>` /
`this[path]` are the cheap typed readers.

Empirical field-storage conventions:
- Handles arrive as `UInt64`,
- Bools as `Int32` (0/1),
- Sub-entities flattened under `m_pXxxServices.m_yyy`,
- Arrays use `[N]` not `.NNN`.

### The POV-demo decode limitation (resolved; kept as the account of POV wire behaviour)

This section describes an entity-state decoder failure on POV / MM-recording demos that
**no longer reproduces**: a mid-2026 decode fix (instancebaseline string-table +
AnimGraph2 field widths) cured it, and `tools/EntityDecodeProbe` now reports zero decode
errors on all 5 bench MM demos. The delta-on-unknown analysis below remains a correct
account of how POV recordings differ on the wire (the skip and its `DeltaUnknownCount`
diagnostic are still in the code), but its conclusion — that the skip itself desynced the
cursor — was superseded: decode is clean on the same demos while delta-on-unknown events
still occur. Details in `KNOWN-AND-SUSPECTED-ISSUES.md`.

**Symptom (historical):** `EntityTracker.LastEntityError` was set on all 5 bench MM demos
(crashes at packet #37 with `FieldPath is full`). Furia HLTV demos parsed
cleanly.

**Root cause (fully diagnosed):** bench MM demos are **POV recordings**.
The wire sends DELTAS for entities the recording client never received an
`ENTERPVS` for. Our parser hits `state is null` on these deltas and
currently skips with `continue` — which consumes only the prelude bits
(`UBitVar entityIndex` + 2 flags = 8–16 bits) but NOT the path-op + value
bits that the wire encoded for that entity. The cursor falls behind the
wire every time this happens, and the misalignment cascades.

Counts (from issue doc):

| Demo | Entities created | Delta-on-unknown events |
|---|---|---|
| Furia HLTV | 359 | 0 |
| Bench MM demo 1 | 6 793 | 87 175 |

**demofile-net is no help here** — it `throw`s on delta-on-non-existent
([dfn `DemoParser.Entities.cs:341`](https://github.com/saul/demofile-net)).
Doesn't crash on Furia (count = 0) but would panic on bench MM immediately.

**Three fix candidates:**

- **A. POV-demo entity tracking via secondary signal** — if CS2 exposes a
  full slot→class map at recording start (CDemoFileHeader, CDemoSpawnGroups,
  CDemoStringTables, CSVCMsg_HltvStatus, ...), populate state from there
  before any deltas. Unknown if such a signal exists.
- **B. Heuristic class assignment by slot range.** Tried (CWorld fallback),
  made things worse — wrong field schemas consume wrong bit counts.
- **C. Document as a known limitation.** Game-event-derived stats still
  work (player_death, player_hurt, etc.) because they're parsed from a
  separate stream that doesn't depend on entity tracking. Tests gated by
  `SkipIfEntityDecodeFailed(tracker)`.

**What works regardless:**
- Every `GameEvent` subtype (272 of them) is decoded by `GameEventDecoder`
  from the `CMsgSource1LegacyGameEvent` proto stream — independent of
  entity tracking.
- `ParsedDemo.AllGameEvents` is always populated correctly.
- All player-stat rules that read from game events (kills, deaths, damage,
  shots fired, etc.) are unaffected — the bench suite still produces
  correct stats on all 5 affected demos.

**What is impacted:**
- Per-tick HP / weapon / armor entity reads from `EntityTracker.CurrentEntities`.
- Phase 2 entity-state HP source in damage-cap enrichment (falls back to
  event-cache HP, which is what the bench-suite stats already use).
- The 6 deferred `EntityIntegrationTests` listed in the known-issues entry.

Diagnostic instrumentation in place (added during the investigation):
- `EntityTracker.DeltaUnknownCount` — public counter; non-zero signals
  POV-style stream.
- Per-packet decode trace ring buffer in `ReadEntityFields`; dumped on
  first `LastEntityError` with bit positions + "top 30 outliers" view.
- `tools/EntityDecodeProbe` — see [§8 Tooling reference](#8-tooling-reference).

---

## 5b. Schema Lens — the typed-wrapper layer

Sitting on top of the entity-state layer is **Schema Lens** — a wire-stable
mapping between volatile CS2 engine field names and a stable C# Tier-3 wrapper
API. The living as-built account of the whole stack — including the SDK derivation that now
produces the lens registry — is [`docs/entity-stack.md`](entity-stack.md). The original design
narrative and phased migration plan (the `docs/schema-lens/` docs) were retired 2026-08-16 once
the SDK derivation superseded them; the full text is in git history. This section is the
architectural-level entry point.

### The 3-tier mapping

```
[ Tier 1: bitstream ]  svc_PacketEntities.entity_data
            │                (Huffman path ops + per-field bit decode)
            ▼
[ Tier 2: lane/slot bridge ]  EntityState lanes
            │   • _intLane     : int[]
            │   • _floatLane   : float[]
            │   • _objectLane  : object?[]
            │   • _seenInt / _seenFloat / _seenObj bitvectors
            │   • _fallback   : Dictionary<string, object?>
            ▼
[ Tier 3: typed wrappers ]   CCSPlayerPawn, CCSPlayerController, …
                (curated, generated; live views over the lanes)
```

The runtime owns Tier 1 (`EntityTracker` + decoder family, §5 above) and
Tier 2 (`EntityState` lanes, this section). Tier 3 is the **SDK-emitted typed
wrappers** (`CS2OpenDev.Sdk.Entities`, bound over the runtime through the
`Entities/SdkAbstractions/` seam). The local codegen-emitted wrapper tier that
previously sat here was deleted in the second stage of the cutover; only the
Schema Lens registry (`Generated/SchemaLens.Generated.cs`) is still
codegen-emitted.

### Architectural steer: lanes are mutable truth, `Fields` is projection

`EntityState` no longer stores fields in three dictionaries. It stores them
in three typed lane arrays (`int[]`, `float[]`, `object?[]`) plus a fallback
dict for fields the planner couldn't slot. Each lane has a parallel
`_seen[]` bitvector so the projection can distinguish "lane default 0/0.0/null"
from "not received yet" — the seen-tracking contract that lets snapshot diffs
ignore unwritten cells.

The familiar public `EntityState.Fields` API (an
`IReadOnlyDictionary<string, object?>`) is preserved as a **computed
projection**: on every call it materialises a fresh dictionary by merging
the lane cells whose `_seen[]` bit is set with the `_fallback` dict, keyed
on the per-class path map. This is bit-for-bit compatible with the
pre-Schema-Lens behaviour the Analysis layer's `SchemaKeysAssertionTests`
witness, and that is the load-bearing compatibility bar — the projection is
display-only, never used on hot paths.

Hot-path readers go through the new path-keyed API:

```csharp
state.Get<int>("m_iHealth");     // routes to int lane via slot map
state.TryGet<float>("m_flStamina", out var s);
state["m_hController"];          // returns raw boxed handle
```

Typed wrapper reads bypass the path lookup entirely — they consult a
codegen-emitted per-class `XxxSlots` static class for the lane + slot index
and call `state.GetIntSlot(slot)` / `GetFloatSlot(slot)` / `GetObjectSlot(slot)`
directly.

### Descriptor walk consults the Lens at first sighting

`EntityTracker.BuildFieldDescs` walks the `RuntimeSchema` spine the first
time it sees a serializer and produces the `FieldDescriptor` tree the
decoder uses. The Schema Lens hooks in here in two places:

1. **Pre-pass slot reservation.** Before any descriptor `Allocate(...)` runs,
   `EntityTracker.PreReserveLensSlots` walks the same spine and reserves
   every Lens-pinned slot via `ClassShapeBuilder.ReserveLensSlot`. This
   answers the auto-increment-vs-pin collision that would otherwise let a
   freshly-walked descriptor steal a slot the codegen already published as
   the canonical home for `m_iHealth`. The auto-increment branch skips
   reserved slots.
2. **Per-field LensSlot lookup.** During the walk, each leaf descriptor
   consults `tracker.BindLensResolver` (a `Func<string, string, LensSlotRule?>`
   set at construction). If the resolver returns a rule, the descriptor uses
   `(LaneKind, Slot)` from the rule; otherwise it falls back to the
   auto-incrementing lane assignment. Unmapped fields go to the
   `Fallback` lane (the dict path), preserving every consumer that reads via
   `state.Fields["…"]`.

The cost of the Lens consult is paid once per (class, field) pair on first
sighting and cached in the per-class `ClassShape`.

### Codegen → runtime contract

The codegen output and the runtime tracker meet at exactly four surfaces:

| Surface | Owner | Notes |
|---|---|---|
| `LensRegistry` / `LensState` | Codegen (`Generated/SchemaLens.Generated.cs`) | Post-replay snapshot of every aliasing + slot decision. |
| `LensHash` | Codegen | sha256 over the canonical-form LensState; compared at startup against a runtime-recomputed hash. Mismatch ⇒ regenerate. |
| ~~`<NetName>Slots.g.cs`~~ | — | Retired with the local wrapper layer in the second stage of the cutover. Slot planning still runs inside the `--schemalens` emit; `PreReserveLensSlots` consumes the lens state directly. |
| `LensResolverBridge.Build(LensState)` | Codegen-emitted Entities-side bridge | Returns the `Func<string, string, LensSlotRule?>` the runtime calls. Required because the project graph forbids `EntityTracker` from naming `LensState` directly. |

A lane-routing edge case is worth flagging: `wireType: int` with
`transform: HandleIndex` declares a handle field, but CS2 networks handles as
varint `UInt64` / `UInt32` which don't fit `Int32`. The codegen recognises
this and routes the slot to the **Object lane**; readers (the SDK seam's
`LensBoundReader.TryReadEntityHandle`) unbox it with an unchecked fold. The
Lens declaration's `wireType: int` is preserved verbatim (it's the
*declared* type for downstream tooling); the routing override happens in
the codegen planner.

### Bootstrap pattern

Analysis (the top of the wire) is responsible for wiring the runtime to its
Tier-3 face. The pattern, applied once per `EntityTracker`:

```csharp
var tracker = EntityTrackerFactory.CreateCurated();   // binds the Schema Lens resolver
SdkEntityWorlds.For(tracker);                         // (Analysis layer) registers the SDK
                                                      // wrapper factories for Get<T>/Resolve<T>
```

Factory registration goes through `TrackerEntityWorld.RegisterWrapper`, which
installs the SDK package's own `EntityWrapperRegistry` factories — one factory
per class, replacement-on-reregister. After registration, the four public
Tier-3 APIs on `EntityTracker` light up:

| API | Returns | Semantics |
|---|---|---|
| `Get<T>(int slot)` | `T?` | Live view; every read re-resolves through the current `EntityState`. |
| `Snapshot<T>(int slot)` | `T?` | Wrapper over a detached `FreezeCopy` — scalar reads are frozen; handle companions resolve live through the wrapper's world. |
| `ResolveHandle<T>(int rawHandle)` | `T?` | Masks, sentinel-checks, dereferences via `CurrentEntities[index]`. |
| `GetFieldMeta(string className, string path)` | `RuntimeField?` | For tools / diagnostics. |

`Get<T>` is constrained `where T : class` deliberately: the tracker never
names a wrapper base type (production factories produce
`CS2OpenDev.Sdk.Entities.EntityWrapper` subclasses the tracker cannot
reference). The factory registry handles the cast.

### What's V1.5 (deliberate scope cuts)

- **Nested snapshot trees.** `Snapshot<T>`/`SnapshotNode` freeze one entity
  (detached `FreezeCopy` + a frozen `Fields` clone). The recursive
  nested-handle freeze that the local wrappers' `SnapshotInto` overrides once
  provided was removed with that layer in the cutover — SDK wrappers resolve
  handles live through `IEntityWorld` instead.
- **Array lane views.** The intended end state is arrays landing as a single
  object-lane slot holding a typed `IReadOnlyList<TElement>`. V1 keeps the
  per-element fallback-dict path (today's behaviour). The 14-class curated
  set's `m_hMyWeapons[i]` reads stay on `state.Fields`.
- **Curated set expansion.** V1 ships wrappers for 14 hand-picked classes
  (the ones touched by current Analysis providers). Concrete weapon
  classes (`CWeaponAK47`, etc.) and the rest of the entity zoo are V2.
  Analysis Phase 5c migrated the simple-field reads on `CCSPlayerPawn` and
  `CCSPlayerController`; the m_hMyWeapons array iteration and weapon
  ClassName reads stay on the dict path.

These cuts were tracked in the retired Schema Lens implementation plan §9 (git history).

---

## 6. Downstream consumers

### The Avalonia desktop app

Entry point: [`MainViewModel.cs`](../src/App/DemoViewer.NET/ViewModels/MainViewModel.cs)
(2 946 lines; large because it's a partial class spanning every UI panel).

How it consumes the parser:

1. **Parse** — calls `DemoParser.Parse(bytes)` on file open. Adds every
   `parsed.Frames` entry to the observable `Frames` collection
   ([line 385](../src/App/DemoViewer.NET/ViewModels/MainViewModel.cs)).
2. **MessageCards** — on frame selection, calls
   `DownstreamUtilities.GetDecompressedPayload(frame, demoBytes)` to inflate,
   then `DownstreamUtilities.ExtractInnerMessageBytes(frame, payload)` to
   get per-message byte ranges. Builds one `MessageCardViewModel` per inner
   message.
3. **Entity tracking** — owns a single `EntityTracker` instance
   (`_currentTracker`, [line 104](../src/App/DemoViewer.NET/ViewModels/MainViewModel.cs))
   that it advances via `AdvanceToIndex(frameIndex, frames)` when the user
   selects a frame. Uses `AdvanceToIndexWithSnapshot` to capture pre-frame
   state. Uses `PeekEntityUpdates` to show per-packet entity diffs without
   altering live state.
4. **Hex highlighting** — drives `HexViewModel.SetHighlights` from
   `NetMessage.DecompressedStart` / `Length` plus `PayloadNode` byte ranges
   from `PayloadNodeBuilder` (UI-side helper that walks the typed
   `IMessage` and annotates byte spans).
5. **Parse Chain** — `ProtoIndex` (UI-side, [`Models/ProtoIndex.cs`](../src/App/DemoViewer.NET/Models/ProtoIndex.cs))
   regex-scans `cs2-opendocs/data/Protobufs/*.proto` at startup so the
   bottom strip can link field names → local file:line or GitHub URLs.

### The Analysis engine

Entry point: [`DemoAnalyzer.cs`](../src/Analysis/DemoViewer.NET.Analysis/DemoAnalyzer.cs).
Builds a [`DemoContext`](../src/Analysis/DemoViewer.NET.Analysis/DemoContext.cs)
from a `ParsedDemo`.

Two construction modes:

| Method | Replays entities? | Use case |
|---|---|---|
| `DemoAnalyzer.BuildContext(demo)` | Yes — full `EntityTracker.Replay` | Stat rules that need entity-state reads. |
| `DemoAnalyzer.BuildEventContext(demo)` | No — empty tracker | Event-only rules (much faster). |
| `DemoAnalyzer.BuildContextAsync(demo)` | Yes, on the thread pool | UI / async callers. |

`DemoContext` carries:
- `Demo` — the original `ParsedDemo`,
- `Rounds` — derived from `RoundFreezeEndEvent` / `RoundOfficiallyEndedEvent` /
  `RoundEndEvent`,
- `EntityState` — the `EntityTracker` (empty on the event-only path),
- A type-keyed event index (`EventsOfType<T>()` → `IReadOnlyList<T>`,
  O(1) per type-key with caching),
- `EventsInRange(fromTick, toTick)` — binary-search slice.
- `CreateEntityLayer()` — returns a fresh `EntityStateLayer` so each
  parallel rule branch can seek independently (see below).

[`EntityStateLayer`](../src/Analysis/DemoViewer.NET.Analysis.Abstractions/EntityStateLayer.cs)
wraps an `EntityTracker` for **incremental forward-only** seeking. Each
parallel rule branch calls `CreateEntityLayer()` to get its own
single-threaded layer (the underlying tracker is not thread-safe). Seeking
backward is a no-op; `Reset()` rebuilds the tracker from frame 0.

[`EntityChangeScanner`](../src/Analysis/DemoViewer.NET.Analysis/EntityChangeScanner.cs)
runs per-evaluator and synthesises `EntityChangeMessage` events when
registered field providers cross emission edges (the "edge detection" layer
that lets YAML rule chains react to entity-state changes).

Plugged in via the `StateGraphEvaluator` and the rule-chain YAML loader; the
specifics are out of scope for this doc (see `docs/analysis-engine/Analysis-Engine-Design.md`).

### The CLI tools

| Tool | What it does |
|---|---|
| `tools/AnalysisBench` | Runs the full parse + analysis pipeline against the bench suite (`demos/benchmarks/*.dem`) and produces per-stat accuracy reports. Supports `--suite`, `--shots-debug`, `--round-debug`, plus opt-in profiling (`--profile` per-phase trees, `--counters`/`--timeline`/`--trace`, `DEMOVIEWER_PROFILE=1`) — see [`profiling.md`](./profiling.md). |
| `tools/EntityDecodeProbe` | Replays a single demo through `EntityTracker` and dumps `LastEntityError` + the decode trace. Sub-modes: `--schema` (dump parsed schema for named classes), `--descriptors` (dump runtime FieldDescriptor list), `--field-bytes` (raw `ProtoFlattenedSerializerField_t` bytes). Confirms whether the POV-demo decode failure fires on a given demo. |
| `tools/DemoViewer.NET.DemoSourceDetails` | Extracts header / source-identification fields from a demo (server name, client name, profile classification). |
| `tools/DemoViewer.NET.EntityFieldDiff` | Compares entity-field snapshots across two demos / two ticks. |
| `tools/DemoViewer.NET.Codegen` | Emits `Entities/Generated/SchemaLens.Generated.cs` (the lane-binding lens registry) via `--schemalens`. The wrapper/slot emitters were retired in the cutover — typed wrappers ship in `CS2OpenDev.Sdk.Entities`; game events come from `CS2OpenDev.Sdk.GameEvents`. |

---

## 7. Wire-format notes

### The cs2-opendocs submodule

[`cs2-opendocs/`](../cs2-opendocs/) is a git submodule
(`sid2934/CS2-OpenDevDocs`) pulled with `--recursive` so its inner `data/`
sub-submodule (`SteamDatabase/GameTracking-CS2`) also lands.

| Submodule path | What's there |
|---|---|
| `cs2-opendocs/data/Protobufs/*.proto` | Reference `.proto` definitions, used for browsing and for the Parser tab's source-link index (`ProtoIndex`). The parser itself runs no protoc — the generated message types ship prebuilt in the `CS2OpenDev.Protos` package. |
| `cs2-opendocs/docs/schemas/server.md` | Server-side entity schema (human-readable). |
| `cs2-opendocs/docs/proto/*.md` | Per-proto documentation. |
| `cs2-opendocs/docs/gameevents_schema.json` | Historical input for the retired `Codegen --gameevents`. No longer read by anything in this repo. |

GitHub web fallback (used when the local submodule isn't present):
`https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/`.

### Where the Huffman tree for FieldPath encoding lives

The 39 ops + frequencies are hand-coded in
[`FieldPathEncoding.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldPathEncoding.cs)
(static `HuffmanRoot` field). The static constructor builds the tree once
at first access. Frequencies are copied verbatim from demofile-net.

### How demofile-net maps to our code

[demofile-net](https://github.com/saul/demofile-net) is the MIT-licensed
.NET CS2 demo parser we treat as the **ground-truth oracle** for parser
output. It is never taken as a dependency — comparison only — but keeping a
local clone as a sibling checkout is worth it for side-by-side reading.

Direct ports of demofile-net code (MIT) in our parser:

| Our file | Their file |
|---|---|
| [`BitBuffer.cs`](../src/Parser/DemoViewer.NET.Parser/BitBuffer.cs) | `BitBuffer.cs` — verbatim with namespace + minor changes |
| [`FieldPath.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldPath.cs) | `FieldPath.cs` — verbatim |
| [`FieldPathEncoding.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldPathEncoding.cs) | adapted (op list + Huffman build) |
| [`HuffmanNode.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/HuffmanNode.cs) | adapted |
| [`FieldDecoderFactory.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldDecoderFactory.cs) | adapted from `FieldDecode.cs` |
| `EntityTracker.cs` | adapted from `DemoParser.Entities.cs` |

Key architectural differences:

- **They use codegen.** Each entity class has a generated C# class with
  per-field strongly-typed decoders. We use **runtime schema** (parsed at
  startup from `CSVCMsg_FlattenedSerializer`) so we don't need a build
  step when the schema changes.
- **They panic on misaligned state** (e.g. throw on delta-on-non-existent).
  We catch into `LastEntityError`, so MM demos at least decode events
  even when entity state goes off the rails (§5).
- **`_serverClassBits` is only written from `CSVCMsg_ServerInfo`** in both
  codebases (we converged to this in the May 2026 pass).

---

## 8. Tooling reference

| Tool / probe | What it gives you | When to use |
|---|---|---|
| `dotnet test DemoViewer.NET.Parser.Tests` | Parser unit + integration tests (TUnit). | Default verification after parser changes. |
| `dotnet run --project DemoViewer.NET.Desktop` | Avalonia UI — frame browser, payload tree, hex view, entity panel, parse chain links. | Interactive debugging of a single demo. |
| `tools/AnalysisBench --suite` | Per-demo accuracy %, per-stat mismatch breakdown across the bench suite. | Regression sweeps. |
| `tools/AnalysisBench --shots-debug=<player>` | Per-event timeline + damage-cap instrumentation + post-death-shot count for one player. | Diagnosing per-player stat divergences. |
| `tools/AnalysisBench --round-debug` | Per-round event trace (one demo). | Diagnosing round-boundary issues. |
| `tools/AnalysisBench --profile` / `--counters` / `--timeline` | Per-phase parse+entity profile trees / evaluator counters / phase timeline (all runtime-gated; all compose with `--suite`). | Performance profiling — see [`profiling.md`](./profiling.md). |
| `tools/EntityDecodeProbe <demo>` | Replays one demo through `EntityTracker` and dumps `LastEntityError`. | Confirming whether entity decode survives a demo. |
| `tools/EntityDecodeProbe --schema <demo> [classes]` | Dumps the parsed schema for one or more named classes (per serializer version). | Diffing schema versions across demos. |
| `tools/EntityDecodeProbe --descriptors <demo> [Class[i][j]…]` | Dumps the runtime `FieldDescriptor` list the parser uses at decode time. | Confirming descriptor / schema alignment. |
| `tools/EntityDecodeProbe --field-bytes <demo> <names-csv>` | Dumps raw `ProtoFlattenedSerializerField_t` bytes for named fields. | Side-by-side wire-shape diff when hunting decode bugs. |
| `tools/DemoViewer.NET.DemoSourceDetails <demo>` | Header fields + auto-classified `DemoProfile`. | Source classification debugging. |
| `tools/DemoViewer.NET.EntityFieldDiff` | Compares entity-field snapshots across two demos / two ticks. | Schema-divergence root-causing. |
| `bench-reports/*.json` | Persistent per-run snapshots; diff vs older runs. | Regression detection. |

---

## 9. Glossary

| Term | Meaning |
|---|---|
| **tick** | One server simulation step. CS2 matchmaking is 64-tick (`TickInterval = 1/64 s`). `DemoFrame.ServerTick` is the **game tick** (gameplay starts at 1, pre-game uses a negative sentinel). `serverTick` in some contexts means the absolute server tick (`gameTick + ServerStartTick`). |
| **frame** | One `EDemoCommands` entry in the .dem file. `DemoFrame`. Multiple frames can share a tick (e.g. `DEM_FullPacket` is interleaved at the same tick as the next regular packet). |
| **PVS** (Potentially Visible Set) | The server-side cull of entities that are sent to a given client/observer based on map geometry. An entity "enters PVS" when it starts being networked to the recipient, "leaves PVS" when it stops. |
| **FullPacket** (`DEM_FullPacket`) | A seek checkpoint frame written every N ticks. Bundles a `CDemoStringTables` snapshot with a re-broadcast `CDemoPacket` containing the full current entity state. **The parser library decodes them; the entity tracker explicitly skips the `PacketEntities` inside them to avoid double-delivery.** |
| **ENTERPVS** | A flag in `svc_PacketEntities.entity_data` indicating "new entity entering this slot." Wire shape: `classId (N bits) + serial (17 bits) + UVarInt32 spawngroup`. |
| **LEAVEPVS** | A flag indicating "entity leaving this slot." May or may not be combined with `FHDR_DELETE` for full destruction. |
| **baseline** (instance baseline) | Per-class initial field-value blob shipped via the `instancebaseline` string table. Applied before the entity's own bytes on first ENTERPVS so that unset fields take class defaults. |
| **delta** | A `svc_PacketEntities` update for an existing entity — sends only changed fields. Encoded as a list of `(field-path-op, value)` pairs against the previous state. |
| **field path** | A path through the (possibly nested) field tree of an entity class. Up to 7 integers; encoded on the wire as a Huffman-coded stream of mutation opcodes. |
| **flattened serializer** | The CS2 entity schema (`CSVCMsg_FlattenedSerializer`), shipped once per demo in `CDemoSendTables`. Describes every networked class, its fields, types, and per-field encoding metadata. |
| **GOTV / HLTV** | The two CS2 broadcast-relay modes. GOTV is the Valve in-engine relay; HLTV is the pro-broadcast relay. They emit slightly different event sets — see `DemoFeatureSet`. |
| **POV demo** | A first-person client-recorded demo (as opposed to a server-relayed GOTV/HLTV stream). The recording client only sees what was in its own PVS, which historically made entity-state decoding fragile — see the resolved decode limitation in §5. |
| **UBitVar / UVarInt32** | Source-engine bit-level varint encodings. `UBitVar` is the 6/10/14/34-bit encoding used for type IDs and entity indices. `UVarInt32` is standard protobuf varint (multiples of 8 bits). |

---

## 10. Where to start reading

### "I want to add a new game-event handler."

1. Confirm the event has a record in `CS2OpenSchema.Events` — that is, in the
   `CS2OpenDev.Sdk.GameEvents` package. Nothing is generated locally, so if the
   record is missing the fix is a package bump, or an upstream ask if the SDK
   doesn't know the event either.
2. Read the payload off the envelope — `gem.DecodedEvent.Payload is TheNewEvent`
   over `frame.InnerMessages.OfType<GameEventMessage>()`, or
   `DemoContext.EventsOfType<TheNewEvent>()`, which takes the **payload** type
   and hands back the `GameEvent` envelopes carrying it.
3. Field names are the SDK's property names, reachable from rules as
   `event.<Property>`. `rules/catalog.json` is generated by reflecting over
   those records, so it is the authoritative spelling — check there rather than
   guessing from the wire name (`dmg_health` → `DmgHealth`; since Sdk 4.0 the
   old unsplit stragglers are gone too — `noreplay` → `NoReplay`).
4. If the event fires on the wire but the SDK has no record for it, report it
   upstream as a curated-supplement candidate (`game-event-supplement.json` in
   the CS2OpenDev-SDK repo — the route `item_drop`/`halftime`/`game_restart`
   took, [CS2OpenDev-SDK#3](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/3)).
   Until it ships, such fires decode as `UnknownGameEvent`.

### "I want to fix a decode bug."

1. Reproduce in [`tools/EntityDecodeProbe`](../tools/EntityDecodeProbe/Program.cs)
   — `dotnet run --project tools/EntityDecodeProbe -- <demo>`. If the bug
   is entity-state-related, this prints the decode trace.
2. For wire-format issues, the entry points are:
   - **Bit-level read shape:** [`Entities/FieldDecoderFactory.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldDecoderFactory.cs)
     — the factory that picks a decoder per type.
   - **Field-path opcodes:** [`Entities/FieldPathEncoding.cs`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/FieldPathEncoding.cs)
     — all 39 path-mutation ops.
   - **Per-frame dispatch:** [`Entities/EntityTracker.cs:ProcessNetMessage`](../src/Parser/DemoViewer.NET.Parser.EntityTracking/EntityTracker.cs)
     (line 686).
3. Compare bit-by-bit against a local demofile-net checkout (`src/DemoFile/`
   — start at `DemoParser.Entities.cs`). Read-only; never wire it as a
   dependency.
4. Add a TUnit test in `tests/DemoViewer.NET.Parser.Tests/`. Heavy parser
   tests need `[NotInParallel]`.

### "I want to consume entity state in a tool."

1. `byte[] bytes = File.ReadAllBytes(demoPath);`
2. `ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());`
3. `var tracker = new EntityTracker();`
4. Either:
   - Full replay: `tracker.Replay(parsed.Frames);` then walk
     `tracker.CurrentEntities.AllInPvs()`.
   - Seek to a tick: `tracker.AdvanceToIndex(targetFrameIdx, parsed.Frames);`.
5. **Always check** `tracker.LastEntityError` afterwards — if non-null, the
   tracker hit a decode error and stopped emitting events (the historical
   POV-demo failure in §5 is cured, so a non-null error today means a genuinely
   new problem). Game events from `parsed.AllGameEvents` are still complete.
6. See [`tools/EntityDecodeProbe/Program.cs`](../tools/EntityDecodeProbe/Program.cs)
   for a compact example.

### "I want to add per-frame UI behaviour."

[`src/App/DemoViewer.NET/ViewModels/MainViewModel.cs`](../src/App/DemoViewer.NET/ViewModels/MainViewModel.cs)
is the single source of truth for what happens on frame selection. Search
for `OnSelectedFrameChanged` and `HandleCardSelected`. The `MessageCards`
collection is rebuilt on every selection; payload-node trees are
pre-populated at frame-load time.

### "I want to add a new player stat."

This is an Analysis-engine task, not a parser task. The parser already
exposes everything you need via `ParsedDemo`. Read
`docs/analysis-engine/Analysis-Engine-Design.md`, then look at
`src/Analysis/DemoViewer.NET.Analysis/PlayerStats/` for existing stat
plugins and `rules/player-stats.yaml` for the rule definitions.
