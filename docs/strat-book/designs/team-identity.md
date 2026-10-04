# Team Identity: design

**Work item:** Team Identity (plan.md §3, Phase 0) · **Kind:** design, review required · **Status:** approved; review folded in
**Tree:** `main` at `d90ec9f` (0.8.1), CS2DemoKit 0.12.0 · **Written:** 2026-09-23 · **Revised:** 2026-09-24
**Unblocks:** Search Filters And Live Count, Watched Situations, The Matrix, Strat Record Panel, Demo Provenance Labels, Map Pool Record, Period Diff, and every Dossier section.

Nothing here is implemented. Every code reference describes the tree at `d90ec9f`. Measurements were taken over the user's own demo cache (277 Valve matchmaking demos from the Steam `replays` folder, builds 10231 through 10896, plus 8 HLTV pro demos), never over the tour sample.

---

> **Status: APPROVED 2026-09-23**, as reviewed. §11 (Valve-rules alignment, TI-V1 to
> TI-V5) was accepted as recommended. **Folded in 2026-09-24:** §3.1, §3.2, §3.3, §3.6 and §3.7 now
> carry the Roster / Team vocabulary, the two-tier matching rule, the `standIn` stamp and the coach
> exclusion, and the §3.3 table was re-measured with the two-tier rule (the previous row is kept for
> comparison). §3 is the design to build from; §11 stays as the record of why. Build order per plan
> D1: after Result Cards And Walking.

## 1. Problem and scope

Finding F4: "the opponent" and "our team" do not exist as data. `DemoCacheRecord` carries a roster (name, SteamID64, end-of-match side) and, on pro demos only, a clan tag per side. Nothing groups demos by who played in them. Four features assume a stable team identity across demos: Watched Situations ("their Mirage A setup: 3 new"), the multi-demo Matrix ("Falcons, last 6 demos"), the Strat Record Panel (our scrims vs officials) and the whole Opponent Dossier.

This design defines:

- a **Team** as data: an id, a name, a roster history, and the set of demo sides that belong to it;
- **clustering** of demo sides into teams by SteamID overlap, with a threshold decided from real demos;
- a single **"this is us"** designation, plus a **"me"** account list that makes "our side" resolvable on demos where no stable roster exists (solo and duo matchmaking, which is most of a matchmaking library);
- how the **opponent** of a demo is derived;
- what happens on a **roster change**, and how a **manual merge or split** is recorded so a rebuild does not undo it;
- **storage**, the **query API** the four consumers call, and the **Library and Match Overview touchpoints**;
- the interaction with **Demo Provenance Labels**.

Out of scope: per-round side (which half a team played CT) is Round Facts; content hashing of every demo is Content Identity; the provenance label vocabulary is Demo Provenance Labels. This design names the seams to each.

---

## 2. What exists today

### 2.1 The roster in the cache

`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs`:

| Thing | Where | What it holds |
|---|---|---|
| `CachedPlayerInfo` | `:68-81` | `Slot`, raw `Name`, `SteamId64` (string), `Team` (2 = T, 3 = CT, else spectator), `IsBot`. |
| `DemoCacheRecord.Players` | `:233` | Every named userinfo slot except the GOTV proxy. Bots and spectators are kept with their team. |
| `DemoCacheRecord.Roster` | `:284` | `Players.Where(Team is 2 or 3)`. |
| `DemoCacheRecord.HasTeamSplit` | `:292` | False for a migrated legacy row (names, no team). |
| `CtClan` / `TClan` | `:246-247` | `CCSTeam.m_szClanTeamname` at the last frame. Populated on pro demos, empty on matchmaking (measured: 0 of 277 replays carry one; 5 of 8 pro demos do). |
| `Sha256` | `:216` | Null until something computes it. Today only files sharing a byte size with another file are hashed (`DemoLibraryModels.cs:375-382`). |
| `DemoCacheIndexEntry.PlayerNames` | `:392` | Names only. The always-loaded index carries no SteamIDs and no team split. |

The roster is projected in `DemoLibraryService.ProjectTier2` (`src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs:1362-1397`) from `ParsedDemo.Players`. The engine documents `PlayerInfo.Team` as "the last `player_team` game event for each slot" (CS2DemoKit.Parser.xml, `ParsedDemo.Players`). So **`Team` is the side a player finished on**, not the side they started on. Both sides swap at halftime; the split is still consistent within one demo, which is all clustering needs. Clan tags are read the same way, at the last frame, in `ExtractFinalScore` (`:1403-1451`): `CtClan` names the roster whose `Team == 3` at the end.

Three data-quality facts from the user's 277 replays, measured with the scratch scripts in §10:

| Case | Count | Consequence |
|---|---|---|
| 5 v 5, every player on team 2 or 3 | 263 | Clusterable. |
| 2 v 2 (wingman) | 7 | Clusterable with a size-relative threshold (§3.3). |
| 5 v 4 (a player never took a side, or left) | 1 | Clusterable; the 4-side matches on 3. |
| All ten players `Team == 0` | 6 (2.2%) | Not clusterable: no `player_team` event reached the parse. These render today as "team split needs a re-index" (`HasTeamSplit` false). Team Identity leaves them unassigned. |
| Bot roster entries | 0 | Bots are excluded from side keys anyway. |
| Empty or zero SteamIDs on a roster | 0 | Excluded anyway. |

### 2.2 The store and its conventions

`DemoCacheStore` (`src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs`): `index.json` plus one lazily-read sidecar per demo under `cache/demos/`, keyed by `StableKey(path)` (`:500-511`), atomic writes (`:559-573`), in-memory on the browser host (`:29-31`, `:48-59`), a `Changed(string? path)` event raised per mutation or once per batch (`:157-170`). The class doc says the cache "is rebuildable and is never a source of truth" (`:24-26`). `LoadRecords` (`:283-318`) is the one bulk reader; measured at ~32 ms warm and ~297 ms cold for 348 demos, and documented as not for the UI thread. `LoadOrCreate` discards every tier on identity drift (`:263-281`).

Sibling user-truth files live one level up, under the config root, resolved by `AppPaths.Resolve`: `settings.json` (`AppPaths.cs:74`), `SessionState.json` for bookmarks (`:81`), `GraphBreakpoints.v2.json` (`:87`). `BookmarkStore` (`Services/BookmarkStore.cs:21-77`) is the smallest example: short-circuits on `OperatingSystem.IsBrowser()`, best-effort JSON.

### 2.3 Where a parse becomes a cache row

`DemoLibraryService` is an `IDemoEvaluator` (`DemoLibraryService.cs:191-215`); `Evaluate` runs tier 2 with the parse held and writes the record through `DemoCacheStore.Update` (`WriteTier2ToDemoCache`, `:1302-1360`). Every other evaluator is fed the same parse by `DemoEvaluationCoordinator`. Team Identity does not need the parse: everything it consumes is already in the record by the time `Changed(path)` fires.

