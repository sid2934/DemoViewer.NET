# DemoViewer.NET

A desktop app for reading Counter-Strike 2 demo files: match stats and highlights for people who
want to know what happened, and a parser/entity inspector for people who want to know how the demo
says it.

Cross-platform (Windows, macOS, Linux), MIT licensed, built on [Avalonia](https://avaloniaui.net)
and the [CS2DemoKit](https://github.com/CS2OpenDev/CS2DemoKit) parsing and analysis packages.

## Install

Grab the installer for your platform from the [latest
release](https://github.com/sid2934/DemoViewer.NET/releases/latest):

| Platform | File |
|---|---|
| Windows (x64) | `…-win-Setup.exe` |
| macOS (Apple Silicon) | `…-osx-Setup.pkg` |
| Linux (x64) | `…-linux….AppImage` |

Each installer is self-contained — it carries the .NET runtime and the map assets, so one download
is everything. The app updates itself from this repository's releases; you are told about a new
version and it downloads only when you say so.

Builds are currently **unsigned**, so the first launch needs one extra step: on macOS, right-click
the app and choose **Open**; on Windows, click **More info → Run anyway** at the SmartScreen
prompt.

The other files on a release — `.nupkg`, `RELEASES*`, `releases.*.json` — are what the updater
reads. You never need to download those by hand.

## What it does

**Match Overview and Stats.** Open a demo and get the scoreboard: per-player kills, deaths,
assists, ADR, KAST%, HLTV rating, weapon breakdowns, per-round detail. A bundled sample match ships
with the app, so there is something to look at before you have found your own demos.

**Highlights and reels.** Rule-driven detection of aces, clutches, no-scopes, spray transfers,
ninja defuses and the rest, each with the tick it happened on. With CS2 installed you can jump
straight to a moment in-game, or render clips to video (requires `ffmpeg`).

**Library.** Points at your demo folders, reads match metadata in the background, and remembers
what it has seen so a re-open is instant.

**Live Sync.** Drives a running CS2 instance from the app — seek the game to the tick you are
looking at, verify a highlight actually looks the way the rules claim.

**The developer half.** A Parser tab that walks every demo frame down to the message, the byte
range, and the `.proto` field that decoded it; an Entity Tracking tab for entity state and
serializer schemas at any tick; an Analysis tab that shows the rule graph evaluating, with
breakpoints; a 2D playback view; and a Diagnostics tab with live logs and counters.

## Rules

Stats and highlights are YAML rulesets, not hard-coded queries. The shipped set lives in `rules/`,
and the in-app Rule Workbench edits them with completion and validation against the JSON schema.

Your own rules go in a user directory the app creates for you — a ruleset there with the same id as
a shipped one replaces it wholesale, and a new id adds new stats. `enabled: false` turns a shipped
ruleset off without redefining it.

Four of the shipped rulesets (`kast`, `player_stats`, `weapon_stats`, `post_plant_double`) come
from `CS2DemoKit.Analysis` and are kept byte-identical to the package's copies; the ten
`highlights_*` rulesets are this app's own content. The rules language is documented in
[`docs/rules-v2/rules-v2-spec.md`](docs/rules-v2/rules-v2-spec.md).

## Contributing

Bugs and feature requests live in [GitHub
issues](https://github.com/sid2934/DemoViewer.NET/issues). Note that the parser and analysis engine
are a separate project — anything about demo parsing, entity decoding, stat correctness or the
rules engine belongs in [CS2DemoKit's
issues](https://github.com/CS2OpenDev/CS2DemoKit/issues) instead, since that is where the fix would
land.

## Building from source

```sh
dotnet build                                          # whole solution
dotnet run --project src/App/DemoViewer.NET.Desktop   # run the app
```

Needs the .NET 10 SDK. Restore pulls everything from nuget.org except `Cs2VideoGenerator.Core`,
which is a committed package in `local-packages/`.

Tests use [TUnit](https://tunit.dev), not xUnit or NUnit:

```sh
scripts/test-app-suite.sh -n 6                        # app suite, batched — see below
dotnet run --project src/Testing/DemoViewer.NET.LiveSync.Tests
```

The app suite must go through the batch runner. It is a single-process headless UI suite and runs
out of memory on a 16 GB machine otherwise; `-n 6` is the batch count that holds. The Live Sync
suite binds port 50051, which is machine-exclusive — close other CS2 tooling first, and note those
tests skip rather than fail if the port is busy.

The parser and analysis engine are not in this repository. They live in
[CS2DemoKit](https://github.com/CS2OpenDev/CS2DemoKit) and are consumed as NuGet packages, so a
parser or engine change belongs there, followed by a version bump here.

## Licence

MIT — see [`LICENSE`](LICENSE). Portions of the demo decoder are adapted from
[demofile-net](https://github.com/saul/demofile-net), also MIT; see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Counter-Strike and Counter-Strike 2 are trademarks of Valve Corporation. This project is not
affiliated with, endorsed by, or supported by Valve.
