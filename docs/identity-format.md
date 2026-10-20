# The `Identity` settings section (schema v1)

Who the user is: the Steam accounts that are theirs and the one that is active. The section lives in
`settings.json` beside every other preference. This document is for anyone who reads or writes it. A
committed sample lives at [`tests/fixtures/identity/settings.identity.sample.json`](../tests/fixtures/identity/settings.identity.sample.json)
and is pinned by `IdentitySettingsTests.V1Schema_MatchesTheCheckedInSamples`. Regenerate it with
`IDENTITY_GOLDEN_UPDATE=1`.

## Where it lives

| Condition | Location |
|---|---|
| Desktop | `<app config root>/settings.json`, under `Identity`. |
| Browser build | The in-memory settings; nothing persists. Settings shows no Identity section there. |

`IdentityService` is the only writer, through `SettingsService.Write`, so the section is written with
the rest of the file, atomically. Extensions read it through the SDK and never write it.

## The section

```jsonc
"Identity": {
  "Accounts": [
    { "SteamId64": "76561198000000001", "Label": "main", "AddedUtc": "2026-10-19T00:00:00Z" }
  ],
  "ActiveSteamId64": "76561198000000001",
  "DismissedSuggestions": [ "76561198000000009" ],
  "MigratedFromTeamsJson": true
}
```

* **`Accounts`**: every account that is the user, in the order they were added. `SteamId64` is a
  non-zero SteamID64 as a plain decimal string. The Settings input adds only individual accounts
  (`76561197960265729` to `76561202255233023`); `teams.json` and the suggestion may carry any id the
  library does.
  `Label` is the user's own name for it, or null. `AddedUtc` is when it was added.
* **`ActiveSteamId64`**: the account the Player Overview opens on. Always a member of `Accounts`, and
  null only when `Accounts` is empty.
* **`DismissedSuggestions`**: accounts the user answered "Not me" to. They are never suggested again.
* **`MigratedFromTeamsJson`**: true once the Strat Book's `teams.json` `me.steamIds` has been read in.

SteamIDs are strings everywhere, so no reader loses precision to a double.

## Rules

* The active account is a member of `Accounts`. When it is unset, or names an account that is not
  there, the first account is active. A hand edit that breaks this is read as if it held, and written
  back corrected on the service's next write. An id that is not a SteamID64, and a repeat of an id,
  are dropped the same way.
* Removing the active account makes the first remaining one active. Removing the last leaves the
  list empty, and it stays empty: nothing refills it on its own.
* **The suggestion.** When at least 20 library demos hold any account and one account is in more than
  half of them, Settings asks "Is this you?" about it, unless it is already the user's or was
  dismissed. Accepting adds it, and makes it active only when the list was empty. Nothing is written
  until the user answers.
* **The `teams.json` import.** At startup, before any extension attaches, a desktop build that has
  not set `MigratedFromTeamsJson` and finds `<app config root>/teams.json` reads its `me.steamIds`
  (keys matched without regard to case, entries that are not SteamID64 strings skipped). When
  `Accounts` is empty those ids become the accounts and the first is active. Either way the flag is
  set, so the import never runs again. A `teams.json` that does not parse imports nothing and sets the
  flag; one that cannot be opened is tried again at the next start. The file is only read.

## What the Settings input accepts

A SteamID64; a `steamcommunity.com/profiles/<id>` link; `[U:1:n]`; `STEAM_X:Y:Z` (read as
`76561197960265728 + 2Z + Y`); and a bare account id under 2^32, read as an individual account and
said so. A `steamcommunity.com/id/<name>` link needs an online lookup, so Settings says to paste the
`/profiles/` link or the SteamID instead.
