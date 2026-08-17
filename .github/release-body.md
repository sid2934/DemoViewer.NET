## What's new in 0.7.0

**DemoViewer.NET is now open source.** The full source lives at
[github.com/sid2934/DemoViewer.NET](https://github.com/sid2934/DemoViewer.NET) under MIT, and
releases and auto-updates now come straight from this repository. The parser and analysis engine
also ship as NuGet packages (`Cs2DemoKit.*`) for anyone building their own demo tools.

**Damage stats no longer overcount burst hits.** When several hits landed in the same GOTV frame —
shotgun pellets, sprays at tournament tickrates — enemy damage could exceed the health the victim
actually had. Same-frame hits are now capped at the victim's remaining health, so damage totals
match the scoreboard.

**Analysis allocates about half the memory it did.** Rule evaluation now runs on chunked
copy-on-write snapshots with a wrapper cache, cutting its allocations roughly in half versus 0.6.0
— below where the app sat before rich highlights landed.

**Entity data now rides the community SDK.** Entity schemas and game-event definitions come from
the community-maintained [CS2OpenDev](https://github.com/CS2OpenDev) SDK packages instead of a
private generated layer. Decoding behaviour is unchanged — verified field-by-field against the
previous implementation on real demos — and definitions stay current as CS2 updates.

**Quality of life**

- Graph breakpoint conditions now evaluate against the event that fired them, closing a gap where
  `event.tick` could read from the wrong event.
- Parser tab source links work again (paths broke in an internal project reshuffle).
- The Entity Tracking inspector only offers entity links for fields that are actually entity
  handles, instead of dressing every handle-shaped value as one.

<details>
<summary>What was new in 0.6.0 and earlier</summary>

**0.6.0** turned the update offer into a full release-notes window with a once-per-update "What's
new" screen, added a damaged-demo banner, made Settings navigable with a jump-chip strip, checked
for ffmpeg before reels start, extended every theme to all surfaces, and added keyboard shortcuts,
window-state memory, and humane error messages.

**0.5.2–0.5.4** brought the in-app updater, the Match Overview landing page with a bundled sample
match, a first-run walkthrough, roughly 24× lower memory after closing a demo, rich play-based
highlights with in-app reels, and a long list of scoring/roster correctness fixes.

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
