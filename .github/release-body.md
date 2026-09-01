## What's new in 0.7.2

A maintenance release built on new parser and analysis packages. One fix in it is worth upgrading
for on its own.

**Demos that used to fail analysis outright now work.** Some demos died with an unhandled error the
moment analysis started: not a degraded result, no result at all. The cause was a frame sharing a
tick with a checkpoint the analyzer had picked, and across a corpus spanning every protocol version
in a real matchmaking replays folder it took out 6 of 15 demos. All 15 now analyze. If you have a
many-core machine you were far more likely to hit this: the analyzer picks more checkpoints the more
cores you have, so above roughly 32 logical cores nearly every clash landed, while the same demos
were fine on a 10-core box. That is also why it was hard to reproduce from a bug report.

**Analysis is faster and much lighter on memory.** Against 0.7.1, measured as the median of three
runs across nine demos: parsing is about 25% faster, evaluation about 13% faster, and total
allocation is down between a third and a half.

**A dead player's entity reference no longer resolves to the wrong entity.** One of the two
"no such entity" markers was not being recognized in the app's read-only entity view, so instead of
reading as empty it resolved to whatever happened to occupy that slot.

**The "What's new" window no longer opens before the app does.** On the launch after an update it
could appear ahead of the main window.

<details>
<summary>What was new in 0.7.1 and earlier</summary>

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
