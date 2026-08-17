## What's new in 0.6.0

**Updates grew a face.** The update offer is now a window — version, release date, and the full
release notes, rendered in-app — instead of a one-line banner. The banner remains as a reminder
with a **Details…** button. And after any update, the first launch shows a **What's new** window
with the notes for the version you just received, once, so you never have to wonder what changed.

**The app now tells you when a demo is damaged.** Demos with corrupted data used to produce a
plausible-looking match page with no players and no explanation. The parser now reports what it
had to skip, and Match Overview shows a **"This demo may be damaged"** banner explaining that
names, rosters, or events may be missing.

**Settings got findable.** A jump-chip strip takes you to any section in one click, sections are
ordered by how often you need them (Updates is near the top now, not buried last), and
developer-only knobs are folded into collapsed Advanced groups. Newly editable in Settings: the
CS2 game-window size for Live Sync, the diagnostics log-buffer caps, and the tick-offset shim.

**Reels tell you about ffmpeg up front.** If ffmpeg isn't installed, the Reels page now says so
before CS2 ever launches, with instructions (winget one-liner, download link, or a no-PATH-edits
drop-in folder) and a Re-check button — instead of a raw error minutes into a render. Also fixed:
several messages incorrectly said reels need OBS; they need ffmpeg.

**Every theme now reaches every surface.** Around eighty colors — message-type accents, log
severity tints, the hex-view highlight ramp, command-palette glyphs, breakpoint dots, library map
accents, and more — were hard-coded for the dark theme. They now follow your theme, so Light and
High-Contrast look right in the Parser, Output, and Diagnostics surfaces too.

**Quality of life**

- Keyboard shortcuts: **Ctrl+O** open, **Ctrl+W** close demo, **Ctrl+,** settings, **Ctrl+B**
  bookmark, **Ctrl+1–9** switch tabs.
- Window size and position are remembered between launches.
- Demo parsing shows a progress indicator instead of bare text.
- Error messages are written for humans now ("Couldn't load the demo — the file's data is not in
  the expected format") with the technical detail routed to the Diagnostics tab.
- The library explains itself when a folder has no demos or your filters match nothing.
- Highlight scans show "N of M scanned" with a real progress bar.
- The rule-editor's autocomplete finally narrows to what fits where your cursor is, and opens as
  you type.
- Icon-only buttons are labelled for screen readers.
- A failed click on a link or folder now says so in the status bar instead of doing nothing.

<details>
<summary>What was new in 0.5.x</summary>

**0.5.2–0.5.4** brought the in-app updater (checks at launch, downloads only on your click), the
Match Overview landing page with a bundled sample match, a first-run walkthrough, roughly 24×
lower memory after closing a demo, rich play-based highlights with in-app reels, and a long list
of scoring/roster correctness fixes (GOTV proxy exclusion, bot flags, assist counts, tournament
round wins).

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
