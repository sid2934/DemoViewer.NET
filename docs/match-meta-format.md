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

The `matchmeta` record pass writes it. It reads no demo: only the file name, the `.dem.info` beside the
demo and the demo's write time. The pass stamps `matchmeta` in the record's `PackStamps` at schema `1`
under fingerprint `mi1`, the reader's version; a stamp at another schema or fingerprint runs the pass
again on every demo. The field is additive: a record the pass has not reached has no `MatchMeta`, and no
tier schema moved when it was added.

## The field

```jsonc
"MatchMeta": {
  "Schema": 1,
  "MatchTimeUtcTicks": 639173350460000000,   // 2026-06-17T23:17:26Z
  "MatchTimeSource": "ValveMatchInfo",
  "ReservationId": "3826256877184877003",
  "OutcomeId": 981591541,
  "Token": 410
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

Strings are used for SteamIDs and reservation ids everywhere in this cache.

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

`DemoCacheIndexEntry` mirrors `MatchTimeUtcTicks`, `MatchTimeSource` and `ReservationId`, each omitted
when null, so a match history sorts without opening a record. The history order (`NewestMatchFirst`) is
newest match first, by `MatchTimeUtcTicks` or the row's write time while it has none, then the higher
reservation id, then the path.
