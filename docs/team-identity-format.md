# The team files: `teams.json` and `team-index.json` (schema v1)

Team Identity keeps two files. What the user authored is truth and lives beside `settings.json`;
what clustering derived is a cache and lives beside `index.json`. This document is for anyone who
wants to read or write either. Committed samples live under
[`tests/fixtures/team-identity/`](../tests/fixtures/team-identity/) and are round-trip-pinned by
`TeamIdentityTests.V1Schema_MatchesTheCheckedInSamples`.

## Where the files live

| File | Desktop | Browser build |
|---|---|---|
| `teams.json` (user truth) | `<app config root>/teams.json` | Nowhere. Session-only, and the Teams tab says so. |
| `team-index.json` (derived) | `<app config root>/cache/team-index.json` | Nowhere. |

`teams.json` is never rebuilt and never overwritten when it cannot be read: a file that fails to
parse is refused, the Teams tab reports why, and the session runs in memory. `team-index.json` is a
cache: missing, corrupt or behind, it is rebuilt from the demo cache's sidecars with the ids in
`teams.json` preserved.

Both files are written whole and atomically (temp file plus replace).

## `teams.json`

```jsonc
{
  "schemaVersion": 1,
  "me": { "steamIds": ["76561198…"] },
  "teams": [
    {
      "id": "3f2a…",                 // GUID, stable for the life of the team
      "name": "FURIA",
      "nameSource": "ClanTag",       // User | ClanTag | Auto; a User name is never overwritten
      "isUs": false,                 // at most one team
      "hidden": false,
      "rosters": [
        { "id": "r1", "since": "2026-06-01",
          "coreLineup": ["7656…", "7656…", "7656…", "7656…", "7656…"],  // exactly five or null; written once
          "extendedCore": ["7656…", "…"],                              // up to seven; a rewritten snapshot
          "label": null,
          "userStarted": false }
      ],
      "mergedFrom": [],              // team ids folded into this one, each tombstoned below
      "notes": ""
    }
  ],
  "tombstones": ["9c1b…"],           // ids a rebuild never recreates or reuses
  "overrides": [
    { "demoSha256": "ab12…", "demoStableKey": null, "side": 3, "teamId": "3f2a…" },
    { "demoSha256": null, "demoStableKey": "3f9c…", "side": 2, "teamId": null }   // null = not a team
  ],
  "provenance": {                    // Demo Provenance Labels' user pins; absent = every demo automatic
    "overrides": [
      { "demoSha256": "ab12…", "demoStableKey": null, "label": "our scrim" }
    ]
  }
}
```

* **`rosters[].coreLineup`**: the fixed five, established the first time the same five is seen on
  two sides of the roster, and then fixed: a rebuild re-matches against the same five.
* **`rosters[].extendedCore`**: the up-to-seven most frequent members, rewritten as counts change so
  the file stands alone when the cache is wiped. The anchor only while `coreLineup` is null.
* **`rosters[].userStarted`**: a roster the user started at `since`; the team's sides from that
  date on match it rather than the earlier rosters.
* **`overrides`**: keyed by content hash when the demo has one, else by the cache's stable path key,
  and upgraded to the hash the moment it appears.
* **`provenance.overrides`**: the same key rule, one entry per demo the user labelled on the Library
  card. `label` is one of `official`, `scrim`, `our scrim`, `matchmaking`; removing the entry hands
  the demo back to the heuristic. The section is additive: a file written before it reads empty.

### The provenance label

The label a demo carries is `IDemoProvenanceSource.LabelFor(sha256)` (path-keyed: `Resolve`). A pin
in `provenance.overrides` wins; otherwise the default is decided, in this order, from the cache row's
clan tags and `sourceKind` and from `team-index.json`'s `ourSideSource` and `opponentTeamId`:

| Condition | Default |
|---|---|
| clan tags on both sides | `official` |
| `ourSideSource` is `Team` or `Override` and `opponentTeamId` is set | `our scrim` |
| our side resolved (any source) and `sourceKind` is `GotvMatchmaking` | `matchmaking` |
| our side resolved (any source), tagless, not matchmaking | `scrim` |
| otherwise | unlabeled (`null`) |

`sourceKind` is the engine classifier's verdict on the file header, stored by name on the cache row
at tier 2. A row written before the field existed is classified from its cached server name alone,
which is the classifier's own fallback, so an old library needs no re-index to be labelled.

## `team-index.json`

```jsonc
{
  "schemaVersion": 1,
  "builtAtTicks": 6392…,
  "demos": {
    "<StableKey(path)>": {
      "path": "C:\\…\\match730_….dem",
      "sha256": null,
      "orderTicks": 6392…,           // DemoCacheRecord.ModifiedTicks until a real match date exists
      "sides": {
        "2": { "key": ["7656…"], "names": ["…"], "clan": null,
               "teamId": "3f2a…", "rosterId": "r1", "overlap": 4, "tier": 1, "standIn": true, "override": false },
        "3": { "key": ["7656…"], "names": ["…"], "clan": null,
               "teamId": null, "rosterId": null, "overlap": 0, "tier": 0, "standIn": false, "override": false }
      },
      "ourSide": 2,                  // 2 | 3 | null
      "ourSideSource": "Team",       // None | Override | Team | Me
      "opponentTeamId": null
    }
  },
  "members": {                       // team id → roster id → SteamID64 → appearances
    "3f2a…": { "r1": { "7656…": { "count": 8, "lastName": "yuurih", "firstTicks": 0, "lastTicks": 0, "standInCount": 1 } } }
  },
  "unaffiliated": [ { "demo": "<StableKey>", "side": 3 } ]
}
```

* **`sides[].key`**: the non-bot, non-coach SteamID64s on that end-of-demo side, sorted. Kept here so
  a rebuild after a merge or a split reads no sidecar.
* **`sides[].tier`**: 1 matched a fixed five, 2 matched an extended core, 0 unaffiliated or placed by
  an override. `standIn` is stored at both tiers and surfaced at tier 1 only.
* **No `clock` block.** Nothing in this file is a tick, so there is no tick anchor to declare; a
  consumer that joins a team to ticks (Watched Situations, the Round Index) carries its own.

The sides are end-of-demo sides. The side a team played in round N is
`TeamIdentityService.SideAtRound`, a join against the Round Facts slots of that round.