### 2.4 The Library and Match Overview surfaces

- Library filters: free text, a multi-select map filter, and a single-select player filter over names (`ViewModels/Library/LibraryTabViewModel.cs:575-633`). The card subtitle is the clan matchup on pro demos and the server name otherwise (`Modules/Library/DemoLibraryModels.cs:167`).
- Match Overview labels each roster card with the uppercased clan tag when present (`ViewModels/MatchOverview/MatchOverviewTabViewModel.cs:828-836`) and "CT" / "T" otherwise.
- New surfaces are first-party `IWorkspaceModule`s: `HighlightsModule` (`Modules/Highlights/HighlightsModule.cs:31-61`) is the shape, registered in `App.BuildRegistry` (`App.axaml.cs:838-871`) with a delegate-injected VM.

### 2.5 The engine

CS2DemoKit 0.12.0 exposes nothing team-shaped beyond `PlayerInfo.Team` and the `CCSTeam` entity. `DemoSourceClassifier` (Parser) yields `DemoSourceKind` (GOTV / HLTV / POV / Unknown) on the profile; the app already reads it (`ViewModels/Stats/AimCapabilityReport.cs:260`). No engine change is needed for this design (§5).

---

## 3. Proposed design

### 3.1 Vocabulary

The vocabulary follows Valve's competition rules (§11.1): a *roster* is five specific people, and a *team* is the continuity a user asserts across rosters.

| Term | Meaning |
|---|---|
| **Side key** | The set of non-bot, non-coach SteamID64s on one side (`Team` 2 or 3) of one demo, as of the end of the demo. Two per clusterable demo. A coach is a player whose `m_iCoachingTeam != 0` (TI-V4); `CachedPlayerInfo` gains `IsCoach` for it. |
| **Roster** | The unit of identity: a persisted id, a `since` date, and an anchor that sides are matched against. A roster created by clustering belongs to one team. Period Diff reads rosters ("roster to roster"). This is what the first draft called an *epoch*. |
| **Core lineup** | `coreLineup`: the roster's fixed five, Valve's "Core Lineup" (§11.1). Established the first time the same five side-key members are seen on two sides of the roster, snapshotted into `teams.json` at that moment, and never rewritten for that roster. Null until then. |
| **Extended core** | `extendedCore`: the up-to-7 most frequently seen members of a roster, ordered by appearance count then last seen. Derived and rolling. It is the anchor only while `coreLineup` is null (§3.3 explains the two tiers and why 7). |
| **Team** | User-owned continuity: a GUID, a name, one or more rosters, and the demo sides assigned through those rosters. Clustering creates a team with one roster; Merge gives a team several. |
| **Assignment** | Per demo: which roster, if any, each side key matched, at which tier and overlap, whether a stand-in was present, which side is ours, and which team is the opponent. Derived, rebuildable. |
| **Stand-in** | `standIn: true` on a side assignment whose overlap with the anchor is 3 or 4 and whose key carries a member outside the anchor (TI-V3). |
| **Us** | Exactly one team may carry `IsUs`. |
| **Me** | A list of the user's own SteamID64s. Makes "our side" resolvable when no roster matches. |
| **Unaffiliated side** | A side key that matched no roster. Most opponent sides in a matchmaking library are unaffiliated by construction (§3.3). |

### 3.2 Data model

Two files. The split follows the store's own rule: what the user authored is truth; what clustering derived is a cache.

**`<config>/teams.json`** (user truth, small, never rebuilt, resolved with `AppPaths.Resolve("teams.json")` beside `settings.json`):

```jsonc
{
  "schemaVersion": 1,
  "me": { "steamIds": ["76561198029679050"] },
  "teams": [
    {
      "id": "3f2a…",                       // GUID, stable for the life of the team
      "name": "FURIA",
      "nameSource": "ClanTag",             // User | ClanTag | Auto
      "isUs": false,
      "hidden": false,                     // user chose not to see this team in lists
      "rosters": [
        { "id": "r1", "since": "2026-06-01",
          "coreLineup": ["7656…", "7656…", "7656…", "7656…", "7656…"],   // exactly five, or null until established; never rewritten
          "extendedCore": ["7656…", "7656…", "7656…", "7656…", "7656…", "7656…", "7656…"], // up to 7, rewritten as counts change
          "label": null }                  // optional user label: "post-KSCERATO roster"
      ],
      "mergedFrom": [],                    // team ids folded into this one, tombstoned below
      "notes": ""
    }
  ],
  "tombstones": ["9c1b…"],                 // ids of teams merged away; a rebuild never recreates them
  "overrides": [                           // manual per-demo assignment, keyed by content hash
    { "demoSha256": "ab12…", "side": 3, "teamId": "3f2a…" },
    { "demoSha256": "ab12…", "side": 2, "teamId": null }   // null = "this side is not a team"
  ]
}
```

`rosters[].coreLineup` is written once, when the five is established (§3.3), and is then fixed for the life of the roster: it is the identity anchor and a rebuild must re-match against the same five. `rosters[].extendedCore` is a **snapshot** rewritten whenever the derived top-7 changes, so the user file stands alone: with the cache wiped, the same rosters re-match the same demos and keep their names and ids. Everything else about members is derived. Both lists hold non-bot, non-coach SteamID64s.

**`<config>/cache/team-index.json`** (derived, rebuildable, beside `index.json`, atomic write through the same `WriteAtomic` idiom):

```jsonc
{
  "schemaVersion": 1,
  "builtAtTicks": 6392…,
  "demos": {
    "<StableKey(path)>": {
      "path": "C:\\…\\match730_….dem",
      "sha256": null,                       // filled when Content Identity lands
      "orderTicks": 6392…,                  // DemoCacheRecord.ModifiedTicks until a real match date exists
      "sides": {
        "2": { "key": ["7656…", …], "teamId": "3f2a…", "rosterId": "r1", "overlap": 4, "tier": 1, "standIn": true },
        "3": { "key": ["7656…", …], "teamId": null,   "rosterId": null, "overlap": 0, "tier": 0, "standIn": false }
      },
      "ourSide": 2,                          // 2 | 3 | null
      "ourSideSource": "Team",               // Team | Me | Override | null
      "opponentTeamId": null
    }
  },
  "members": {                               // per team, per roster: who appeared how often
    "3f2a…": { "r1": { "7656…": { "count": 8, "lastName": "yuurih", "firstTicks": …, "lastTicks": … }, … } }
  },
  "unaffiliated": [ { "demo": "<key>", "side": 3 }, … ]   // one-side candidates for a future roster
}
```

