## What's new in 0.8.2

The Strat Room: six features for a team working through its own demos, shipped as the Strat Book
extension on top of a new extension framework in the app.

**Situation Search.** Describe a situation and get back every round in your library that matches it.
Build the query on a canvas, or open a round in 2D Playback and press Ctrl+F to find rounds like
that one. A tolerance slider decides how close a match has to be, and the results come back as
cards you can walk straight into playback.

**Round Tagger.** Tag rounds from a shipped palette or with your own labels, with the facts the
engine already knows about a round filled in for you. Detectors propose tags and you accept or
reject them in a review loop, rather than having them written behind your back. Tagging works from
2D Playback too, and the tags get their own lane on the timeline.

**Strat Book.** Write a strat as steps with lines, verbs and locations picked on the map, then watch
the preview walk each player along real nav routes and throw the utility where you put it. Strats
keep their version history, the record panel tracks how one has actually gone, role view shows a
single player's job, and callout aliases let your team's name for a spot work as well as the
canonical one. Strats mined out of your own demos turn up as detected strats, and anything in the
book exports.

**Utility Book.** Every grenade thrown across your demos, indexed, with a lineup card and a clip
for each, and a map view of the utility for a site.

**Review Queue and Reels.** Queue a clip from anywhere in the app, then work through the queue in
review mode. The `dv2d` command renders a whole pack headlessly if you would rather have files.

**Opponent Dossier.** A team's map pool record, how they open rounds, what they do after a plant,
their setups, how the two halves differ, and how they behave in specific situations, with notes you
can edit and an export. Teams come from rosters you confirm yourself; nothing is tracked from
matchmaking automatically.

**All of it is an extension, and off is a real off.** First run asks whether you want the Strat
Book. Turning it off removes its tabs, panels and lanes, stops its indexing passes and its queued
jobs, and gives back the memory it was holding in the session you are in, not after a restart. With
it off, Library indexing does strictly less work than before this release. The extension carries its
own version and updates from its own feed on its own cadence; a new copy is signed, and both the
signature and the host contract it was built against are checked before it is accepted.

**Numbers that move.** The analysis engine moves to the 0.13 line, which the Strat Room needs for
round facts, team-scoped rules and grenade projectiles. Three things change for figures you may
already have written down: a weapon's clip now reads the real magazine count, smokes decode their
full state rather than a partial one, and a round's winner comes from the server's own verdict, with
round-won and round-lost counting only for the team they name.

<details>
<summary>What was new in 0.8.1 and earlier</summary>

**0.8.1** rebuilt the Stats page: a scoreboard with per-column scales and a podium, fifteen
aim-quality columns and an Aim board measuring reaction, time to first damage and spray control from
what the demo records about every shot, a spray plot with your own bullets drawn over the recoil
pattern, collision geometry for all ten shipped maps so the seventeen sight-dependent columns
stopped reading a flat zero, a dash instead of a zero wherever a cell has no data, CS2's own icons
through the app and the kill feed, and an Analysis graph that draws the rules the engine really ran,
per player, instead of its shared scaffolding. Counter-strafe figures fell: the window had been too
long and was crediting shots taken after you had already stopped.

**0.8.0** rebuilt the 2D playback view on a new Skia compositor: annotations you can draw on the map
with their own time envelopes, saved beside the demo; video export straight out of the 2D view as
WebM, MP4 or GIF; maps with more than one floor, with the level switching as the action moves;
a scrubbable timeline with tracks for rounds, kills, the bomb and your annotations; following a
player by clicking their card; and a headless `dv2d` command for rendering, exporting and
benchmarking from a terminal. Drawing a frame became one Skia operation that allocates nothing per
frame and holds p99 2.5 ms at 1080p against an 8 ms budget.

**0.7.2** was a maintenance release. Demos that used to fail analysis outright started working
again: a frame sharing a tick with a checkpoint the analyzer had picked took out 6 of 15 demos
across a real matchmaking replays folder, and the more cores you had the more likely you were to hit
it. Analysis also got roughly 25% faster to parse and 13% faster to evaluate, with total allocation
down between a third and a half. A dead player's entity reference stopped resolving to whatever
happened to occupy that slot, and the "What's new" window stopped opening ahead of the main one.

**0.7.1** was a maintenance release: the parser and analysis engine moved out to
[CS2DemoKit](https://github.com/CS2OpenDev/CS2DemoKit) and are consumed from nuget.org as packages,
shrinking the source tree here by roughly a hundred thousand lines, and the editor schema file for
rule authors became `cs2demokit-rules.schema.json` (existing `dv-rules.schema.json` references keep
working).

**0.7.0** was the open-source debut: the source moved to a public repository under MIT, releases
and auto-updates began coming from it, damage stats stopped overcounting same-frame burst hits, and
analysis allocation dropped by roughly half.

**0.6.0** turned the update offer into a full release-notes window with a once-per-update "What's
new" screen, added a damaged-demo banner, made Settings navigable with a jump-chip strip, checked
for ffmpeg before reels start, extended every theme to all surfaces, and added keyboard shortcuts,
window-state memory, and humane error messages.

**0.5.2–0.5.4** brought the in-app updater, the Match Overview landing page with a bundled sample
match, a first-run walkthrough, roughly 24× lower memory after closing a demo, rich play-based
highlights with in-app reels, and a long list of scoring and roster correctness fixes.

</details>

---

## Install

Download the one installer for your platform. Nothing else is required:

- **Windows**: `…-win-Setup.exe`
- **macOS (Apple Silicon)**: `…-osx-Setup.pkg`
- **Linux (x64)**: the `…-linux…AppImage`

Each installer is **self-contained**: it bundles the .NET runtime **and** the map assets, so a single download has everything.

This build is **unsigned** (signing is planned):
- **macOS**: right-click the app → **Open** on first launch.
- **Windows**: at the SmartScreen prompt, click **More info → Run anyway**.

> The `.nupkg`, `RELEASES*`, and `releases.*.json` files below are what the in-app updater reads; you don't need to download them by hand.
