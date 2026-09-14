## What's new in 0.8.1

The Stats page is rebuilt, and it can finally answer questions about aim rather than just counting
kills.

**A new scoreboard.** The table gained per-column scales, so a number is shaded against the other
players in the match instead of sitting there as a bare figure, and the best in each column is
marked. There is a podium for the top three, team badges, and a sub-nav that splits the stats into
pages rather than one endless table. The whole palette now comes from the theme, so a custom theme
retints the boards instead of fighting them.

**Fifteen aim-quality columns and an Aim board.** How fast you react to someone appearing, how long
from first sight to first damage to the kill, how well you hold a spray, and whether you were
actually stopped when you fired. These are computed from what the demo records about every shot,
not estimated from the scoreboard.

**A spray plot.** Pick a weapon and see its recoil pattern with your own bullets drawn over it, so
a spray-control number has a picture behind it. There is a toggle to measure from the enemy instead
of from your first bullet, which shows tracking rather than pure recoil control.

**Every shipped map has geometry now.** Seventeen of the new columns need to know what you could
actually see, which needs the map's collision. Four maps had none, so those columns showed a flat
zero that looked like a measurement. All ten maps have it now, de_train among them.

**And when something cannot be measured, it says so.** A cell with no data shows a dash and drops
out of the ranking and the shading, instead of showing a zero that reads like a real result. If a
demo is on a map with no geometry, the board says which map and why.

**CS2's own artwork.** Weapons, kill modifiers, ranks and round objects now appear as the icons
Valve drew for them, in the kill feed and through the app, with the text still there underneath
wherever an icon is missing.

One number moves down: counter-strafing was being measured over too long a window and was crediting
shots taken after you had already stopped. The window now comes from the game's own friction values,
so published counter-strafe figures fall. That is the metric getting more honest, not a regression.

<details>
<summary>What was new in 0.8.0 and earlier</summary>

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