`tier` on a side assignment is 1 when the side matched a fixed `coreLineup`, 2 when it matched an `extendedCore`, 0 when unaffiliated (§3.3). `standIn` is stored at both tiers and surfaced at tier 1 only.

`IsCoach` on `CachedPlayerInfo` is an additive field (no schema bump, per the cache's own convention): `true` when `CCSPlayerController.m_iCoachingTeam != 0` at the last frame. A record written before the field exists reads `false`, which is what the 277 matchmaking replays and the 8 HLTV demos here measure anyway (§3.3). Verifying it on an HLTV demo with a registered coach is TI-V4's condition and belongs to Inputs Per Demo Source.

Why not a tier on `DemoCacheRecord`: assignment is cross-demo by nature. A user rename must touch zero demos; a merge must re-assign every side of the merged team; "list demos of team X" must not open 300 sidecars. A per-demo tier would scatter all three. The index row is not the place either: it is written only by `ToIndexEntry()` from the record (`DemoCacheModels.cs:337-370`), so an assignment there would have to be on the record. `team-index.json` is joined in memory by `StableKey`, which is exactly how the cache already keys its sidecars.

Why the side keys live in `team-index.json` and not only in the sidecars: a rebuild after a merge or split needs every side key. Reading them from `team-index.json` avoids the `LoadRecords` cold pass (~300 ms per 350 demos) on every user action; the sidecar pass runs only when `team-index.json` is missing or its schema is behind.

Size: about 250 bytes per demo plus the members table. 300 demos: roughly 80 KB. 5,000 demos: roughly 1.3 MB. Read once at startup.

Join key and Content Identity: assignments are derived and keyed by `StableKey(path)`, like the sidecars. Only `overrides` are user truth that must survive a moved file, so they key by `demoSha256`. Until Content Identity hashes every demo, an override on a demo with a null hash is stored with `demoStableKey` instead and upgraded to `demoSha256` when the hash appears. That is the only dependency on Content Identity, and it is soft.

Clock header: this store carries no tick anchors. A `clock` block is required by F15 for positional stores; team-index.json holds none and says so in its schema doc. Consumers that join a team to ticks (Watched Situations, the Round Index) carry their own clock header.

### 3.3 Clustering: the matching rule and the threshold

**Anchor.** Every roster carries one anchor set that side keys are compared against, and the anchor decides the tier:

- **Tier 1, `coreLineup`:** the roster's fixed five, Valve's Core Lineup. It is established the first time the same five members are seen on two sides of the roster, snapshotted into `teams.json`, and never rewritten. A pro roster establishes it at its second sighting; a matchmaking trio with rotating fifths may never establish one.
- **Tier 2, `extendedCore`:** the top 7 members of the roster by appearance count (ties by last seen), derived and rolling. Used only while `coreLineup` is null. Seven is Valve's maximum registered persons (five, a coach, a substitute; §11.1), and it is also the cap at which the measurement below stops chaining. A one-side candidate's extended core is its own key.

**Rule.** A side key `S` matches roster `R` when `|S ∩ anchor(R)| >= k(S)`, where `k(S) = min(3, |S|)`. Three of five is Valve's continuity rule: §3.2.5(a) requires that at least three of the Invited Roster play in each match, and §3.10.1 forfeits any match in which a roster does not field three of them for its entirety (TI-V5). `min(3, |S|)` makes wingman (2 v 2) require both players and lets a 4-player side match on 3. Candidates compete by overlap; a tie goes first to a roster with an established five, then to the roster founded first. **A roster never takes both sides of one demo**: if both match the same roster, the side with the larger overlap keeps it and the other becomes unaffiliated (a user override can fix a genuine mirror match).

The tie rule is a correction. The first draft said "the most recent `lastTicks` wins" but the measurement behind its table was taken with founded-first; the two disagree on this library (table below). Founded-first is kept because it is stable: a new sighting on roster B does not move an older tie away from roster A on the next rebuild.

**Stand-in.** When a side matches at overlap 3 or 4 and its key carries a member outside the anchor, the assignment is stamped `standIn: true` (TI-V3). The stamp is stored at both tiers and surfaced (Dossier, Period Diff, Strat Record Panel) only at tier 1, because "with a stand-in" has no referent until a five exists. Measured: 0 of 13 matches on the pro demos, 82 of 90 on matchmaking, of which 17 at tier 1.

**Coaches.** Side keys exclude `IsCoach` (TI-V4). Measured: all 16 pro side keys in this library have exactly five members, so the exclusion changes nothing here; a six-member pro side key is the symptom that would show it is needed.

**Measured, user's 277 replays (554 side keys), incremental assignment in file-date order, founded-first tie unless noted.** "Rosters" counts candidates with 2 or more sides; "fixed fives" counts rosters whose `coreLineup` is established; "unaffiliated" counts one-side candidates.

| Rule | k | cap | Rosters | Fixed fives | Largest roster (sides / distinct members) | Unaffiliated | Tier-1 matches | Verdict |
|---|---|---|---|---|---|---|---|---|
| Rolling core only (the rule as first designed, row published 2026-09-23) | 3 | 7 | 24 | n/a | 27 / 42, core counts 27, 25, 17, 12, then 3s | 427 | n/a | Previous rule, kept for comparison. |
| Rolling core only, most-recent tie (the tie rule the first draft stated) | 3 | 7 | 21 | n/a | 20 / 35 | 430 | n/a | Shows the tie rule matters; not used. |
| **Two-tier, five established when the same five is seen twice** | **3** | **7** | **24** | **5** | **18 / 29**, counts 18, 18, 17, then 3s | **428** | **19 of 90** | **Chosen.** A trio plus its established five; see below for where the other 9 sides went. |
| Two-tier, five established when five members each recur | 3 | 7 | 22 | 8 | 19 / 31 | 432 | 42 of 88 | Fixes a five whose fifth has 3 of 19 appearances. Rejected: a five is people who played together. |
| Two-tier | 3 | 5 | 26 | 5 | 18 / 29 | 429 | 19 of 87 | Splits tier-2 rosters when the fourth rotates. |
| Two-tier | 3 | 10 | 22 | 5 | 20 / 31 | 428 | 19 of 92 | Tier 2 starts to chain again. |
| Two-tier | 4 | 7 | 22 | 6 | 5 / 8 | 485 | 3 of 35 | One stand-in breaks the roster, against Valve §3.2.5(a). |
| Two-tier | 2 | 7 | 26 | 4 | 50 / 101 | 365 | 69 of 151 | Chains: the largest roster is not a roster. |

Where the previous 27-side roster went under the chosen rule: 18 sides stay together as the roster whose five (the user, two regular partners and two others) was established at its 11th side; 6 sides move to a 7-side tier-2 roster of the user, one of the two partners and a third player; 3 sides move to a 10-side roster with its own fixed five. Every moved side shared only two of the established five, which under Valve's rule is a different roster. The rolling core kept them together only because the third player had crept into its top 7. Both results are defensible; the two-tier one is the one Valve's rules describe, and Merge (§3.6) is how a user says "these rosters are one team" when that is what they mean.

Same rule over the 8 HLTV pro demos (16 side keys): 3 rosters, every five established at its second sighting, 13 of 13 matches at overlap 5 of 5 (10 at tier 1; the 3 tier-2 matches are each roster's second sighting, the one that establishes the five), zero stand-ins, zero unaffiliated. Identical to the previous rule: FURIA (8 sides, tags "FURIA" x4 and "Furia" x1, three sides with no tag), Team Vitality (4 sides, tagged), NaVi (4 sides, one tagged). Clan tags therefore seed names correctly only after a case-insensitive fold, and they are absent on some pro sides, so the roster is the identity and the tag is only a label.

**Pairwise overlap distributions, same corpus** (these justify the "opponent is unaffiliated" default):

| Pair type | Overlap 0 | 1 | 2 | 3 | 4 | 5 |
|---|---|---|---|---|---|---|
| Our side vs our side | 0 | 32,770 | 2,087 | 349 | 31 | 8 |
| Opponent side vs opponent side | 41,308 | 12 | 7 | 1 | 0 | 0 |
| Our side vs opponent side | 76,529 | 64 | 12 | 3 | 0 | 0 |

In matchmaking the opponent never recurs: 1,304 of 1,331 opponent accounts appear once. An Opponent Dossier over matchmaking demos has no subject, and the design says so in the UI rather than clustering noise into a team (§3.9).

**Roster creation.** A side that matches nothing becomes a one-side candidate, recorded as unaffiliated; its anchor is its own key. Candidates compete with rosters under the same rule, so when a later side shares `k` or more members with it, both become the first two sides of a new auto roster in a new auto team, with `extendedCore` ranked by count and `coreLineup` established at once if the two keys are the same five. Single sightings never create a roster, so a library of solo queues creates none, and the Teams panel stays empty rather than listing 400 one-demo entries.

**Ordering.** Incremental assignment is order-dependent by nature (the first side seen founds the roster, and the first repeated five fixes its lineup). The order is `orderTicks` ascending, then path. `orderTicks` is `DemoCacheRecord.ModifiedTicks` today, which for Valve replays is download time. When a real match date exists (§6), it replaces this without a schema bump.

### 3.4 Names

| Source | When | Rule |
|---|---|---|
| `User` | The user renamed the team. | Never overwritten. |
| `ClanTag` | Any assigned side carries a `CtClan` / `TClan`. | Most frequent tag after case-insensitive fold; the most recent spelling is displayed. Re-evaluated on every assignment until the user renames. |
| `Auto` | No tag anywhere. | "Team of {name1}, {name2}" from the two most frequent anchor members' last-seen names (the `coreLineup` when established, the `extendedCore` otherwise), sanitised at the render boundary as every player name is (`DisplayText.Sanitize`). Re-evaluated as counts change. |

Names are display; ids are GUIDs. Two teams may share a name (two FURIA rosters the user chose to keep as separate teams); the panel disambiguates with the roster's `since` date.

### 3.5 "Us" and "me"

- `IsUs` is set on at most one team. Setting it on another clears the first.
- `me.steamIds` is a list. Seeding: on first run, or when empty and the library has 20 or more clusterable demos, suggest the account with the highest demo share when that share exceeds 50%. Measured: 266 of 277 replays (96%) contain the user's account. The user confirms in the Teams panel; nothing is written without confirmation.
- Resolution per demo, in order: an `override` for either side; a roster of the `IsUs` team matching a side; a `me` account on a side. The first rule that fires sets `ourSide` and `ourSideSource`. If none fires, `ourSide` is null and every "our / their" consumer treats the demo as neutral.
- `opponentTeamId` is the team assigned to the side that is not ours, or null when that side is unaffiliated. When `ourSide` is null there is no opponent, by definition.

This is what makes a matchmaking library work: with `me` set, every demo has an "our side" even though no stable roster exists, and "their setup" in Watched Situations means the other five, whoever they were. The Dossier, which needs recurrence, stays empty for those demos, correctly.

### 3.6 Roster changes, merges and splits

| Event | What clustering does | What the user can do |
|---|---|---|
| One or two members change | The side still shares 3 or more with the anchor, so it joins the same roster and is stamped `standIn` (§3.3). At tier 2 the newcomers enter the members table and rise into the `extendedCore` as their count grows, while departed members fall out of the top 7. At tier 1 the five never changes: a permanent replacement keeps matching at 4 with `standIn` on every side until the user starts a new roster. This is Valve's substitute (§11.1): allowed, limited, and not a new roster. | Nothing needed. Optionally "start a new roster here": a new roster in the same team, `since` the chosen demo, which establishes its own five from the sides after it. Period Diff then reads the boundary. |
| Three or more change at once | Overlap falls to 2 or less, the side matches nothing and becomes unaffiliated; a second sighting founds a new auto roster in a new auto team. Two teams now exist. This is Valve's new-roster boundary (§3.2.5(a)). | **Merge** B into A: A gains B's rosters (each keeps its own anchor), B's sides re-assign to A through those rosters, B's id is tombstoned. A's name stays. Recorded in `teams.json` as `mergedFrom` plus the tombstone. |
| A team is really two | One team, one roster, two fives that happen to share three players (a sister roster, an academy team). | **Split**: select a roster, or a set of demos, and "split into new team". A new team is created with those sides, a new roster whose five is established from them, and a name from the same rules. A rebuild keeps both because both ids exist. |
| Drift suggestion | Tier 1: the same non-lineup member appears on 5 consecutive sides of a roster. Tier 2: the top five over the roster's last 5 sides differ from the top five over its first 5 by 3 or more members. Either shows "roster changed around {date}: start a new roster?". Never applied automatically. | Accept, or dismiss (recorded so it is not re-asked for the same boundary). |
| A side is not a team | Two acquaintances of the user queued with three strangers twice. | **Not a team**: an override with `teamId: null` on that side, and the auto team is deleted if it has no other sides. |

**Rebuild is id-preserving.** A rebuild (schema bump, missing `team-index.json`, or the user's "recompute") first re-matches every side against the existing rosters' anchors from `teams.json` (`coreLineup` where established, else the `extendedCore` snapshot), then founds new auto rosters only for unaffiliated pairs, then deletes auto teams (`nameSource != User`, `isUs` false, no `mergedFrom`) that received no sides. An established `coreLineup` is never recomputed by a rebuild. Tombstoned ids are never reused or recreated. A user-named team with zero sides survives and is listed as "no demos".

### 3.7 Service and query API

One service in `DemoViewer.NET.dll`, `Services/Teams/TeamIdentityService.cs`, a DI singleton like `DemoCacheStore`, with the store split into `TeamsFile` (user truth) and `TeamIndexFile` (derived). Sketch:

```csharp
public sealed class TeamIdentityService
{
    // Reads (all cheap, all in-memory after startup)
    IReadOnlyList<Team> Teams { get; }                    // hidden excluded unless asked
    Team? Us { get; }
    IReadOnlyList<string> MyAccounts { get; }
    TeamAssignment? GetAssignment(string demoPath);        // null when unclusterable or unknown
    IReadOnlyList<DemoRef> DemosOf(Guid teamId);           // ordered by orderTicks descending
    IReadOnlyList<DemoRef> DemosAgainst(Guid opponentTeamId); // demos where ourSide != null and opponent == teamId
    IReadOnlyList<DemoRef> OurDemos();                     // every demo with ourSide != null
    Team? TeamOnSide(string demoPath, int endSide);        // 2 or 3, the end-of-demo side

    // Writes (each persists teams.json, re-derives what it must, raises Changed)
    void Rename(Guid teamId, string name);
    void SetUs(Guid? teamId);
    void SetMyAccounts(IReadOnlyList<string> steamIds);
    Guid Merge(Guid into, Guid from);
    Guid Split(Guid teamId, IReadOnlyList<DemoSideRef> sides, string? name);
    void StartRoster(Guid teamId, DateOnly since, string? label);
    void Override(string demoPath, int endSide, Guid? teamId);   // null = not a team
    void SetHidden(Guid teamId, bool hidden);
    Task RebuildAsync(CancellationToken ct);                // id-preserving; off the UI thread

    event Action? Changed;                                  // posted to the UI thread like DemoCacheStore.Changed
}

public sealed record TeamAssignment(
    string DemoPath, string? Sha256,
    SideAssignment T, SideAssignment Ct,                     // end-of-demo sides
    int? OurSide, OurSideSource Source, Guid? OpponentTeamId);

public sealed record SideAssignment(
    IReadOnlyList<string> Key, Guid? TeamId, string? RosterId,
    int Overlap, int Tier, bool StandIn);                    // Tier 1 = fixed five, 2 = extended core, 0 = unaffiliated
```

`TeamAssignment` is explicit that its sides are **end-of-demo** sides. A consumer that needs the side a team played in round N joins Round Facts; the API will grow `SideAtRound(demoPath, teamId, round)` there, not here.

What each consumer calls:

| Consumer | Calls |
|---|---|
| Search Filters And Live Count (opponent filter, "our demos" filter) | `Teams`, `DemosOf`, `OurDemos`, `GetAssignment` to resolve "their side" per demo. |
| Watched Situations ("their Mirage A setup: 3 new") | `DemosAgainst(opponent)` intersected with the Round Index's newly indexed set; `GetAssignment(...).OurSide` to know which five are "their". |
| The Matrix ("Falcons, last 6 demos") | `DemosOf(teamId)` take 6. |
| Strat Record Panel (our scrims vs officials) | `OurDemos()` joined with the provenance label; `StandIn` on our side for the caution. |
| Opponent Dossier and Period Diff | `DemosAgainst`, `Team.Rosters`, the members table per roster, `SideAssignment.StandIn` at tier 1 for "with a stand-in". |
| Map Pool Record | `DemosOf(teamId)` joined with `Map`, `CtScore`, `TScore`, and end sides. |
| Demo Provenance Labels | `GetAssignment(...).Source` for the default (§3.10). |

### 3.8 When assignment runs

- **Startup:** read `teams.json`, then `team-index.json`. If the index is missing or `schemaVersion` is behind, schedule `RebuildAsync` off the UI thread; it uses `DemoCacheStore.LoadRecords(e => e.ParseSchema > 0)` once (the documented cold cost) to collect side keys, then never again unless rebuilt.
- **Incrementally:** subscribe to `DemoCacheStore.Changed`. For a non-null path whose index row has `ParseSchema > 0` and whose `team-index.json` entry is missing or whose sides changed, load that one record (`TryLoadRecord` is a capacity-1 cache hit right after an upsert), compute both side keys, assign, persist, raise `Changed`. For a null path (a batch), diff the index against the team index and process the difference. No new `IDemoEvaluator` and no parse.
- **Removal:** `DemoCacheStore.Changed(path)` for a path no longer in the index drops the entry and decrements member counts.
- **User writes:** rename and hide touch nothing derived. `SetUs`, `SetMyAccounts` and `Override` re-derive `ourSide` and `opponentTeamId` for every demo (a pure in-memory pass over the team index). `Merge`, `Split`, `StartRoster` re-assign from the side keys already in `team-index.json`; no sidecar is read.

All writes are atomic through the same temp-and-replace idiom as `DemoCacheStore.WriteAtomic`. Both files are written whole; they are small.

### 3.9 UI touchpoints

**Library** (`LibraryTabViewModel`, `LibraryTabView.axaml`):
- A **Team** single-select filter beside the player filter: "All teams", "Us", then every visible team. Filtering by a team keeps demos where either side is assigned to it. This is the plan's Library touchpoint for D1.
- Card subtitle: today the clan matchup or the server name (`DemoLibraryModels.cs:167`). With an assignment, "{Us name} vs {opponent name}" where both resolve, "{team} vs (unaffiliated)" where one does, and the current text otherwise. `DemoEntry` gains `CtTeamName` / `TTeamName` observable strings that the library service fills from `TeamIdentityService.Changed`.
- A small "us" mark on the card when `ourSide` is resolved.

**Match Overview** (`MatchOverviewTabViewModel.cs:828-836`): the roster card label prefers, in order, the user team name, the clan tag, "CT" / "T". A "this is us" glyph on our card.

**Teams panel**: a first-party module `Modules/Teams/TeamsModule.cs`, id `net.demoviewer.teams`, one Main tab `teams.browser`, display "Teams", feature id `tab.teams`, delegate-injected VM like `HighlightsModule`. Contents: the team list (name, roster count, demo count, last seen, us mark), a roster row per team showing its fixed five or "no fixed five yet" with the extended core, a member table per roster (last name, appearances, first and last seen, stand-in count), the demo list for the selected team (opens in the workspace), and the actions of §3.7: rename, set as us, merge (select two), split, start roster, hide, not-a-team, recompute. The "me" account list and its suggestion live at the top of this panel, not in Settings, so team truth has one home. A footer states how many demos could not be clustered and, when every opponent is unaffiliated, says that the Dossier has no recurring opponent to describe.

**Browser host:** both files are session-only, like every store on that host (wasm-matrix.md "Demo library / cache / bookmarks: degraded, nothing in the UI says so"). The Teams panel shows the annotations-style line: "session only: this browser tab forgets teams when it reloads". Add a row to `docs/playback2d-v2/wasm-matrix.md` under Degraded at build time.

### 3.10 Interaction with Demo Provenance Labels

Demo Provenance Labels owns the vocabulary (`official | scrim | our scrim`) and the override. Team Identity supplies the default:

| `ourSideSource` | Clan tags on both sides | Default label |
|---|---|---|
| `Team` or `Override` | any | `our scrim` |
| `Me` | none | matchmaking: see the decision in §8 (the vocabulary has no value for it; `scrim` is wrong and `official` is wrong) |
| null | both | `official` |
| null | none | `scrim` |

The label item should read `GetAssignment(...).Source` and nothing else from this service, so the two items stay independently testable.

---

## 4. Alternatives considered

| Alternative | Why not |
|---|---|
| **Pairwise union-find at k = 3** (the plan's literal wording). | Measured: one 91-side cluster with 131 distinct members in the user's library, chained through the user's account. Correct on pro demos, wrong on any library where one account recurs. A bounded anchor (the fixed five, or the top-7 extended core) is the fix and costs nothing on pro demos. |
| **Rolling top-7 core as the only anchor** (this design's first draft). | Works, and is kept as tier 2, but it is not what "the same roster" means: a third partner who creeps into the top 7 makes a two-of-five side match at 3 (the 27-side roster in §3.3). Valve anchors identity to a fixed five; the two-tier rule does the same wherever a five exists. |
| **k = 4 of 5.** | Measured: a single stand-in breaks the team; the largest team drops to 5 sides. Stand-ins are routine in scrims. |
| **Name-based clustering (clan tags first, SteamIDs second).** | Tags are absent on 100% of matchmaking demos and on 3 of 16 pro sides here, and vary in case ("FURIA" / "Furia"). Tags seed names; they cannot be identity. |
| **A tier on `DemoCacheRecord`.** | Cross-demo data on a per-demo record: a merge would rewrite every sidecar of the merged team, and "demos of team X" would be a sidecar scan. See §3.2. |
| **Everything in one `teams.json`.** | Mixes user truth with a rebuildable derivation, which is the exact defect the unified cache design calls out for the old library cache (`DemoCacheModels.cs:50-54`). Two files, one rule. |
| **SQLite.** | Decision D2 is deferred to The Round Index design. This store is tens of kilobytes and read once; JSON is the current pattern and needs no package. If D2 lands on SQLite, `team-index.json` can move into it without changing the API. |
| **Automatic split on roster drift.** | An unattended split renames nothing and can halve a team's history the day a sixth player fills in twice. Suggest, never apply. |
| **"Us" as a team only, no "me" accounts.** | Leaves 427 of 554 user-library sides without an "our side", which is every solo and duo queue. The Watched Situations badge would never fire for a matchmaking user. |
| **Identify "me" from the demo file name or the Steam install.** | Valve replay file names carry a match id, not an account. Reading the logged-in Steam account from `loginusers.vdf` is possible but couples a data model to a Steam install; the share heuristic is one line and confirmable. |

---

## 5. External and engine changes required

**None.** Everything consumed exists in the cache record today. Two optional items for the record, neither assumed:

- CS2DemoKit could expose the `CCSTeam` clan name and `m_iTeamNum` on `ParsedDemo` so `ExtractFinalScore`'s entity replay is not the only source; this would remove app code, not add capability, and is not proposed here.
- Valve replays ship a `.info` sibling (`match730_….dem.info`, a `CDataGCCStrike15_v2_MatchInfo` protobuf) with `matchtime` and account ids. It would give a real match date for `orderTicks` and Period Diff. Reading it is a small library-side feature and belongs to Round Facts or a later date item, not here.

---

## 6. Risks and unknowns

| Risk | Likelihood | Mitigation |
|---|---|---|
| `Team == 0` for every player on some demos (6 of 277 here). | Certain, small. | Unassigned, surfaced as the existing "team split needs a re-index" state. Track the rate in the Teams panel footer. If the rate is higher on other sources, it becomes a parser question for CS2DemoKit. |
| Order dependence of incremental clustering: a library indexed in a different order founds different auto teams. | Medium. | Ids are preserved across rebuilds and user names never move, so the only difference is which auto team a borderline side founded. A "recompute" action exists. Document it. |
| The extended-core cap of 7 was tuned on one library. | Medium. | It applies only at tier 2, is a constant in one place with the measurement beside it, and matches Valve's registered-roster maximum. The corpus fixture in §7 pins the numbers; re-run on a scrim library when one exists. |
| The fixed five is established on the first repeated five, which on a matchmaking roster may be a weak one (established at side 11 of 18 here). | Low impact. | Only the anchor moves; the roster's sides and id do not. "Start a new roster here" resets the five. On pro and scrim libraries the first repeated five is the roster. |
| A registered coach reaches the side key on some demo source. | Unmeasured; 0 of 16 pro side keys here have six members. | `IsCoach` exclusion (TI-V4); Inputs Per Demo Source verifies on an HLTV demo with a coach. |
| `ModifiedTicks` is download time, not match time, on Valve replays; a bulk re-download reorders everything. | Low impact. | Only affects which side founds a team, never membership. Replace with the `.info` date when available (§5). |
| The user's account absent on a demo from a shared folder (a teammate's replays). | Real for teams. | `me.steamIds` is a list; add every teammate's account and "our side" resolves the same way. |
| Two teams with three shared players (sister rosters). | Low. | Split, and the split is recorded; a rebuild honours both ids. |
| A user-named team whose core stops matching anything (the whole roster left). | Low. | Survives with zero demos and a "no demos" note; the user can merge it into the successor. |
| Browser host: nothing persists. | Certain. | Session-only, stated in the panel, recorded in wasm-matrix.md. |

Unknown: whether FACEIT and POV demos populate `player_team` the same way. Inputs Per Demo Source can add a roster column at no cost while it is probing sources.

---

## 7. Test and verification strategy

Unit tests in `src/App/DemoViewer.NET.App.Tests/TeamIdentityTests.cs` over synthetic rosters (no demo files):

1. Matching: 3 of 5 matches, 2 of 5 does not; 2 of 2 matches for wingman; 3 of 4 matches. Ties: an established roster beats an unestablished one at equal overlap; otherwise the roster founded first wins.
2. A roster never takes both sides of one demo; the larger overlap wins.
3. Two sightings found a roster; one does not. The same five seen twice establishes `coreLineup` at once; two sides sharing three do not.
4. Two tiers: after a five is established, a side with three of the five plus two others matches at tier 1 with `standIn` true, and a side with two of the five plus three members of the old extended core does not match. Before a five exists, the extended core of 7 is the anchor: after 20 sides with rotating fifths, it is the constants plus the most frequent fifths, and a side with the user plus two acquaintances outside it does not match.
5. Name seeding: "FURIA" x4 and "Furia" x1 fold to one name; a user rename is never overwritten by a later tag.
6. Us and me: `ourSide` resolution order (override, team, me); `SetUs` clears the previous team; no opponent when `ourSide` is null.
7. Merge: sides re-assign, rosters concatenate with their anchors intact, tombstone written, a rebuild does not recreate the merged id.
8. Split: new id, both survive a rebuild.
9. Rebuild is id-preserving: rename a team, delete `team-index.json`, rebuild, the name and id are still on the same demos.
10. Overrides key by sha256 when present and upgrade from `StableKey` when the hash appears.
11. Store: atomic write, corrupt `team-index.json` triggers a rebuild rather than a crash, corrupt `teams.json` is refused (never overwritten) and reported, browser host is in-memory.
12. Schema snapshot: `teams.json` and `team-index.json` round-trip pinned by a serialized fixture, the way `annotations-format.md` pins the sidecar.
13. Coaches: a roster entry with `IsCoach` true is not in the side key; a six-member side with one coach yields a five-member key.

Corpus test: a fixture of anonymised side keys derived from the user's cache (SteamIDs replaced by stable fakes, 554 sides, dates kept) committed under the test project. Asserts the chosen §3.3 row for k = 3, cap 7, two-tier: 24 rosters, 5 with an established five, largest 18 sides over 29 members, 428 unaffiliated, 19 of 90 matches at tier 1; and the pro result: 3 rosters, all three fives established at their second sighting, 13 of 13 at overlap 5, no stand-ins. This is the regression pin for the constants.

Manual verification at build time: run the Teams panel over the user's library, confirm the "me" suggestion is the expected account, name the trio team, set us, and check that the Library "Us" filter returns 266 demos and that a pro demo shows "FURIA vs Team Vitality" on its card.

---

## 8. Decisions

1. **D1 (plan §6): ship order.** Recommendation unchanged: approve this design in Phase 0; build the service, both stores and the Library filter in Phase 1 immediately after Result Cards And Walking and before Search Filters And Live Count, which lists Team Identity as a dependency. The Teams panel can follow one step later; the Library filter and "me" seeding are enough for Watched Situations.
2. **Threshold and anchor:** decided at review (TI-V2, TI-V5): `k = min(3, |side|)` per Valve §3.2.5(a) and §3.10.1; the fixed five as the tier-1 anchor, established when the same five is seen twice; the extended core of 7 as the tier-2 anchor. Both constants stay constants with the measurement beside them, not settings.
3. **"Me" as a list of accounts, stored in `teams.json`,** with the >50% share suggestion. Accept, or require manual entry only.
4. **Matchmaking provenance:** the label vocabulary has no value for a demo whose "our side" comes from `me` with no tags. Options: add `matchmaking` to Demo Provenance Labels (recommended), or default such demos to `scrim`.
5. **Teams panel as its own Main tab** versus a pane inside the Library. Recommendation: its own tab, following F14, because merge and split need room the Library card grid does not have.
6. **Auto team visibility:** show auto teams with 2 sides in the panel (recommended, they are what the user names), or only from 3 sides.
7. **Storage split:** `teams.json` under the config root and `team-index.json` under `cache/`. Accept, or fold both under `cache/` with the truth file exempt from the "rebuildable" rule.

---

## 9. Effort estimate and sequencing

| Step | Estimate | Depends on |
|---|---|---|
| Models, two stores, clustering core, unit tests 1 through 12 | 2 days | nothing |
| `TeamIdentityService`: startup, incremental hook on `DemoCacheStore.Changed`, rebuild, corpus fixture | 1.5 days | step 1 |
| Library: team filter, card subtitle, us mark; Match Overview labels | 1 day | step 2 |
| Teams module: tab, VM, view, actions, browser note, wasm-matrix row | 2.5 days | step 2 |
| "Me" suggestion and confirmation flow | 0.5 day | step 4 |
| Provenance default hook (in the Demo Provenance Labels item, not here) | 0 here | step 2 |

About 7.5 days. Steps 3 and 4 are independent of each other. Nothing in this item touches the engine, the baker or the CSVG plugin.

---

## 10. Sources

- `plan.md` §2 F4, F9, F13, F14, F15, F17; §3 Team Identity, Content Identity, Demo Provenance Labels; §6 D1, D2.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheModels.cs:68-81, 216, 233, 246-247, 284-296, 337-370, 392`.
- `src/App/DemoViewer.NET/Services/DemoCache/DemoCacheStore.cs:24-31, 48-59, 157-170, 263-318, 500-511, 559-573`.
- `src/App/DemoViewer.NET/Modules/Library/DemoLibraryService.cs:191-215, 1302-1360, 1362-1397, 1403-1451`.
- `src/App/DemoViewer.NET/Modules/Library/DemoLibraryModels.cs:153-167, 375-382`.
- `src/App/DemoViewer.NET/ViewModels/Library/LibraryTabViewModel.cs:575-633`.
- `src/App/DemoViewer.NET/ViewModels/MatchOverview/MatchOverviewTabViewModel.cs:828-836`.
- `src/App/DemoViewer.NET/Modules/Highlights/HighlightsModule.cs:31-61`; `src/App/DemoViewer.NET/App.axaml.cs:838-871`.
- `src/App/DemoViewer.NET/Services/AppPaths.cs:74-119, 128-136`; `src/App/DemoViewer.NET/Services/BookmarkStore.cs:21-77`.
- `src/App/DemoViewer.NET/Services/DemoProcessing/IDemoEvaluator.cs:22-47`.
- CS2DemoKit 0.12.0 `CS2DemoKit.Parser.xml`: `ParsedDemo.Players`, `PlayerInfo`, `PlayerInfo.IsHltv`, `DemoSourceClassifier`.
- `docs/playback2d-v2/wasm-matrix.md` (Degraded table); `docs/playback2d-v2/annotations-format.md` (`demo.sha256`, `clock`).
- Measurements: `cluster.py` and `incremental.py`, run 2026-09-23, and `twotier.py` (two-tier rule, both tie rules, both establishment rules, the cap and k sweep), run 2026-09-24, over `%APPDATA%\DemoViewer.NET\cache\demos` (285 parsed records: 277 Valve matchmaking replays from the Steam `replays` folder, builds 10231 through 10896, and 8 HLTV demos from `\\BLACK-BOX\Demos\Pro Demos`; the same 285 records on both dates, and `incremental.py` reproduces its 2026-09-23 row exactly). The tour sample was not used.
- `ValveSoftware/counter-strike_rules_and_regs`: `tournament-operation-requirements.md` §1.2, §3.2.5(a), §3.10.1; `major-supplemental-rulebook.md` Team Requirements (see §11).

---

## 11. Review notes: alignment with Valve's competition rules (2026-09-23)

Added at review, after reading `ValveSoftware/counter-strike_rules_and_regs`
(`tournament-operation-requirements.md`, `major-supplemental-rulebook.md`). Valve's rules are the
only published definition of "the same team" in professional Counter-Strike, so the design's identity
rules are checked against them here. **Folded into §3 on 2026-09-24**; the "Design (§3)" column below
describes the first draft and is kept as the record of what changed and why. TI-V2's re-measurement is
the §3.3 table.

### 11.1 What Valve defines

| Rule | Source | Text |
|---|---|---|
| A roster is five people, not an organisation | tournament-operation-requirements §1.2 | "A collection of five specific Athletes, irrespective of their current or later association with a given Team organization." |
| Continuity is three of five | §3.2.5(a) | at minimum "three of the Participating Roster's Athletes were on the Invited Roster on the Invite Date and will play in each event match." |
| Enforced by forfeit | §3.10.1 | "Tournament Operator will declare a forfeit in any match in which a roster does not field at least three of the Invited Roster Athletes for the entirety of the match." |
| The identity anchor is a snapshot | §3.2.5(a) | overlap is measured against "the Invited Roster on the Invite Date", a fixed five, not a rolling membership. |
| Registered roster is at most seven people | major-supplemental-rulebook, Team Requirements | "The registered roster must match the invited roster ('Core Lineup')"; "may optionally include 1 coach"; "may optionally include 1 substitute player"; "The coach and substitute may be the same person." |
| Substitution is limited | Substitutions | "Each team can substitute a player with their registered substitute once during the event." Between matches unrestricted; mid-match only for medical emergencies. |
| Names follow the media | Team Names and Logos | "registered and represented as they are commonly seen in their own media and in third party esports media." |

### 11.2 Where the design agrees, and where it does not

| Topic | Design (§3) | Valve | Verdict |
|---|---|---|---|
| Unit of identity | `Team` = GUID + one or more `epochs`; matching on SteamID64 sets; the name is display only | The roster (five athletes) is the identity; the organisation is incidental | **Aligned in substance, inverted in vocabulary.** The design's *epoch* is Valve's *roster*; the design's *team* is the organisation-level grouping that `Merge` produces. Rename: epoch → **Roster** (with a `coreLineup` of exactly five), team → **Team** (user-owned continuity across rosters). |
| Continuity threshold | `k(S) = min(3, |S|)` | three of five | **Aligned, and now sourced.** Keep; cite §3.2.5(a) and §3.10.1 in §3.3. |
| The anchor | rolling core of the top 7 members by appearance count | a fixed Core Lineup of five at a point in time | **Divergent.** For a pro or scrim-team library the anchor should be the fixed `coreLineup` snapshot the design already writes to `teams.json`. The top-7 rolling core is a matchmaking accommodation for rotating fifths (the hub problem in §3.3) and should be the fallback, not the rule. Two-tier matching: (1) `|S ∩ coreLineup| >= 3` against each roster's five; (2) only for rosters whose five cannot be established (fewer than five recurring members), the extended core of up to 7. Note Valve's own maximum of seven registered persons (five plus coach plus substitute) makes the 7 defensible as a ceiling, but for a different reason than §3.3 gives. |
| Substitutes | one or two changed members still match; three or more found a new team | one registered substitute, used once per event; three or more changes means a different roster | **Aligned.** Four core plus a stand-in is overlap 4. The design's drift suggestion (core differs by 3 or more across the last five sides) is exactly Valve's new-roster boundary. Add: when a side matches with overlap 3 or 4 and carries non-core players, stamp the assignment `standIn: true` so the Dossier, Period Diff and the Strat Record Panel can say "with a stand-in". |
| Coaches | side key = every non-bot SteamID64 with `Team` 2 or 3 at end of demo | a coach is registered but is not one of the five | **Risk, unverified.** `CCSPlayerController.m_iCoachingTeam` (int32, "Team number this player is coaching (0 if not coaching)") identifies a coach. Whether a coach's own `m_iTeamNum` reads 2/3 or spectator in an HLTV demo is not measured; if 2/3, the side key becomes six people and the Core Lineup cannot be five. Add `IsCoach` (`m_iCoachingTeam != 0`) to `CachedPlayerInfo` as an additive field and exclude coaches from side keys; verify on the HLTV demo that Inputs Per Demo Source re-acquires. `CachedPlayerInfo` today carries `Slot`, `Name`, `SteamId64`, `Team`, `IsBot` only (`DemoCacheModels.cs:68-81`). |
| Names | seeded from `CtClan`/`TClan`, user-renamable, GUID identity | as seen in media | Aligned. |
| One roster, one side | a team never takes both sides of one demo | not stated | Aligned; keep. |
| Regional assignment | not modelled | majority of players' region | Out of scope; no action. |

### 11.3 Decisions, informed by the rules

| # | Decision | Recommendation |
|---|---|---|
| TI-V1 | Adopt Valve's vocabulary: **Roster** (identity: a Core Lineup of five, 3-of-5 continuity) and **Team** (organisation-level grouping over rosters, user-owned, created by naming or Merge). Rename `epochs` → `rosters`, `core` → `coreLineup` plus `extendedCore`. | Yes. It costs a rename in §3.2 and §3.6 and makes Period Diff read as "roster to roster", which is what analysts mean. |
| TI-V2 | Match against the fixed `coreLineup` first; use the extended core of up to 7 only when a five cannot be established. | Yes. Re-run the §3.3 measurement on the user's library with the two-tier rule before approval; the 24-team result in the table may change for matchmaking and should not for the 8 HLTV demos (FURIA, Vitality, NaVi were 5-of-5 clusters already). |
| TI-V3 | Stamp `standIn` on an assignment whose overlap is 3 or 4 with non-core players present, and surface it. | Yes; cheap, and it is the field the Dossier's "roster changed" diff and the Record Panel's caution both want. |
| TI-V4 | Add `IsCoach` to `CachedPlayerInfo` from `m_iCoachingTeam` and exclude coaches from side keys. | Yes, as an additive field (no schema bump, per the cache's own convention). Verification on an HLTV demo is a condition of approval, not of the design. |
| TI-V5 | Keep `k = min(3, |S|)`. | Approve; it is Valve's rule, cited. |

### 11.4 Sources

- `ValveSoftware/counter-strike_rules_and_regs`, `tournament-operation-requirements.md` §1.2, §3.2.5, §3.10.1.
- `ValveSoftware/counter-strike_rules_and_regs`, `major-supplemental-rulebook.md`: Team Requirements, Substitutions, Team Names and Logos.
- CS2OpenDev-Docs, `docs/generated/schemas/client/CCSPlayerController.md`: `m_iCoachingTeam`, `m_iTeamNum`, `m_iPendingTeamNum`.
