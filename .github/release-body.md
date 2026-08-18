## What's new in 0.7.1

A maintenance release. Nothing in the app looks or behaves differently; the change is underneath.

**The parser and analysis engine are now packages.** They moved out to
[CS2DemoKit](https://github.com/CS2OpenDev/CS2DemoKit) and this app consumes them from nuget.org
like any other dependency, which is why the source tree here shrank by roughly a hundred thousand
lines. Anyone building their own CS2 demo tooling can now install the same parser and analysis
engine this app runs on, without taking the app with it.

**For rule authors:** the editor schema file is now called `cs2demokit-rules.schema.json`. New rule
files get the new name in their `# yaml-language-server:` line automatically. Existing files in
your rules folder still point at `dv-rules.schema.json`, which stays where it is and keeps working
— update the line when convenient to validate against the current schema.

<details>
<summary>What was new in 0.7.0 and earlier</summary>

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

Download the one installer for your platform — nothing else is required:

- **Windows** — `…-win-Setup.exe`
- **macOS (Apple Silicon)** — `…-osx-Setup.pkg`
- **Linux (x64)** — the `…-linux…AppImage`

Each installer is **self-contained**: it bundles the .NET runtime **and** the map assets, so a single download has everything.

This build is **unsigned** (signing is planned):
- **macOS** — right-click the app → **Open** on first launch.
- **Windows** — at the SmartScreen prompt, click **More info → Run anyway**.

> The `.nupkg`, `RELEASES*`, and `releases.*.json` files below are what the in-app updater reads — you don't need to download them by hand.
