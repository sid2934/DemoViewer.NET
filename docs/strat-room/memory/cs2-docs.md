# CS2 Developer Reference — Key Facts

Source: https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/AGENTS.md
Site: https://cs2opendev.github.io/CS2OpenDev-Docs
Docs updated every 4 hours from SteamDatabase/GameTracking-CS2.

> Moved 2026-09: was `sid2934/CS2-OpenDevDocs`, whose raw `docs/proto/*` and
> `docs/schemas/*` paths now 404. Layout changed too, so old URLs cannot be fixed
> by swapping the org alone. Verified against build 25175329 (schema 0.10.0).

## Architecture
- CS2 is Source 2. Entity system uses **controller/pawn split**.
- Controller (`CCSPlayerController`): persistent per-client, survives round resets.
- Pawn (`CCSPlayerPawn`): in-world body, recreated each round. Controller → pawn via `m_hPlayerPawn`.
- All server entities: `CEntityInstance` → `CBaseEntity`. Client mirrors prefixed `C_`.

## Key Entities & Docs
| Entity | Docs URL |
|--------|----------|
| `CBaseEntity` | https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/server/CBaseEntity.md |
| `CCSPlayerController` | https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/server/CCSPlayerController.md |
| `CCSPlayerPawn` | https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/server/CCSPlayerPawn.md |
| `CCSGameRules` | https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/server/CCSGameRules.md |
| `CCSWeaponBase` | https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/server/CCSWeaponBase.md |
| `CPlantedC4` | https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/server/CPlantedC4.md |

## Team Numbers
- 0 = Unassigned, 1 = Spectator, 2 = T, 3 = CT

## Game Phase (`CCSGameRules.m_gamePhase`)
- 1 = First Half, 2 = Second Half, 3 = Pre-OT, 4 = OT, 5 = Game Over

## Demo Format
- CS2 demos: Source 2 "PBDEMS2" binary format.
- Key protos: `CDemoHeader`, `CDemoPacket`, `CDemoFullPacket`, `CDemoStringTables`, `CDemoClassInfo`
- Demo proto raw: https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/demo.proto

## Common Task Lookup
| Task | Reference |
|------|-----------|
| Parse demo file | `demo.proto`, `netmessages.proto`, `CCSGameRules` |
| Player position/health | `CCSPlayerPawn` in server schema |
| Player money/economy | `CCSPlayerController_InGameMoneyServices` |
| Round state | `CCSGameRules.m_bFreezePeriod`, `m_gamePhase`, `m_bWarmupPeriod` |
| Kill/damage events | `cs_gameevents.proto` → `CMsgSource1LegacyGameEvent` |
| Weapon properties | `CCSWeaponBase`, `CCSWeaponBaseVData` |
| Bomb events | `CPlantedC4`, `cs_gameevents.proto` |
| Player commands | `cs_usercmd.proto` → `CCSUsrCmd` |

## Raw Doc URLs for Fetching

Machine-readable first — these are generated, not hand-written:
- Full schema (3,779 classes): `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/cs2_schema.json`
- Field history (when a field appeared/vanished, by build): `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/field_history.json`
- Game events (289): `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/gameevents.json`
- Wire IDs (message id -> type): `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/network.json`
- ConVars (3,955): `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/convars.json`
- Items: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/data/items.json`

Protos (real `.proto` text, not Markdown):
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/demo.proto`
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/netmessages.proto`
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/cs_gameevents.proto`
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/cs_usercmd.proto`
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/downstream-codegen-schemas/proto/usercmd.proto`

Per-class pages: `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/generated/schemas/<module>/<TypeName>.md` (module = `server` | `client`).

Curated overlays (hand-written notes, treat as hints not proof):
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/overlays/server.yml`
- `https://raw.githubusercontent.com/CS2OpenDev/CS2OpenDev-Docs/main/docs/overlays/protobufs/cs_gameevents.yml`

## Is a field actually in a demo?

The schema dump carries NO `MNetwork*` metadata, so no doc on that site can tell you
whether a field reaches a demo. Three tests, weakest to strongest:

1. **Client-twin test.** A networked field must exist in the `client.dll` mirror class.
   No twin => server-only => never in a demo. Necessary, not sufficient. The size split
   on `CSMatchStats_t` (server 192 bytes vs client 128) is how you catch server-only tails.
2. **Overlay notes** (`LocalPlayerExclusive`, `LocalWeaponExclusive`) — hand-written hint.
3. **Ground truth: the demo.** `CSVCMsg_FlattenedSerializer` carries `var_encoder_sym`,
   `bit_count`, `low_value`, `high_value` per field. `tools/EntityDecodeProbe --schema`
   in DemoViewer.NET dumps exactly this. Run it before designing around any field.
