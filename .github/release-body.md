## What's new in 0.8.0

The 2D playback view is rebuilt. It renders through a new Skia compositor, and that rework is what
the rest of this release is built on: annotations you can draw on the map, video export straight out
of the 2D view, maps with more than one floor, and a scrubbable timeline.

**Annotations.** Draw and erase on the 2D view, with undo/redo and a color picker. Each stroke has
its own time envelope, so it can appear and fade when you want instead of hanging over the whole
round. Strokes anchor either to the map or to a player, so they survive seeking and level switches
rather than drifting. Everything saves to a `.dvann.json` file next to the demo, so a marked-up demo
stays marked up.

**Video export from the 2D view.** Export any range as WebM, MP4, or GIF, HUD included. ffmpeg is
picked up from your PATH if you have it and downloaded on demand if you don't, and there is a
built-in GIF encoder that needs no ffmpeg at all. The default is 720p60, which encodes at about 2.7x
realtime. Export declines to start while LiveSync or a reel job is running rather than fighting them
for the demo.

**Maps with more than one floor.** Multi-level maps now have a real layered level model instead of
one flattened overhead. Pick a level by hand or let it switch as the action moves, and following a
player always puts you on that player's floor.

**A timeline along the bottom.** Scrub the demo directly, with tracks for rounds, kills, the bomb,
and your own annotations, and hover to see what is under the cursor. There is a full keymap for
playback speed, follow, fit, and the drawing tools.

**Follow a player by clicking their card.** Selecting a card in the overview follows that player in
the 2D view, and in-engine as well when LiveSync is on.

**A headless `dv2d` command.** Render frames, export video, run benchmarks, and verify goldens from a
terminal with no UI, against the same renderer the app uses.

Underneath all of it, drawing a frame is one Skia operation that allocates nothing per frame and
holds p99 2.5 ms at 1080p against an 8 ms budget. The previous 2D control is still available behind a
settings toggle for this release and is removed in the next one.

<details>
<summary>What was new in 0.7.2 and earlier</summary>

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
