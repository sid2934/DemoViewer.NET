# End-User Feature References

This directory holds source files that were carved out of `src/App/DemoViewer.NET/`
during the UI v2 refactor (Phase 1). They are **not built into either the desktop
or browser app**. They exist purely as reference implementations of end-user
("watch the match") features that may be useful starting points if/when
non-developer-centric windows or tabs are revived.

## Pinning

These files were extracted at commit `5d1daba` (UI v2 Phase 0: palette
consolidation) on branch `ui-v2/refactor`. They are unlikely to compile
against later versions of the project without adaptation. `MainViewModel`,
`HarvestCardViewModel`, and adjacent types are being refactored in subsequent
phases and the public surfaces these samples bind against will shift.

If you intend to use one of these as a starting point, treat it as a sketch
of *intent*, not a drop-in component. The data flows (where it sourced
snapshots from, which events it subscribed to) are the durable part. The
specific bindings will need to be re-wired to whatever the current shell
exposes.

## What's here

| Path | Purpose |
|---|---|
| `Views/MapView.cs` | 2D map rendering primitives; drew player positions on a CS2 map texture. |
| `Views/ReplayMiniMapControl.axaml(.cs)` | Mini-map composition built on `MapView`. |
| `Views/PlaybackWindow.axaml(.cs)` | 2D player-position playback window (separate top-level `Window`). |
| `ViewModels/PlaybackViewModel.cs` | Playback state machine + interpolation between ticks; consumed `MainViewModel.EntitiesRefreshed`. |

## Why these and not others

The Phase 1 deletion list also removed `StatsTableControl`, `PlayerDetailWindow`,
the nine `Stats*.cs` files, `PlayerDetailViewModel`, `ReplayPlayerViewModel`,
and `HarvestWindow` (a 1235-line UI gallery). Those were generic plumbing: table
layout, snapshot wiring, a style showcase, all easy to recreate when needed.
The four files preserved here contain visual/spatial code (map projection,
interpolation, miniature canvas composition) that is non-trivial to recreate
from scratch and may save a future engineer real time.
