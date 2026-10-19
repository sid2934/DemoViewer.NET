## What's new in 0.9.0

Extensions from outside this repository, a library that knows a demo by its content rather than its
path, one parse per demo for everything the app reads from it, and an opt-in beta channel.

**Third-party extensions.** Drop an extension into the `extensions` folder under the app's
config folder and it loads on the next launch, in its own load context, beside the Strat Book, as
long as it is compatible with this version. An unsigned one loads only with **Allow
unverified and potentially dangerous extensions** turned on in Settings, which is off by default. A
broken signature never loads, and an extension that claims a `net.demoviewer.*` id is refused.

**An extension can't take the app down with it.** Every call into an extension is guarded. Three
faults in a minute, or ten in a session, switch it off for the rest of the session with a notice.
One that throws while registering is left out and the app starts without it. Launch with
`--safe-mode` to load no extensions at all; the app also starts in safe mode on its own after a
launch that never finished, a crash with an extension on the stack, or a freeze you had to kill.

**Moved, renamed and remounted demos keep their analysis.** The cache used to key a demo by its
path, so moving a file, renaming it, or reaching it through a second mount parsed it again from
scratch, and a bad prune could delete results. A demo is now known by its SHA-256, with every path
it has been seen at kept as a location. A copy is recognised from its size and a hash of its head
and tail, so a second mount of the same folder folds onto the cards you already have. Removing a
folder detaches it rather than deleting anything, and a demo with no location left is only swept
after a grace period. Recents, extension data and Strat Book indexes follow the content too. The
first launch after updating renames the existing cache files in one queue item; it reads no demos
and steps aside if you open one.

**Network folders stop costing you analysis.** A folder used to count as scanned before it had been
listed, so an automount that had not mounted yet looked empty and pruned everything under it, and
one slow share held up the whole queue. Listing and header reads now run as short queue slices with
a time bound, and a folder counts as reached only when its walk completes. Network demos are no
longer memory-mapped.

**One parse per demo.** Library score, clans and coaches, highlights, round facts, the Strat Book's
round index, suggested tags and the grenade walk each used to walk the demo on their own. They now
all read off the one rules run. On the benchmark demos a library sweep is 26 to 48% faster and
opening a demo 21 to 38% faster.

**The processing queue panel.** Running work sits on top and queued work is listed in the order it
will start. Finished work moves to a Recent tab with the last 25 items and their errors.
Right-click a queued item and **Move to top** to make it next, without interrupting what is running.

**Beta releases, if you want them.** Settings > Updates has **Subscribe to beta releases**, off by
default. Betas can break your configuration or corrupt cached data. Subscribed copies always move to
whichever is newer, the latest beta or the latest full release, and turning it off never downgrades
you: you stay put until a full release passes your build. Unsubscribed copies never see a beta.

**Smaller fixes.** A surrendered match keeps the score it was read at. The Background Work chip
counts only the work the concurrency setting controls. A full queue no longer keeps the planner
spinning. Context menus and flyouts take their colours from the theme instead of Fluent's gray,
under every theme.

<details>
<summary>What was new in 0.8.2 and earlier</summary>

**0.8.2** brought the Strat Room as the Strat Book extension: Situation Search, Round Tagger,
Strat Book, Utility Book, the Review Queue with reels, and the Opponent Dossier, all behind one
switch that gives back its memory when turned off. The analysis engine moved to the 0.13 line: a
weapon's clip reads the real magazine count, smokes decode their full state, and a round's winner
comes from the server's own verdict.

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
