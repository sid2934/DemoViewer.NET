# Tour sample demo

> Lives here, not next to the asset it describes. Both shipping paths copy `assets/` by wildcard —
> the Desktop `Content` glob (`assets\tour\**`) and `scripts/publish.sh` (`cp -R assets "$OUT/assets"`)
> — so this file shipped inside every installer while it sat at `assets/tour/README.md`, putting repo
> paths and internal reasoning in front of end users. `assets/` holds shippable payload only;
> `ReleaseGateTests` fails the build if a `.md`/`.txt` reappears under it.

`assets/tour/sample-de_nuke.dem` (~11.0 MiB) is the bundled sample match the Library's "Try a sample match"
CTA and the first-run walkthrough open when the user has no demos of their own. It ships in every
installer automatically — `scripts/publish.sh` copies the whole `assets/` tree next to the binary,
and `TourDemoLocator` (App layer) resolves the first `assets/tour/*.dem` by walking up from
`AppContext.BaseDirectory`.

## What it is

The first **3 rounds** of a **professional tournament GOTV demo** — Vitality vs FUT, map 3, de_nuke
— produced by the demo trimmer's `v3c` rung (inner-message strip of animation + usercmd payloads,
contiguous entry — the app-loadable variant). It is deliberately an *incomplete* match: scores read
3 rounds, not a final scoreboard.

Trimmed GOTV demos structurally lack the initial team seating (`player_team` is only ever emitted
at the halftime swap), so the trimmer synthesizes those events into the output — the file is
self-describing and needs no app-side special-casing. See
`docs/research/demo-trimmer-poc.md` and `tools/DemoViewer.NET.DemoTrimmer/TeamEventSynthesizer.cs`.

## Why a PRO demo, and not the matchmaking reference demo

**This ships publicly, so its contents are published.** A CS2 demo's `userinfo` string table carries
every participant's in-game name and SteamID64, and a SteamID64 resolves straight to a public Steam
profile. The sample was originally the repo's own matchmaking de_nuke reference demo, which meant
bundling **ten private individuals'** identities into a globally downloadable installer — materially
different from the in-Steam demo sharing they had opted into, and irreversible once published.

Tournament GOTV demos are already publicly distributed by the organiser, and the participants are
public figures competing publicly, so republishing that same data changes nothing about their
exposure.

**Two constraints when choosing a replacement source**, both learned the hard way:

1. **Exactly 10 named non-HLTV entries.** Match Overview counts every named entry that is not the
   HLTV proxy, but only rosters `Team == 2/3` — so a demo carrying observers/coaches/admins shows a
   headline player count higher than the two rosters beneath it, breaking the invariant the
   fakeplayer/ishltv fix restored. The `furia-vs-vitality-*` demos each carry **3 extra
   non-roster accounts** and would display "13" over rosters of 10. The `vitality-vs-fut-*` demos are
   clean: 10 players + 2 `CSTV` proxies.
2. **Verify before shipping**, do not assume. Parse the candidate and confirm
   `counted == rostered == 10` and that the extras list is empty.

## Regenerating

```sh
dotnet run --project tools/DemoViewer.NET.DemoTrimmer -c Release -- \
  trim demos/pro-demos/vitality-vs-fut-m3-nuke.dem \
  --out demos/trimmed --rounds 3 --variants v3c --prefix pro-vit-nuke
cp demos/trimmed/pro-vit-nuke-v3c-no-usercmds-contiguous-3r.dem assets/tour/sample-de_nuke.dem
```

The trimmer verifies every emitted candidate (re-parse, entity replay, game-event and team-seating
checks) and exits non-zero on any failure — the current file passed 27 checks. Any
`assets/tour/*.dem` swap is a one-file replacement: the locator picks the first `.dem` it finds, and
nothing keys on the filename, so the name is kept even though the source match changed.
