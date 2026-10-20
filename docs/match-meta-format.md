# The `MatchMeta` record field (schema v1)

A demo file carries no wall-clock time: not in its header, its server info, its cvars or its events.
DemoViewer dates a match from what sits around the file instead, and keeps the answer on the demo's
cache record as `MatchMeta`. This document is for anyone who reads or writes that field. A committed
sample lives at [`tests/fixtures/match-meta/match-meta.sample.json`](../tests/fixtures/match-meta/match-meta.sample.json),
built from the committed [`demos/match730_003826256877184877003_0981591541_410.dem.info`](../demos/match730_003826256877184877003_0981591541_410.dem.info),
and both are pinned by `MatchMetaTests.V1Schema_MatchesTheCheckedInSamples` and
`MatchInfoReaderTests.DecodesTheCommittedInfoFile`. Regenerate the sample with `MATCH_META_GOLDEN_UPDATE=1`.

## Where it lives

| Condition | Location |
|---|---|
| Desktop | `<app config root>/cache/demos/<key>.json.gz`, the demo's record, under `MatchMeta`. Three values are mirrored on the demo's row in `cache/index.json` (below). |
| Browser build | The in-memory cache; nothing persists. |

Two passes write it. The `matchmeta` record pass reads no demo: only the file name, the `.dem.info`
beside the demo and the demo's write time. It stamps `matchmeta` in the record's `PackStamps` at schema
`1` under fingerprint `mi1`, the reader's version. The `matchrank` pass rides the demo's visit, the one
forward read the library already makes, and writes the game mode, the source and the rank rows (below).
It stamps `matchrank` at schema `1` under fingerprint `mr1`. A stamp at another schema or fingerprint
runs its pass again on every demo, and each pass keeps what the other wrote. The field is additive: a
record neither pass has reached has no `MatchMeta`, and no tier schema moved when it was added.

## The field

```jsonc
"MatchMeta": {
  "Schema": 1,
  "MatchTimeUtcTicks": 639173350460000000,   // 2026-06-17T23:17:26Z
  "MatchTimeSource": "ValveMatchInfo",
  "ReservationId": "3826256877184877003",
  "OutcomeId": 981591541,
  "Token": 410,
  "GameMode": "premier",
  "MatchSource": "GotvMatchmaking",
  "Players": [
    { "SteamId64": "76561198000000001", "RankType": 11, "RankOld": 14213, "RankNew": 14402,
      "RankChange": 189, "Wins": 312, "RankSource": "ServerRankUpdate" }
  ]
}
```

* **`MatchTimeUtcTicks`**: when the match was played, in .NET ticks (100 ns since 0001-01-01) UTC. Null
  only when the source is `Unknown`.
* **`MatchTimeSource`**: where the time came from, written by name. The first source present wins, in
  this order:

  | Source | Meaning | Exact |
  |---|---|---|
  | `UserOverride` | Reserved for a user-set time. Nothing writes it yet. | yes |
  | `ValveMatchInfo` | `matchtime` from the Steam replays folder's `<demo>.dem.info`. | yes |
  | `ExternalMetadata` | Reserved for an online source. Nothing writes it yet. | yes |
  | `FileWriteTime` | The demo file's last write time: when it was downloaded or copied, not when it was played. | no, show it as approximate |
  | `Unknown` | Nothing to go on. | |

  A source captured earlier is kept when a later run finds only a lower one. The `.dem.info` lives only
  beside the original download, so a demo moved out of the replays folder keeps the time it was first
  seen with. On a tie the later run wins.
* **`ReservationId`**: Valve's matchmaking reservation id as a decimal string (it does not fit a double).
  From the `.dem.info` when there is one, else the file name. Absent for a demo that is not a Valve match.
* **`OutcomeId`**, **`Token`**: the second and third numbers of a Valve file name
  `match730_<reservationid>_<outcomeid>_<token>.dem`. Absent when the name is not one. No Valve, FACEIT
  or HLTV file name carries a date, so the name never dates a match.

* **`GameMode`**: the server's `svc_ServerInfo.game_session_config.gamemode` as written (`premier`,
  `competitive`, ...). A demo whose server info names none takes the players' commonest rank system
  (11 `premier`, 12 `competitive`, 7 `wingman`). Absent until the demo's visit has read it, or when it
  has neither.
* **`MatchSource`**: the engine's `DemoSourceKind` by name (`GotvMatchmaking`, `HltvPro`, `Pov`, `Faceit`,
  `Custom`, `Unknown`). The engine never classifies a FACEIT server, so an `Unknown` or `Custom` demo
  whose server name contains `faceit` is written `Faceit`.
* **`Players`**: one rank row per human, by SteamID64, ordered by it. Absent when no player has a rank
  system (FACEIT, HLTV and community demos).

Strings are used for SteamIDs and reservation ids everywhere in this cache.

## Rank rows

Two sources feed a row. A Valve matchmaking server sends `CCSUsrMsg_ServerRankUpdate` (net message 352)
once at the end of the match, with every human's `account_id`; the account joins its controller as
`account_id + 76561197960265728`. Each `CCSPlayerController` carries six `m_iCompetitive*` fields, all
pre-match. The update wins where there is one.

| Field | From the update | From the controller alone |
|---|---|---|
| `RankType` | `rank_type_id` | `m_iCompetitiveRankType` (11 premier, 12 competitive) |
| `RankOld` | `rank_old` | `m_iCompetitiveRanking` |
| `RankNew` | `rank_new` | premier: `m_iCompetitiveRankingPredicted_Win`, `_Loss` or `_Tie` for the result the player got; competitive: absent |
| `RankChange` | `RankNew - RankOld` | the same, when both are present |
| `Wins` | `num_wins`, after the match | premier: `m_iCompetitiveWins` plus one for a win; competitive: absent |
| `RankSource` | `ServerRankUpdate` | `Predicted` (premier with a known result), else `Controller` |

* **Unranked is absent, never 0.** Rank 0 marks an account still in placement, in both sources, and is
  written as no `RankOld` or `RankNew`. A row with neither is unranked. Competitive accounts can be
  ranked with fewer than ten wins, so wins are never part of the test.
* **The result** for a prediction is the final score: a finished result wins, a tie of 12 or more is a
  tie, and anything else (a surrender, a demo cut short) is unknown, which leaves `RankNew` absent.
  Competitive predictions are all 0, so a competitive row without the update has no rank after.
* A controller with no rank system and rank 0 is no row.

## The `.dem.info` file

A `CDataGCCStrike15_v2_MatchInfo` protobuf message. No generated type for it ships, so the reader walks
the wire format and reads four fields by number; every other field is skipped by its wire type.

| Path | Field | Read as |
|---|---|---|
| `matchtime` | 2 | unix seconds, UTC |
| `roundstatsall[]` | 5 | one entry per round, in order |
| `roundstatsall[].reservationid` | 1 | the last entry that has one; Valve writes it on the final round only |
| `roundstatsall[].match_result` | 11 | the last entry's |
| `roundstatsall[].team_scores` | 12 | the last entry's, packed or not |

`team_scores` and `match_result` are decoded but not stored in `MatchMeta`. A truncated or malformed file
counts as no file.

## The index row

`DemoCacheIndexEntry` mirrors `MatchTimeUtcTicks`, `MatchTimeSource`, `ReservationId`, `GameMode` and
`MatchSource`, each omitted when null, so a match history sorts and labels without opening a record.
The rank rows stay on the record. The history order (`NewestMatchFirst`) is
newest match first, by `MatchTimeUtcTicks` or the row's write time while it has none, then the higher
reservation id, then the path.
