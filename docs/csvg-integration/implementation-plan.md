# CSVG Integration — Implementation Plan

Shipped — merged to `main` 2026-07-20. This stays the plan of record: the §2 verified facts override anything that contradicts them. Claims were verified against both codebases before implementation (2026-07-18).

**Scope:** Live CS2 playback sync (F1), analysis verification in-game (F2), Highlights tab + highlight-reel generation (F3), mock-server test strategy (F4). Companion: `docs/csvg-integration/ux-design.md` (UI/UX specification). The three research appendices this plan was distilled from were retired 2026-08-16; §2 supersedes them.

CSVG = Cs2VideoGenerator (a sibling checkout next to this repo, same maintainer), the CLI + .NET library + C++ in-game plugin that launches and controls a live CS2 instance over a gRPC bidi stream, with OBS-based capture. DV = DemoViewer.NET (this repo).

---

## 1. Goals and scope

| ID | Feature | One-line goal |
|---|---|---|
| F1 (live sync) | Bi-directional playback sync | DV's 2D Playback and a live CS2 instance mirror play/pause/seek/demo-change in real time, either side leading |
| F2 (verify-in-CS2) | Analysis verification | From the Analysis tab (and Highlights tab), seek the live CS2 to a rule-trigger moment, spectating the relevant player, to eyeball rule correctness |
| F3 (highlights) | Highlights tab + reel generation | A Library-like tab browsing analysis-declared highlights across the demo library, with a Create Highlight Reel dialog that drives CSVG compilation capture to produce a video |
| F4 (testing) | Mock-server testing | The whole integration is developable and CI-testable on macOS against CSVG's mock_server; real capture validated on Windows/Linux |

**Non-goals for v1** (each is a tracked follow-up, not silently dropped):
- CS2→DV spectated-player mirroring (needs fresh engine reverse-engineering — no readable spectator surface exists; §5.6).
- Detecting user speed (`demo_timescale`) changes made in the CS2 console (no cheap convar readback; DV→CS2 speed set IS in scope via the new CSVG release).
- Cross-machine operation (plugin dials `localhost:50051` hardcoded; demo paths are passed verbatim — same-machine only).
- Mid-clip camera switching in reels (CSVG roadmap item; one spectator target per clip).
- Browser/WASM anything CSVG-touching — live sync, verify, and reel generation are desktop-only. Exception per ux-design.md §1: the Highlights **tab** itself is CSVG-free and ships on WASM registered-but-degraded (in-memory highlights of the currently-open demo only — no cache, no scan, no reel/verify), mirroring the Library tab's degrade pattern.
- Game-scoped (`for: match`) highlights — the rules engine rejects them at build time today; the pipeline assumes per-player highlights throughout.

**Protected-files statement:** no work item in this plan touches `DemoParser.cs`, `DemoFrame.cs`, `LEB128Utils.cs`, or `BitBuffer.cs`. Everything DV-side lives in the App layer, the Analysis layer (`StateGraphEvaluator.cs` and builders are not protected), new projects, or CSVG's own repo.

---

## 2. Verified integration facts (the ground truth this plan is built on)

Full citations lived in the retired research appendices. The load-bearing facts:

**Tick clocks (corrected during planning):**
- DV's `DemoFrame.ServerTick` (int; `GameTick` is its `int?` alias — there is NO `.Tick`) holds the **demo/frame clock**: pre-game frames use a large negative sentinel, gameplay frames run 1, 2, … (`DemoParser.cs:530-538`).
- `GameEvent.GameTick` is in the **same demo/frame clock** (`GameEventDecoder.Decode`: `gameTick = msg.ServerTick − ServerStartTick`, or `frameTick` directly — `GameEventDecoder.cs:33-36`). `GameEvent.ServerTick` is the **absolute** engine tick; `absolute − ParsedDemo.ServerStartTick = frame clock`.
- `RuleChainEvent.Tick` = the firing frame's `ServerTick` → already frame clock (`StateGraphEvaluator.cs:437,458,488`). **Do NOT subtract `ServerStartTick` from it** (the original highlights research got this wrong; verified against `GameEventDecoder.cs` on 2026-07-18).
- CSVG's wire ticks are int32 **CS2 demo ticks** (`IDemoFile::GetDemoTick()` / `demo_gototick` units — `Cs2PluginClient.cpp:479,530`). Working hypothesis: CS2 demo tick ≡ DV frame clock (both count the same recorded tick range; DV's `TickCount` comes from the same `CDemoFileInfo.PlaybackTicks` CS2 uses — falling back to the highest observed frame tick when `PlaybackTicks` is absent/zero, so truncated demos inherit that fallback, `DemoParser.cs:523-528`). **Unproven** — Phase 0 empirical spike + a `TickOffset` config shim (§6.3).

**CSVG architecture:**
- Role inversion: the .NET side is the gRPC **server**; plugin/mock_server dial in to hardcoded `localhost:50051`. DV's desktop process must host Kestrel HTTP/2 and `MapGrpcService<Cs2GameService>()`; `StartSessionAsync` fail-fast-probes the port (`CsvgClient.cs:705-726`). One orchestrator per machine.
- Every `DemoPlaybackStatusChange` today is a **command echo** — the plugin's polling loop deliberately never reports engine-observed state (`Cs2PluginClient.cpp:739-759`). User in-game pause/seek/spectate/demo-change are invisible on the current wire. The plugin computes `isPaused` and drops it before serialization (`Cs2PluginClient.cpp:131-144`).
- `TickUpdateEvent` default cadence: every 500 ticks (~7.8 s). `SetTickUpdateFrequency(0)` = every observed tick (bounded by the plugin's ~120 Hz poll). Forced sends at pause/resume/seek/range-end. On `SetDemoTick` the plugin force-sends the **target** tick *before* the engine actually seeks (`Cs2PluginClient.cpp:484`) — the echo is not an arrival ack.
- `TickUpdated` is a **synchronous `Action` invoked inline on the single gRPC read loop**; `CaptureProgressUpdated` is a synchronous `Action` forwarded inline on the capture provider's (OBS websocket) event thread (`CsvgClient.cs:1567-1568`), not the gRPC loop. `DemoPlaybackStatusChanged`/`ProcessStatusChanged`/`SessionStarted`/`SessionEnded` are async `Func<…,Task>` but are **awaited serially inline on that same gRPC read loop** (`Cs2GameSession.cs:102,114,155,189-193`) — a slow handler, sync or async, stalls all CSVG event processing; only `StateChanged`/`CommandRejected`/capture-state events are fire-and-forget. Exception isolation is per-subscriber for async events only; the sync `TickUpdated` path has none (a throw skips co-subscribers) — handlers must be exception-free, post-and-return.
- The plugin processes commands sequentially on one reader thread; `HandleLoadDemo` blocks it ~15 s (blind sleep). `Cs2ProcessStatus READY` follows a fixed 5 s sleep. Both apply in mock mode too (shared production client code).
- The plugin has **no reconnect loop** — a dropped stream from a still-running CS2 is permanent until CS2 relaunches. From `CsvgSessionState.Faulted`, `StopSessionAsync()` is the mandatory reset before `StartSessionAsync`. Use `DisposeAsync`, never sync `Dispose` (15 s bounded stop + clears all event subscriptions).
- Engine access is reverse-engineered vtable offsets that churn with CS2 updates (2026-07-09 update broke playback entirely). `IDemoFile::IsDemoPaused` (win slot 012) and `ISource2EngineToClient::GetDemoFilePath` (slot 043) are **declared but never production-called** — unvalidated.
- Spectating is `spec_player "<exact display name>"`; SteamID64 is metadata-only. `spec_lock_to_accountid` is unhidden by the plugin but README documents SteamID targeting as not working (needs re-validation).
- Compilation capture (`CaptureCompilationAsync`) exists and fits F3: groups clips by demo, per-clip options/spectate/capture, FFmpeg concat, progress events. Fail-fast on first failed clip; concat failure still returns `Success=true` (check `ConcatenationResult`); requires an initialized real capture provider (OBS ⇒ Windows/Linux only).
- Mock: mock_server reuses the production plugin client against a `MockEngineInterface`; NuGet bundles win-x64/linux-x64 only (osx-arm64 buildable, not shipped); mock cannot simulate user actions today; demo never ends; any path "loads".

**DV architecture:**
- Playback position unit is the 0-based **frame index**; tick is derived. `PlaybackController` is UI-thread-only; `SeekToFrame` during play pauses first; discrete seeks kick a heavy debounced entity checkpoint-replay. Derive outbound sync intent ONLY from `FrameNavigationViewModel.SelectedFrameChanged` + `PropertyChanged(IsPlaying/Speed)` + `IModuleContext.DemoReset` — never from the render-coalesced `Advanced` push (which also fires after paused discrete seeks and `StepForward`, so its silence doesn't imply paused either).
- A live-sync engine cannot live in a tab VM (tab VMs only get pushes while selected; views are torn down on deactivation) — it must be a host-level service holding `MainViewModel.Playback`.
- The Browser (WASM) csproj references the App project directly; there is no compile-time desktop split. CSVG/ASP.NET Core dependencies must live in a **new desktop-only project** referenced only by `DemoViewer.NET.Desktop`, behind an App-side interface (precedent: `IWindowService`).
- `MainViewModel._loadedDemoPath = localPath ?? fileName` — `IModuleContext.DemoPath` can be a bare filename; validate `Path.IsPathRooted` + `File.Exists` before sending to CS2.
- Highlights at runtime are `RuleChainEvent`s: single tick, no range, no SteamID, no title, ruleset qualifier lost (`_chain_<highlightId>` unqualified). `CheckedHighlight.Title` is never rendered anywhere (the "Phase 2.2d surfacing layer" doesn't exist — F3 is that layer). All lowered highlights are per-player (game-scoped throws at build).
- No analysis-result caching exists; full run ≈ 5 s parse + 3.5 s eval per demo; ONE heavy parse machine-wide (16 GB), and the shell's own load parse is currently uncoordinated with the Library indexer.
- Packaging: DV pins `Google.Protobuf 3.27.3` centrally; CSVG Core needs 3.29.5 (bump required). DV's `Grpc.Tools 2.57.0` pin is unaffected (governs DV's own protoc runs only; CSVG arrives prebuilt). CSVG Core is net10.0 + **GPL-3.0-only**. DV has no LICENSE file (private).

**Corrections** (earlier research claims overturned during verification — this plan is authoritative where they disagree):
1. Tick clocks: `RuleChainEvent.Tick` needs NO `− ServerStartTick` conversion (see the tick-clock block above; the earlier formula was wrong).
2. `PlayDemoTickRangeAsync(record:true)` does NOT "return Succeeded when capture failed" — `DemoPlaybackResult.Success` mirrors `CaptureResult.Success` (`DemoPlaybackResult.cs:55-63`); the true semantic is that capture failures are non-throwing. (The misleading source comment at `CsvgClient.cs:1321` is a CSVG v1.1 cleanup note.)
3. Event threading: `CaptureProgressUpdated` fires on the capture provider's (OBS websocket) thread, not the gRPC read loop; the async status/session events are awaited serially ON the gRPC read loop, not on independent thread-pool threads.
4. A2 hash replay order: highlights hash BEFORE deferred computes, not after (see §7.2).
5. `DemoLibraryService.Save` is best-effort but NOT atomic — not a precedent for atomic writes (see §7.3).

---

## 3. Decisions

All adopted; the license question was the only one that needed an owner call.

- **License.** CSVG Core is GPL-3.0-only; linking it into DV makes distributed DV GPL-encumbered. DV is currently private/unlicensed, so in-proc linking proceeds now; before any public distribution a dual/compliant license gets picked (the same maintainer owns both repos and all contained code). The CLI/compilation-JSON seam (see reel transport below) stays viable as the technical fallback. Resolved 2026-07-18: deferred to distribution time — not a development constraint.
- **Tick identity.** Treat CS2 demo tick ≡ DV frame clock with a `TickOffset` config shim (default 0). Phase 0 runs the empirical spike on Windows; if a constant offset appears it's a settings fix, if it drifts we need a mapping table (unlikely; architecture unchanged either way). The spike gates *calibration*, not development.
- **Titles.** Cache the **rendered** highlight title at emission time; the tab shows the cached string. Title edits don't invalidate the cache (they're absent from the canonical hash preimage) — acceptable staleness, keeps the tab cache-only.
- **Store.** Highlights cache = a single `highlights.json` sidecar (schema §7.3), not rows in `library.json` (different invalidation lifecycle, bigger payloads). Revisit SQLite/per-demo sidecars only if real libraries show >~10 MB files.
- **Packages.** Bump central `Google.Protobuf` 3.27.3 → ≥3.29.5; add `Cs2VideoGenerator.Core` PackageVersion. Verified by Phase 0 build + full test suite (DV's own generated protos run on the newer runtime).
- **Speed.** While synced against a plugin without the `timescale-set` capability, DV's speed control is locked to 1.0 (tooltip explains); with the new CSVG release, speed becomes a mirrored DV→CS2 control. CS2-side user speed changes remain undetected in v1.
- **Sync scope v1.** Mirrored both ways: play/pause, seek. Demo identity CS2→DV is *display + offer only*: DV never auto-loads a demo CS2 chose; if the path matches a library entry it offers "Open in DV", otherwise it degrades with an explanation. DV→CS2 only: spectate-by-name, speed. Not mirrored: CS2→DV spectate.
- **Scanning.** Library-wide highlight scanning is opt-in (Settings toggle, default off). The open demo's analysis run is always harvested for free.
- **Reel transport.** Reel generation uses in-proc `ICsvgClient.CaptureCompilationAsync`. The compilation-JSON assembly code is kept serialization-clean so the CLI seam remains a drop-in fallback — the license decision's escape hatch.
- **CSVG versioning.** All CSVG changes ship as one capability release (target **v1.1.0**): proto delta §5.2, plugin §5.3, Core API §5.4, mock §5.5, packaging §5.6. `nbgv prepare-release` first (version.json currently `1.0.0-rc.{height}`, sorts below 1.0.0). Old-plugin/new-library degradation via capability tokens — DV must behave correctly against a v1.0 plugin.

---

## 4. Architecture overview

```
┌────────────────────────── DemoViewer.NET.Desktop (net10.0, desktop only) ──────────────────────────┐
│  ProjectRef → DemoViewer.NET.LiveSync (NEW)              ProjectRef → DemoViewer.NET (App)          │
│                                                                                                     │
│  DemoViewer.NET.LiveSync (NEW project)                    DemoViewer.NET (App project)              │
│  ┌───────────────────────────────────────────┐            ┌──────────────────────────────────────┐  │
│  │ CsvgWebHost (Kestrel HTTP/2 :50051,       │            │ ILiveSyncService (contract, no CSVG  │  │
│  │   private DI container, Cs2GameService)   │            │   types) + LiveSyncStatusViewModel   │  │
│  │ LiveSyncEngine : ILiveSyncService         │◀──────────▶│ AppHostHooks.LiveSyncFactory seam    │  │
│  │   DvPlaybackObserver / OutboundReconciler │  holds     │ PlaybackController, MainViewModel     │  │
│  │   PendingCommandLedger / Cs2EventPump     │  MainVM    │ Analysis tab "Verify in CS2"          │  │
│  │   DriftServo / TickMapper                 │            │ Highlights module + tab + reel dialog │  │
│  │ ReelJobService                            │            │ HighlightsCacheStore / ScanService    │  │
│  └───────────────┬───────────────────────────┘            │ HeavyJobGate                          │  │
│                  │ PackageRef                             └──────────────────────────────────────┘  │
│        Cs2VideoGenerator.Core v1.1 (NuGet)                DemoViewer.NET.Analysis:                  │
│                  │ gRPC bidi (plugin dials in)             HighlightFired emission (A1),           │
│        CS2 + CSVG plugin v1.1  /  mock_server              HighlightConfigFingerprint (A2)         │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Data flow summary: DV user intent → `DesiredState` → reconciler diff vs `BelievedCs2State` → coalesced commands → CSVG Core → plugin. CS2 engine truth → plugin polling (~120 Hz) → `TickUpdateEvent`/`DemoStateEvent` (new) → latest-value slot → 30 Hz UI pump → ledger match (echo suppression) or remote-apply to `PlaybackController` under `_applyingRemote`.

---

## 5. Workstream WS-A — CSVG capability release v1.1 (owner-controlled repo)

### 5.1 Principles
- Pure-additive proto: append oneof arms at next free numbers (`CsControlRequest`=10+, `CsProcessEvent`=4+), every new scalar field `optional` (presence = the entire old-vs-new detection mechanism; plain proto3 bools are indistinguishable from "not sent").
- Capability tokens advertised in `Cs2ProcessStatus.capabilities` (new `repeated string`, field 6); the library exposes them and degrades per-token — warn-never-error, matching the library's existing version-handshake philosophy.
- All new plugin stream writes go through the existing `WriteEvent` under `m_streamMutex`; `Stop()` remains `TryCancel()`-only.

### 5.2 Protocol delta (target contract — final field naming at implementation time)

```proto
// CsControlRequest: new arms + envelope correlation id
SetDemoTimescaleCommand set_demo_timescale = 10;  // float timescale (demo_timescale)
QueryDemoStateCommand   query_demo_state   = 11;  // empty; reply = DemoStateEvent snapshot
optional uint64 command_id = 100;                 // envelope-level, outside the oneof; absent = no ack requested

// SetDemoTickCommand
optional bool pause_after_seek = 2;               // deterministic post-seek state; absent = legacy

// SetSpectatorTargetCommand
optional uint64 steam_id64 = 2;                   // EXPERIMENTAL spec_lock_to_accountid path; name fallback

// CsProcessEvent: new arms
DemoStateEvent  demo_state  = 4;
CommandAckEvent command_ack = 5;

message DemoStateEvent {                          // low-rate, change-driven engine truth
  optional bool   is_playing_demo        = 1;     // ISource2EngineToClient::IsPlayingDemo (already used)
  optional bool   is_paused              = 2;     // IDemoFile::IsDemoPaused (slot mapped, unvalidated)
  optional int32  demo_tick              = 3;
  optional string demo_file_path         = 4;     // GetDemoFilePath (slot 043, unvalidated); empty = none
  optional float  demo_timescale         = 5;     // last-host-set value in v1.1 (no engine readback)
  optional string spectated_player_name  = 6;     // RESERVED — omitted until spectator RE lands
  ChangeOrigin    origin                 = 7;     // UNKNOWN / HOST_COMMAND / USER (suppression-window attribution)
  optional uint64 in_reply_to_command_id = 8;
}
message CommandAckEvent {
  uint64 command_id = 1; bool success = 2;
  optional string error_message = 3;
  optional int32 observed_demo_tick = 4;          // actual engine tick at completion (fixes target-echo lie)
}

// TickUpdateEvent (hot path — minimal)
optional bool is_paused = 2;                      // plugin already computes this; engine-truth once IsDemoPaused polls

// Cs2ProcessStatus
repeated string capabilities = 6;                 // "demo-state-events","command-ack","seek-ack","timescale-set",
                                                  // "demo-identity","engine-pause-detection","load-failure-detection",
                                                  // "spectate-by-steamid","user-demo-ui"
```

### 5.3 Plugin work items (C++), ordered low-risk-first for incremental landing

| # | Item | Risk | Notes / acceptance |
|---|---|---|---|
| A-P1 | Capability list constant + `command_id`/`CommandAckEvent` plumbing + serialize the already-computed `is_paused` on tick updates (echo-truth interim) | LOW | Mechanical; mock inherits free (shared client code) |
| A-P2 | Emit `DemoStateEvent` from the polling loop on observed changes: playing→stopped (demo end / user `stop`), stopped→playing; wire `ChangeOrigin` via a short suppression window after host-command execution | LOW | Today detection exists and deliberately no-ops (`Cs2PluginClient.cpp:739-759`) |
| A-P3 | `SetDemoTimescaleCommand` → `ExecuteClientCmd("demo_timescale %.3f")`; remember last-set value for `DemoStateEvent.demo_timescale` | LOW | No readback in v1.1 |
| A-P4 | Load validation: `std::filesystem::exists` → emit `DEMO_FILE_NOT_FOUND(-1)`; replace the blind 15 s sleep with an `IsPlayingDemo()` poll + timeout → `DEMO_FILE_UNPLAYABLE(-2)` | LOW/MED | Enum values + `LoadDemoAsync` handling already exist; kills the 2-min bad-path timeout AND the 15 s mock tax |
| A-P5 | Conditional demo UI: `demoui 0` runs per-load (`Cs2PluginClient.cpp:391`) and can be flag-gated, but `demo_ui_mode 0` runs once at plugin connect (`server_plugin.cpp:377`), BEFORE any command arrives — interactive mode must actively **re-enable** (`demo_ui_mode 1` + skip `demoui 0`) when the `LoadDemoCommand` carries the new interactive flag | LOW | Required for F1 — user interaction is currently designed out; token `"user-demo-ui"`. DV must SET the flag (§6.5) |
| A-P6 | Engine-truth pause: poll `IDemoFile::IsDemoPaused()` in the snapshot read (SEH-guarded); diff → `DemoStateEvent` pause flips with origin; upgrade `TickUpdateEvent.is_paused` to engine truth | MED | Unvalidated vtable slot — validate on current CS2 build first |
| A-P7 | Seek ack: on `SetDemoTickCommand` with `command_id`, stop pre-sending the target tick; schedule arrival detection (`tick within ±ε of target`, distinct logic for backward seeks), then `CommandAckEvent{observed_demo_tick}`; honor `pause_after_seek`; timeout → failure ack | MED | The subtle one — `m_tickActions` is one-action-per-tick `std::map`; needs its own keying |
| A-P8 | Demo identity: read `GetDemoFilePath()` (slot 043) in the polling snapshot (SEH-guarded, copy immediately); include in `DemoStateEvent` on load/start/stop/change | MED-HIGH | Unvalidated slot; string read concurrent with teardown — extend SEH guard; Linux has no guard today (accept risk or add signal guard) |
| A-P9 | `spec_lock_to_accountid` empirical validation; if it works on current CS2, implement `steam_id64` targeting + advertise `"spectate-by-steamid"`; else drop the token and keep the field reserved | HIGH (validation) | Windows session required |
| A-P10 | Plugin redial loop (reconnect to orchestrator with backoff) | MED | Removes "DV restart ⇒ CS2 relaunch"; keep out of v1.1 if it slips — everything else works without it |

Deferred beyond v1.1: spectator readback (`spectated_player_name` emission — fresh RE), timescale engine readback, configurable gRPC port, multi-connection policy.

### 5.4 Core (.NET) API additions

- `IReadOnlySet<string> PluginCapabilities { get; }` + `bool Supports(string)` — populated at handshake; all new APIs degrade or throw `NotSupportedException` per-method when the token is absent.
- `Task<SeekResult> SetDemoTickAsync(int demoTick, bool? pauseAfterSeek, bool waitForCompletion, CancellationToken)` — command_id allocation + pending-ack table; legacy fire-and-forget overload retained. `record SeekResult(bool Success, int ObservedDemoTick, string? Error)`.
- `Task SetDemoTimescaleAsync(float, CancellationToken)`; fix `PlayDemoTickRangeAsync`'s hardcoded 64 t/s timeout math to divide by effective timescale.
- `event Func<string, DemoState, Task>? DemoStateChanged`; `DemoState? LastDemoState { get; }`; `Task<DemoState> QueryDemoStateAsync(CancellationToken)`. `record DemoState(bool? IsPlayingDemo, bool? IsPaused, int? DemoTick, string? DemoFilePath, float? Timescale, string? SpectatedPlayerName, DemoStateOrigin Origin)`.
- `StartWatchSessionAsync(...)` convenience = watch-mode session (`initializeCapture:false`) + `SetTickUpdateFrequencyAsync(0)`.
- Optional (F3 macOS dry-run convenience): ship a public no-op `IVideoCaptureProvider` stub, or document the host-registered-stub pattern.

### 5.5 mock_server work (macOS F4 coverage)

- Free (shared client code): command_id/ack, capabilities, `is_paused` echo, seek-ack logic, stop events. Engine-truth pause polling (A-P6) is also free in mock — `MockDemoFile` already overrides `IsDemoPaused()` (`MockEngineInterface.h:55-58`).
- `MockEngineInterface`: implement `GetDemoFilePath()` (path already stored); handle `demo_timescale` (scale the tick-thread interval); fix `spec_player` parsing (quoted name, not `atoi` slot); optional `CSVG_MOCK_DEMO_TICKS` (natural demo end).
- **User-action injection channel** (the key F4 addition): stdin command reader on mock_server driving `MockEngineInterface` directly, bypassing gRPC — `user-pause`, `user-resume`, `user-seek <tick>`, `user-playdemo <path>`, `user-stop`, `user-timescale <v>`, `end-demo`. The production client's polling + origin attribution then sees them exactly as real user actions — this is the only way to integration-test F1's game→viewer direction on macOS.
- `CSVG_PLUGIN_FAST_TIMINGS=1` env override shrinking the 5 s READY and load-poll timings for tests (A-P4 already removes the 15 s sleep).

### 5.6 Packaging & release mechanics

- CI matrix leg: build + bundle **osx-arm64** mock_server in the NuGet (csproj globs already RID-agnostic; `build-native-plugins.yml` + release gate change). Until it ships, DV devs build locally (`cmake --preset release-unix`) + `ExternalMockServerPath`.
- Release: `nbgv prepare-release` (version.json is `1.0.0-rc.{height}`); tag-triggered workflow hard-fails on mismatch. Update the plugin-compatibility JSON if the CS2 build baseline moved.
- Docs: README protocol section + `docs/mock-cs2-mode.md` (injection channel) + CHANGELOG.

---

## 6. Workstream WS-B — DV live-sync engine (F1 + F2)

### 6.1 Project layout & wiring seam

- **New project `src/App/DemoViewer.NET.LiveSync/`** (net10.0): `FrameworkReference Microsoft.AspNetCore.App`, PackageReference `Cs2VideoGenerator.Core`, ProjectReference → App project. Referenced ONLY by `DemoViewer.NET.Desktop`. Nothing CSVG/ASP.NET may enter the App project (WASM poison — Browser references App directly).
- **App project** gets the contract: `ILiveSyncService` (+ `LiveSyncState`, `LiveSyncStateKind`, event args — no CSVG types), `LiveSyncStatusViewModel`, and the static seam `AppHostHooks.LiveSyncFactory : Func<MainViewModel, ILiveSyncService>?` set by `Desktop/Program.Main` before the Avalonia lifetime starts (insertion point exists before `StartWithClassicDesktopLifetime`, `Program.cs:37-38`); invoked in `App.OnFrameworkInitializationCompleted`'s desktop branch after `MainViewModel` exists (hook site `App.axaml.cs:66-96`); `DisposeAsync` registered on `ShutdownRequested`. Precedent note: `IWindowService` establishes *per-host impls behind an App-side interface*, but both its impls live in the App project and are lifetime-branch-selected — `AppHostHooks` is the **first Desktop→App static injection seam** (required because this impl lives in a project App cannot reference).
- Solution: add the project to `DemoViewer.NET.slnx`.

### 6.2 `CsvgWebHost`

- Private second DI container: `WebApplication.CreateSlimBuilder` + in-memory config from `AppSettings.LiveSync` → CSVG's `Cs2VideoGenerator` section (`MockMode`, `ExternalMockServerPath`, install-dir overrides; `GrpcPort` fixed 50051); `AddCs2VideoGeneratorCore()`; Kestrel `ListenLocalhost(50051, Http2)`; `AddGrpc()`; `MapGrpcService<Cs2GameService>()`. `ICsvgClient` resolved from `app.Services`.
- Lazily started on user enable, never at app start. `AddressInUseException` → `Error("port 50051 in use")`. CSVG logs bridged to the Output panel via a custom `ILoggerProvider`.
- Playback-only sessions: `StartSessionAsync(w, h, fullscreen, initializeCapture: false)` — leave `VideoCaptureProvider="OBS"` default untouched (options validation requires it).
- Guard against ambient config bleed (`DOTNET_`/`ASPNETCORE_` env, appsettings.json in app dir) — explicit empty configuration sources except our in-memory section (Phase 2 verifies at runtime).

### 6.3 `TickMapper`

- `cs2DemoTick(frameIndex) = max(0, frames[frameIndex].ServerTick) + TickOffset` (clamp the pre-game negative sentinel; `TickOffset` default 0, settings-overridable — the §3 tick-identity shim).
- `frameIndex(cs2DemoTick)` = binary search over `SemanticNavigator.TickBoundaryFrames` (first frame of each distinct tick) — NOT `PlaybackController.SeekToTick`'s linear scan.
- F2 event ticks: `GameEvent.GameTick` / `RuleChainEvent.Tick` are already frame-clock — use directly. Convert absolute `GameEvent.ServerTick` values (if ever used) via `− ParsedDemo.ServerStartTick`.

### 6.4 Sync model (control plane: DV is the single command authority; data plane while playing: CS2 is the clock master)

- `DesiredState { demoTick, playing, demoPath, spectatorName, speed }` ← DV local intent, from: `FrameNavigationViewModel.SelectedFrameChanged` (discrete seeks only), `PlaybackController.PropertyChanged(IsPlaying/Speed)`, `IModuleContext.DemoReset` + `DemoPath`, and (new surface) `Playback2DTabViewModel.FollowSlotChanged` → roster name. **Never derive intent from `Advanced`.**
- `BelievedCs2State` ← command echoes + tick stream + `DemoStateEvent` (v1.1) or inference (v1.0).
- `_applyingRemote` re-entrancy flag: every engine-driven `PlaybackController` mutation happens on the UI thread under the flag AND writes the same values into `DesiredState` — the observer sees no diff, no echo command. This is the loop breaker.
- `OutboundReconciler`: edge-triggered with a ~140 ms settle window; diffs desired vs believed; emits the **minimal** command set (a seek-while-playing becomes seek+pause matching DV's actual post-seek state — `SeekToFrame` stops the play loop first, emitting `IsPlaying=false` then position).
- `PendingCommandLedger` (echo suppression): PendingSeek (±32-tick confirm tolerance, provisional-confirm + 1 s grace against the target-tick pre-echo on v1.0; real acks on v1.1 `seek-ack`), PendingPlay/Pause (status-echo confirm, 5 s), PendingLoad (the client awaits internally), PendingRange (F2). Expiry → `Degraded`, adopt CS2-reported truth.
- Single-slot latest-wins seek pipeline (at most one in-flight `SetDemoTick`; new targets replace the slot) — the plugin executes commands serially and a load blocks its thread ~15 s (v1.0). Two v1.1 plugin facts the pump must respect (from A-P7/A-P8 implementation): (a) never interleave `CloseDemo`/`LoadDemo` with an in-flight acked seek — the plugin's pending-seek slot survives demo stop (backward reloads pass through stop) and a new demo playing past the old target within the 30 s deadline could ack spuriously; drain or supersede the seek first; (b) a same-path re-load completes its identity check immediately (basename match — instances are indistinguishable), so after a same-path `DemoReset` reload, confirm via tick-0 arrival, not load completion.
- Command pump: one background consumer of a `Channel<SyncCommand>`; ALL `ICsvgClient` calls off the UI thread. Demo-change rule: when `BelievedCs2State` already has a demo loaded, enqueue `CloseDemoAsync` BEFORE `LoadDemoAsync` — the v1.1 plugin's load-completion poll checks `IsPlayingDemo`, which is already true while a demo plays, so a direct re-load returns before the new demo is actually up (stale-true gap, discovered during A-P4 implementation; the durable fix is A-P8 demo identity).
- `DriftServo` (while both playing, 30 Hz): `err = mapToDvTick(cs2Tick) − currentTick`; `|err| ≤ 8` nothing; `≤ 128` speed servo `Speed = clamp(1 + err/256, 0.75, 1.5)` under `_applyingRemote` (avoids discrete seeks — those pause DV and trigger the heavy entity re-seek); `> 128` hard resync (seek + play). DV's play loop keeps running; CS2's ticks are a drift reference, not a position push.
- Inbound threading: `TickUpdated` handler = write `(tick, timestamp[, isPaused])` into a padded latest-value slot, return immediately (synchronous hot path; **must be exception-free** — the sync event path has no per-subscriber isolation, a throw skips co-subscribers). A ~30 Hz UI-thread `DispatcherTimer` (`Cs2EventPump`) drains: ledger, servo, watchdog, status. Async events → `Dispatcher.UIThread.Post`-and-return (they are awaited serially on the gRPC read loop — never await UI work inline). CsvgClient swallows async-subscriber exceptions per-subscriber — the engine routes its own failures into its state machine explicitly.
- Observer robustness: tolerate (a) duplicate `PropertyChanged(Speed)` from the controller's clamp re-entry (`PlaybackController.cs:170-183`), and (b) the end-of-demo auto-`Pause()` (`PlaybackController.cs:531-535`) — a legitimate engine-originated DV intent change (DV reached demo end → mirror pause to CS2), not a user-pause echo.
- No-demo state: sync enabled with no DV demo loaded ⇒ `ConnectedIdle`, no `LoadDemo` emitted (`DesiredState.demoPath = null` reconciles to nothing); a DV load transitions `ConnectedIdle → LoadingDemo`; CS2-side demo activity in that state is handled per §6.5. Transition covered by WI-15 state-machine tests.

### 6.5 CS2→DV direction

- **Enabling in-game control:** when the `user-demo-ui` capability is advertised and live sync is user-enabled, DV's LoadDemo path sets the new interactive-demo-UI flag (A-P5) — without it the plugin keeps hiding the demo UI and the whole CS2→DV direction cannot occur even on v1.1. On v1.0 (token absent) in-game user playback control is **impossible by design** (demo UI hidden) — the inference fallback below then mostly covers console-driven actions, and the UI copy says so.
- **v1.1 path (primary):** `DemoStateChanged` with `origin=USER` → remote-apply to DV (pause/resume/seek/demo-change/end). `TickUpdateEvent.is_paused` gives exact pause state per tick update.
- **v1.0 fallback (capability-gated inference, shippable but labeled):** tick-silence watchdog (750 ms) → "CS2 paused (inferred)"; tick jump >128 without pending seek → CS2-side seek → remote-apply; tick restart near 0 → `Degraded("CS2 demo state unknown")` + Re-sync button. Inference cannot distinguish pause / demo end / hang — the UI copy says so.
- Demo change CS2→DV (v1.1, `demo-identity` token): match `DemoStateEvent.demo_file_path` against the library; offer "Open in DV" (a §3 decision — never silent auto-load); unknown path → `Degraded` + explanation. Note: after a CS2-side user demo-change, `PlayDemoTickRangeAsync` is unusable until DV re-issues `LoadDemoAsync` — its precondition reads the echo-only `LastDemoPlaybackStatusChange` (`CsvgClient.cs:1195-1203`), which a user `playdemo` never populates.

### 6.6 Lifecycle, degradation, failure surfacing

- `LiveSyncState`: `Disconnected → HostStarting → LaunchingCs2 → Connecting → ConnectedIdle → LoadingDemo → Synced{Holding|Following|SeekPending} → Degraded(reason) → Error/Faulted(reason)`, plus `Suspended(reel render)` — entered when `ReelJobService` takes over the CS2 instance (ux-design.md §9); sync actions disabled for the duration, and after the reel finishes the state returns to `Disconnected`-with-Reconnect-prompt (never auto-relaunch CS2). Mapped from `CsvgSessionState` + engine ledger/watchdog.
- Capability degradation matrix (per token → which engine features turn off, which UI affordances lock): maintained as a table in code (`LiveSyncCapabilities`) and rendered in the status flyout ("plugin 1.0 — update CSVG for exact pause sync").
- Reconnect = `StopSessionAsync` (kills CS2, restores backups) + `StartSessionAsync` (~2 min) — button copy sets expectations; auto-retry OFF. From `Faulted` always `StopSessionAsync` first. Shutdown: `await DisposeAsync()` then Kestrel stop, on `ShutdownRequested`.
- **Crash recovery & uninstall:** a DV crash (no `ShutdownRequested`) leaves the CS2 install modified (patched `gameinfo.gi`, plugin files, orphaned backups — CSVG's session patches on start and restores on stop). On host start with live sync enabled, detect leftover CSVG backups/patched state and offer restore before any `StartSessionAsync` (CSVG's BackupManager/doctor surfaces exist; `csvg restore` is the CLI equivalent). Permanent-disable path restores the install and removes plugin files. Owned by WI-33.
- Demo path gating: require rooted+existing path before `LoadDemoAsync`; bad paths on v1.0 burn a 2-min timeout (pre-validation is the defense until A-P4 load-failure detection).

### 6.7 F2 — `VerifyMomentAsync`

`ILiveSyncService.VerifyMomentAsync(int frameClockTick, int preRollTicks = 192, int postRollTicks = 64, string? spectateName = null, CancellationToken ct)`:
1. Not synced for the current demo → surface the enable/launch/load prompt (same engine paths).
2. Suspend follower (ledger `VerificationPending` mode).
3. Optional `SetSpectatorTargetAsync(rawName)` (exact in-demo name from `PlayerRosterEntry`).
4. `PlayDemoTickRangeAsync(map(tick)−preRoll, map(tick)+postRoll, record:false)` — deterministic paused arrival via the auto-pause echo (works on v1.0; on v1.1 optionally acked seek + `pause_after_seek`). Clamp post-roll at demo end. The `record:false` path **never throws** — every failure (precondition, timeout, even caller cancellation) comes back as `DemoPlaybackResult.Failed` (`CsvgClient.cs:1170-1179`); branch on `result.Success` and track cancellation state separately. Precondition: a demo loaded *through this client this session* (echo-only `LastDemoPlaybackStatusChange` ∈ {DemoLoaded, Playing, Paused}) — always true in `Synced`.
5. Remote-apply DV playhead to the trigger frame; resume follower.

Analysis-tab hookup: "Verify in CS2" context command on rule-trigger rows/nodes — resolve the trigger's firing tick (frame clock; `FrameIndexOfMessage`/`OnFrameSeeked` seams exist) and the attributed player's raw name. Highlights tab reuses the same command per highlight row ("Verify live").

### 6.8 DV-side ancillary changes

- `Playback2DTabViewModel.FollowSlotChanged` event (view-layer `FollowSlot` currently has no observable) → DV→CS2 spectate.
- NavStrip speed-lock affordance (bound to `LiveSyncStatusViewModel`).
- `AppSettings.LiveSync` section (binder-safe defaults): `Enabled` (session-scoped), `MockMode`, `ExternalMockServerPath`, `TickOffset`, `Cs2RootInstallationDirectory?`, window size prefs.
- FeatureCatalog: `chrome.livesync` (+ per ux-design.md); runtime `OperatingSystem.IsBrowser()` guard on every entry point; feature ids are persisted keys — never rename.
- `MainViewModel` DI factory + ctor additions are explicit (nullable optional params, fail-open for tests).

---

## 7. Workstream WS-C — Highlights pipeline (F3)

### 7.1 A1 (rich emission — the keystone)

Emit a self-contained record at rising-edge time so the cache never needs snapshots (bare mode `CaptureSnapshots=false` is the only affordable scan mode; round attribution + title holes otherwise require `MessageSnapshots`):

```csharp
public sealed record HighlightFired(
    string RulesetId, string HighlightId,   // qualified — fixes the lost-qualifier problem
    int FrameIndex, int Tick,               // frame clock (== RuleChainEvent.Tick semantics)
    int PlayerSlot, string PlayerName,      // RAW in-demo name (spec_player currency)
    int RoundNumber,                        // live round_number node at emission
    string RenderedTitle);                  // Title template rendered at the firing instant
```

Mechanism: `BuildV2Highlight` already registers a rising-edge action per highlight (bumps `.count`); add a second collector action. Source split: `PlayerSlot`/`PlayerName`/title-hole inputs are already available in the `BuildV2Highlight` materialization closure (`RuleChainBuilder.RulesetsV2.cs:1091`, slot at `:416`); only `(frameIdx, tick)` need the new evaluator-provided action signature — an additive `Action<int,int>` arm alongside the existing plain-`Action` registrations, accepted by BOTH registration paths (`StateGraphEvaluator.cs:136-139` and `:1410-1419`). Small non-breaking evaluator change — `StateGraphEvaluator.cs` is not protected. Surface the collection on `AnalysisRun`/`EvaluationResult`. Join `SteamId64` from `ParsedDemo.Players[slot]` at cache-write time. Title holes resolve against live node values (bare ids via the template's local lookup; `{player.name}`, `{round.number}` specials) — semantics documented against `docs/rules-v2/` during implementation; A1 lands with unit tests on a reference demo comparing round attribution vs the snapshot-mode projector.

### 7.2 A2 (config fingerprint)

Standalone `HighlightConfigFingerprint` helper replaying the builder's canonical hashing without building graphs: per (tickRate, profile) composition, per ruleset in the builder's exact order — **non-compute/rate stats → rate stats → per-highlight Flag-descriptor hashes → deferred computes LAST** (`RuleChainBuilder.RulesetsV2.cs:470-547`; computes may reference a highlight's `<id>.count`, so hashing computes before highlights throws in `MapStatHashSource`). Highlights register FOUR spellings (bare id, `<id>.count`, `{rulesetId}.<id>`, `{rulesetId}.<id>.count` — `:528-531`) vs stats' two; preserve ruleset iteration order. Per-demo row fingerprint = SHA-256 over sorted `"{rulesetId}.{highlightId}=<hash>"`. **Golden test asserting helper ≡ builder hashes** (drift guard). Note: hashes are tickRate/profile-dependent — no global fingerprint; compute per row inputs (cheap: YAML load + Compose + hash, no parse).

### 7.3 Cache store (`HighlightsCacheStore`, `AppPaths.HighlightsCacheFile` → `<ConfigRoot>/highlights.json`)

Row: identity `(path, size, modifiedTicks, demoSha256)` (SHA-256 = `MatchChecksum` we hand CSVG — CSVG never reads it, any stable string is valid); demo facts for clip assembly without re-parse (`mapName`, `tickRate` (use the existing `ParsedDemo.TickRate` property — one canonical rounding site), `tickCount`, `serverStartTick` (diagnostic only), `profileName`, `players[{slot,name RAW,steamId64 as string,team}]`, `rounds[{number, startTickFrameClock}]` from `AllGameEvents` round_start **GameTick** values); invalidation (`configFingerprint`, per-highlight hash map for partial-staleness display, `scanState`); `events[]` = `HighlightFired` verbatim + `steamId64`. Additive-nullable evolution (the Library `ScoreComputed` backfill pattern); corrupt-file-tolerant load (`DemoLibraryService` load mechanics). Writes: **atomic temp-file + `File.Replace` — deliberately NEW behavior**; `DemoLibraryService.Save` is best-effort-only (`File.WriteAllText`, non-atomic) and is not the precedent here. Player names stored RAW; `DisplayText.Sanitize` at display time only.

### 7.4 `HeavyJobGate`

Machine-wide ONE-heavy-parse invariant, made explicit: `SemaphoreSlim(1,1)` + interactive-preemption flag, registered in `App.BuildServices`. Consumers: Library tier-2 indexer (replaces its private cap), the highlight scanner, and (recommended, separable work item) the shell's own `LoadDemoFromBytesAsync` parse — closing today's uncoordinated-overlap gap. Background workers yield between demos when the interactive flag is up. Reel generation raises the flag for its whole session (CS2+OBS on 16 GB must not fight a background parse). Policy for the converse case: an interactive shell demo-load requested **while a reel session is active** is refused with a clear status message ("highlight reel in progress — try again when it finishes") rather than queued — silently deferring a foreground click is worse than telling the user why. Covered by the gate tests (§9).

### 7.5 `HighlightScanService`

- **Piggyback tier:** when Library tier-2 holds a `ParsedDemo` and the highlights row is missing/stale → run `DemoAnalysis.Build` + bare `Evaluate` before releasing the demo (~+3.5 s on the already-serialized job). Analysis failure marks only the highlights row Failed, not the library entry.
- **Backfill queue:** newest-first single-consumer queue for stale/missing rows (rules changed, pre-existing libraries): read bytes → parse → bare eval → write row → drop before next (never two `ParsedDemo`s in flight). Save every N completions; single swapped CTS per rescan; `post`-delegate UI marshalling; skip `._*` AppleDouble sidecars.
- **Re-fingerprint triggers:** app start, Highlights tab activation, Authoring Workbench rule save, manual Rescan. Auto-scan is opt-in (§3).
- **Open-demo harvest:** subscribe `AnalysisViewModel.EvaluationCompleted` — the currently-open demo's full-snapshot run refreshes its row for free.

### 7.6 Highlights tab (module `Modules/Highlights/`, TabId `highlights.browser`)

`IWorkspaceModule` with `ViewModelFactory` (never `DataContext` — lifecycle skip), registered in `App.BuildRegistry`; FeatureCatalog descriptor + `TabFeatureIds` entry (gating per ux-design.md). Master-detail: filterable demo list (players multi-select w/ counts keyed steamId64, highlight-type chips incl. historical ids, map filter, free text; Library card virtualization + radar thumbs) → details pane (per-player groups → highlight rows: rendered title, round, tick, estimated clip window (pure clip-window math, WI-28 — lands with this phase), selection checkbox, "Verify live" — shipped disabled/hidden until WI-21 (`VerifyMomentAsync`, Phase 4) exists). Staleness badge + scanState chip per row; queue length in StatusStrip. Double-click → `LoadDemoFromPathAsync` delegate (Library ctor-delegate pattern). Live updates via store `Changed` event. Session tab-restore is positional — accept the shift or fix in the same change (noted in ux-design.md).

### 7.7 Highlight → clip mapping (pure functions, unit-tested)

Compute the whole window in the **frame clock** (all clamps included), then apply `TickOffset` once at emission — mixing spaces skews clamps whenever `TickOffset ≠ 0` (the very case the shim exists for):
```
startFrame = max(0, optionalRoundStartFrameClock, event.Tick − leadInSeconds  × tickRate)
endFrame   = min(tickCount,                       event.Tick + leadOutSeconds × tickRate)
StartTick  = startFrame + TickOffset;   EndTick = endFrame + TickOffset       // CS2 demo-tick space
```
Defaults lead-in 15 s / lead-out 5 s (rising edges fire at the END of the action), per-type overrides in settings (`AppSettings.Highlights`). Coalesce overlapping windows per (player, round). Sort clips by (demo, StartTick) ascending. `Cs2CompilationClip` fields: `PlayerSteamId` = cached steamId64; `MatchChecksum` = demoSha256; `DemoFilePath` = row path (pre-flight `File.Exists` — CSVG `Validate()` requires it); `PlayerNameToSpectate` = RAW cached name; when the connected plugin advertises `spectate-by-steamid`, ALSO pass the cached steamId64 through the new field (DV-side consumption of WI-07 — without this, mid-match renames stay broken even after CSVG ships the fix); `ClipOptions` from dialog. Table tests must include a `TickOffset ≠ 0` case and a mid-match (`ServerStartTick ≠ 0`) demo.

### 7.8 Reel dialog + `ReelJobService`

- Dialog via `IWindowService.ShowHighlightReelDialog(...)` (FirstRunWizard modal precedent; both window-service impls touched). Contents/UX per ux-design.md: clip list w/ visible coalescing, padding, options flags (`Default`/`NoHudDefault` presets), output (dir/base name/container/fps/concat toggle), encoding (CRF ⊕ bitrate mutually exclusive — UI-enforced), audio toggle, inline pre-flight validation (run `Cs2Compilation.Validate()` locally).
- `ReelJobService` (LiveSync project): ensure session with `initializeCapture:true`; `CaptureCompilationAsync` on a background task; the dialog **hands off to a background job** surfaced as a second StatusStrip chip (Reel chip) with flyout progress — no multi-minute modal (ux-design.md §8.7-8.8). Progress = `CompilationClipStarted/Completed` (k of N) + `CaptureProgressUpdated` (synchronous, fires on the OBS/capture thread — post-only handler); fail-fast surfacing with per-clip status + "Retry remaining" (new compilation from unfinished clips; cross-run concat is a CSVG follow-up); `Faulted` → `StopSessionAsync` reset; `HeavyJobGate` interactive flag held; results row with output path.
- **F1↔F3b single-CS2 interlock (ux-design.md §9):** live sync sessions are `initializeCapture:false`, reels need `true` — they cannot coexist. Reel start while `Synced` shows an informed confirm, then `StopSessionAsync` (sync CS2 killed, install restored) → capture session → reel → `StopSessionAsync`; the sync chip shows `Suspended (reel render)` throughout and ends at Reconnect-prompt (no auto-relaunch). Mutual exclusion: sync Enable/Reconnect disabled while a reel runs; one reel job at a time.
- Platform gating: real generation iff `OperatingSystem.IsWindows() || IsLinux()` (+ OBS available). macOS primary action = "Dry run (mock)": mock session, walk the clip plan with `SetDemoOptions → SetSpectatorTarget → PlayDemoTickRange(record:false)` — validates command plumbing + tick math; clearly labeled a developer/testing feature. `PlayDemoTickRangeAsync` gets explicit timeouts derived from the row's real tickRate (the default hardcodes 64 t/s), and its `record:false` path never throws — branch on `result.Success` (cancellation folds into failure; track CT state separately). Dry-run fidelity depends on the v1.1 mock (WI-09): the v1.0 mock mis-parses `spec_player` (atoi slot) and pays 15 s per demo load — Phase 6 therefore depends on Phase 1's mock work, or the gate is scoped to "command plumbing only".

---

## 8. UI/UX specification

Owned by `docs/csvg-integration/ux-design.md` (delivered 2026-07-18; WI-00 complete). Two shared patterns were also promoted into `docs/ui/design-system.md`: a `StatusChip` + StatusStrip-chip-region component (used by both the Live Sync chip and the Reel job chip) and the app's first master-detail split layout (Highlights). Key decisions the implementation must follow:

- **Feature gates:** `chrome.livesync` (Chrome scope, `Defaults(consumer:false, power:false, dev:true)` — deliberately off for power users until the CSVG v1.1 exact-state items land; the gate shim additionally ANDs `!OperatingSystem.IsBrowser()`), and `tab.highlights` (Tab scope, default-visible for all categories) + the `"highlights.browser" → "tab.highlights"` `TabFeatureIds` entry. Reel generation is deliberately **ungated** (marquee consumer feature; the dialog + platform check + interlock are the guards).
- **Two-consent enable flow** for non-developers: Settings → Live Sync → "Enable Live Sync" toggle (flips the gate override; the beta/install-modifying opt-in) → chip appears → chip flyout → "Enable Live Sync…" informed confirm (the launch-CS2-now consent). No toolbar button; the chip is the single home.
- **Live Sync visual language:** status-strip dot+label chip + full-state flyout (states, plugin/game versions, capability-degradation notes, Reconnect/Re-sync). Inferred states get a distinct hollow-ring `(inferred)` treatment, separate from solid-caution `Degraded` — two honesty levels read differently. Zero new theme tokens; all pairings WCAG-checked across the four built-in themes (labels stay neutral `TextMid`; state is word-carried, dots are redundant cues).
- **F2 affordances:** context menu on pointer-RELEASE for analysis graph nodes (matches the graph idiom and the known ContextMenu gotcha) + inline "Verify live" buttons on Highlights rows; two-level gating (feature present/absent vs live-session enabled/disabled-with-prompt); SeekPending → arrived feedback in the chip/flyout.
- **Highlights tab:** master-detail split (list left, details right; collapses to single column with Back below ~900 px), Library card/filter reuse, staleness badges, scanState chips; background scan is opt-in for ALL categories (not a category default). WASM: registered-but-degraded to open-demo-only (§1 non-goals).
- **Reel UX:** modal config dialog (validation inline) that hands off to a **background job + Reel status chip** (no long-lived modal; there is no toast system); the F1↔F3b interlock per §7.8 with the `Suspended (reel render)` sync-chip state; macOS primary action is the developer-labelled "Dry run (mock)".
- **Settings:** Live Sync section (enable, mock mode [dev-labelled], CS2 install path, collapsed Advanced with force-incompatible-plugin) and Highlights section (auto-scan opt-in, clip padding defaults, reel output defaults) — both desktop-only, suppressed on WASM.

The spec's §13 carries the same open decisions as this plan (the GPL posture, the power-default flip trigger after CSVG v1.1, cross-run concat, spectate-name fragility) — reconciled, no contradictions.

---

## 9. Testing strategy (F4)

**DV unit (TUnit, fast, no processes):**
- `TickMapper` (sentinel clamp, boundary binary search, `TickOffset`), clip-window math + coalescing (table tests incl. mid-match `ServerStartTick ≠ 0` demos proving no accidental subtraction), `PendingCommandLedger` (provisional confirm, grace consumption, expiry), `OutboundReconciler` (settle-window coalescing, seek-while-playing minimal command set), state-machine transitions, capability degradation matrix.
- **`FakeCsvgClient : ICsvgClient` test double** — the only place inference/suppression edges are testable (mock_server cannot originate user actions until the injection channel ships): scripted event sequences for user-pause silence, seek jumps, target-tick pre-echo, anomalous statuses, session drops, Faulted-reset ordering.
- Highlights: A2 fingerprint golden test (helper ≡ builder), cache store round-trip/corrupt-file/backfill, scanner queue semantics under `HeavyJobGate`, A1 round-attribution parity vs snapshot projector on a reference demo.

**DV ↔ mock_server integration (TUnit, serialized) — new project `src/Testing/DemoViewer.NET.LiveSync.Tests/` (WI-32; named so the 16 GB batched-suite policy can account for it — it is NOT part of the App suite batching):**
- Port 50051 is exclusive: `[NotInParallel]` (shared constraint key) + skip-if-port-busy via `SkipTestException`. Budget ~25 s/session on CSVG v1.0 timing (5 s READY + 15 s load); re-budget after A-P4/fast-timings.
- Coverage: Kestrel hosting inside the test host, session lifecycle (incl. Faulted→Stop→Start), LoadDemo, seek→confirm→no-loop, frequency-0 tick following, servo engagement, demo-change (DV→CS2), F2 range playback arrival; with v1.1 mock: injection-channel-driven user pause/seek/demo-end mirroring, capability handshake, ack paths.
- macOS: local mock build + `ExternalMockServerPath` until the osx-arm64 NuGet leg ships; restore the executable bit on CI-artifact binaries.

**Headless UI (Avalonia.Headless + Skia, App.Tests, via `scripts/test-app-suite.sh`):** `LiveSyncStatusViewModel` states, Highlights tab filtering/master-detail, reel dialog validation states.

**CSVG-side (xUnit v3, in the CSVG repo):** proto round-trips, ack correlation, capability advertisement, mock injection channel, load-failure emission, timescale; extend `MockModeEndToEndTests`.

**Windows validation passes (owner-run, real CS2):**
1. **Phase 0 tick-identity spike:** load one demo in both apps; seek CS2 to the frame-clock tick of a known kill (`GameEvent.GameTick`); eyeball the killfeed; log `(dvTick − cs2Tick)` over 60 s of synced playback → set `TickOffset` if constant. Include one truncated demo (exercises the `TickCount` highest-observed-tick fallback).
2. `spec_lock_to_accountid` empirical check (A-P9 gate).
3. `demo_gototick` post-seek pause semantics; `playdemo` with absolute out-of-gamedir paths; seek-past-end behavior.
4. **Vtable-slot sanity for the new engine reads:** call `IDemoFile::IsDemoPaused` (win slot 012) and `GetDemoFilePath` (slot 043) on the current CS2 build and record whether returns are sane (gates A-P6/A-P8; today both are declared-but-never-called).
5. Real reel E2E: 3-clip compilation from Highlights tab → mp4 out; OBS availability/failure paths.
6. v1.0-plugin degradation run (old plugin + new DV): inference labeling, speed lock, no crashes.

---

## 10. Phasing

| Phase | Content | Depends on | Exit gate |
|---|---|---|---|
| **0 — Spikes & prereqs** | Done 2026-07-18 (except owner items) — UX design spec (WI-00, done); package bump + `Cs2VideoGenerator.Core` reference compiles in a scratch desktop-only project + full DV test suite green; macOS mock build + `ExternalMockServerPath` smoke (hosting + session + load); Windows spike checklist §9 items 1–4 (owner — outstanding); GPL posture noted (§3) | — | Facts confirmed or plan amended |
| **1 — CSVG v1.1** | Done 2026-07-18 (A-P9 deferred pending owner validation; WI-10 CI leg deferred, local pack rc.26 in DV feed) — §5 in full: proto delta, plugin A-P1…A-P8 (A-P10 redial stretch not taken), Core API, mock work incl. injection channel. 293/293 Core + 113/113 CLI green | 0 — spike item 2 gates A-P9; spike item 4 gates A-P6/A-P8 (or validate in-phase as their first step) | CSVG tests green; mock exercises every new message; release published |
| **2 — DV sync foundation (one-way)** | Done 2026-07-18 (code side) — §6.1–6.3 + §6.4 **outbound only** (observer, reconciler, ledger, command pump; inbound limited to status/LastTick display + ledger echoes) + §6.6 state machine (v1.0-empty capability path hardcoded; consent flow; crash-recovery WI-33) + §6.8 settings/gating. DV→CS2 play/pause/seek/load. Built against the v1.1 rc.26 package but exercising the v1.0 command/echo path as phased | 0 | Mock integration suite (WI-32) green (23/23 incl. engine↔mock E2E); manual Windows sanity — owner-run outstanding |
| **3 — Bi-directional** | Code done 2026-07-18 (needs CSVG rc.27 — the `LoadDemoAsync` interactiveDemoUi overload) — §6.4 **inbound** (`Cs2EventPump`, `DriftServo`, frequency-0 following) + §6.5 (`DemoStateChanged` mirroring, origin attribution, demo-identity + "Open in DV", interactive-demo-UI request, v1.0 inference fallback) + `LiveSyncCapabilities` degradation matrix (consumes the v1.1 Core API; acked seeks + engine-truth pause) + speed mirroring/lock + `FollowSlotChanged` spectate (name-only pending A-P9). Injection-channel exit gate green — real mock user actions → USER-origin events through DV's stack, mapped via the pure `InboundLogic.Decide`; surfaced 2 real fixes: LoadDemoAsync completes PAUSED AT TICK 0, and user seeks emit NO DemoStateEvent — tick-stream jumps are the seek signal on every version, so jump detection runs on v1.1 too. Outstanding: Phase 3 UI polish (2D HUD indicator §5.3, capability note + live position in flyout); Windows validation pass 6 (v1.0 degradation, owner) | 1, 2 | Injection-channel tests green; Windows validation pass 6 (v1.0 degradation) |
| **4 — F2 verify-in-CS2** | Code done 2026-07-18 — §6.7: `VerifyMomentAsync` (engine verification mode: reconciler + inbound pump stand down while the range playback owns CS2; chip reads Seeking…; remote-apply to trigger + realign on completion), Analysis-tab "Verify in CS2" graph context-menu command (fire-precise tick resolution, two-level gating per ux §6.2), roster-name plumbing via the graph filter's selected player. Highlights-tab "Verify live" affordance lands with the tab itself (Phase 5). | 2 (3 optional) | Seek-to-trigger works on Windows (owner-run outstanding); mock test for range arrival green (VerificationRunnerMockTests) |
| **5 — Highlights data + tab** | Done 2026-07-18 — §7.1–7.6 + WI-28 clip-window math: A1/A2 (landed earlier), cache store (atomic writes), HeavyJobGate (+ Library tier-2 + shell-load rewiring), scanner (piggyback/backfill/harvest/triggers), tab UI with live "Verify live" (Phase 4 landed first). 20+11 new tests green | 0 (+4 for the Verify-live affordance) | Golden + parity tests green; tab browses a real library (headless-verified; live-library pass = owner) |
| **6 — Reel generation** | WI-30 done (ReelJobService: interlock via SuspendForReel, gate reel session, real capture path + validation + per-clip progress, macOS dry-run walk w/ tickRate-derived timeouts, retry-remaining; terminal status only after teardown). Dry-run mock gate green (3-clip walk, per-clip outcomes, interactive-load refusal). WI-29 done (dialog w/ visible coalescing + inline pre-flight + §9 interlock confirm + §8.9 platform primary action; Reel chip full lifecycle; resolved fork: Generate BLOCKS on a missing demo rather than silently dropping a selected highlight). Phase 6 code-complete | 2 (session infra), 5, 1 (WI-09 mock fixes + A-P4 load timing — else the dry-run gate is scoped to "command plumbing only") | macOS dry-run E2E green; Windows real reel produced (owner-run outstanding) |
| **7 — Hardening & docs** | Failure-mode sweep, perf pass (UI-thread flooding, servo thresholds), settings polish, docs + KNOWN-ISSUES updates | all | Full suites green on both repos |

Parallelism notes: WS-C (highlights, Phases 5–6 minus reel execution) is independent of WS-A/WS-B and can proceed concurrently. The builds are the real constraint: on a 16 GB machine, parallel heavy builds exhaust memory — keep them sequential, and rebuild dependent tools after feature work (stale-binary trap).

---

## 11. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | Tick-unit identity unproven | Phase 0 spike; `TickOffset` shim; worst case a mapping table — architecture unchanged |
| 2 | CS2 updates break plugin vtables (historical precedent) | Surface as "plugin not responding — check for CSVG update"; DV fully functional standalone; new engine reads (A-P6/A-P8) increase the churn surface knowingly |
| 3 | GPL-3.0 CSVG Core linked in-proc | Private use now; CLI seam kept viable; owner decision before distribution |
| 4 | `Google.Protobuf` central bump ripples into DV's own proto runtime | Phase 0 full-suite verification |
| 5 | Kestrel-in-Avalonia surprises (ambient config, firewall prompts, AddressInUse) | Slim builder w/ explicit config; first-class port-conflict error state; Phase 2 runtime check |
| 6 | Inference fragility on v1.0 plugin (pause ≈ end ≈ hang) | v1.1 is the primary target; inference is capability-gated and honestly labeled |
| 7 | Seek storms degrade CS2 (serial plugin command thread) | Latest-wins single-slot pipeline + settle window (load-bearing; regression-tested) |
| 8 | Hard resyncs feel broken in DV (heavy entity re-seek) | Speed-servo-first design; hard seek only >128-tick divergence |
| 9 | UI-thread flooding at 64 ticks/s | Latest-value slot + 30 Hz pump; synchronous handlers do nothing else |
| 10 | DV restart forces CS2 relaunch (no plugin redial) | A-P10 stretch; UX copy sets expectations |
| 11 | Highlight scan competes with interactive loads | `HeavyJobGate` + interactive preemption + opt-in scanning |
| 12 | Exact-name spectating breaks on mid-match renames | Known v1 limitation; CSVG `spectate-by-steamid` (A-P9) is the fix pending validation |
| 13 | `highlights.json` growth on big libraries | Additive schema + the §3 store-decision revisit threshold (~10 MB) |
| 14 | DV crash (no `ShutdownRequested`) leaves the CS2 install modified (patched gameinfo.gi, plugin files, orphaned backups) | WI-33: leftover-backup detection + offered restore on next start; permanent-disable uninstall path; CSVG `restore`/`doctor` as the manual fallback |

---

## 12. Work-item index

| ID | Phase | Layer | Item | Size |
|---|---|---|---|---|
| WI-00 | 0 | UX | `docs/csvg-integration/ux-design.md` — predecessor of WI-15, WI-27, WI-29 — done 2026-07-18 (incl. design-system.md StatusChip + master-detail additions) | M |
| WI-01 | 0 | DV build | Central `Google.Protobuf` ≥3.29.5 + `Cs2VideoGenerator.Core` PackageVersion + full suite | S |
| WI-02 | 0 | dev env | macOS mock_server local build + `ExternalMockServerPath` smoke host | S |
| WI-03 | 0 | validation | Windows spike checklist §9 items 1–4 (owner-run; item 4 = IsDemoPaused/GetDemoFilePath vtable sanity) | S |
| WI-04 | 1 | CSVG proto | §5.2 delta | S |
| WI-05 | 1 | CSVG plugin | A-P1…A-P5 (low-risk set) | M |
| WI-06 | 1 | CSVG plugin | A-P6 pause truth, A-P7 seek ack, A-P8 demo identity | L |
| WI-07 | 1 | CSVG plugin | A-P9 steamid spectate validation (+impl if green) | M |
| WI-08 | 1 | CSVG Core | §5.4 API surface + ack table + capability set | M |
| WI-09 | 1 | CSVG mock | §5.5 incl. injection channel + fast timings | M |
| WI-10 | 1 | CSVG CI | osx-arm64 mock leg + release (`nbgv prepare-release`) | S |
| WI-11 | 2 | DV | LiveSync project + `ILiveSyncService`/`AppHostHooks` seam + slnx | S |
| WI-12 | 2 | DV | `CsvgWebHost` + options + log bridge | M |
| WI-13 | 2 | DV | `TickMapper` + tests | S |
| WI-14 | 2 | DV | Observer/Reconciler/Ledger/CommandPump + FakeCsvgClient tests | L |
| WI-15 | 2 | DV | State machine (incl. ConnectedIdle/no-demo transitions) + first-run informed-consent enable flow + `LiveSyncStatusViewModel` + StatusStrip chip/flyout (per ux-design.md) | M |
| WI-16 | 2 | DV | Settings section + `chrome.livesync` gating + WASM/platform guards | S |
| WI-17 | 3 | DV | `Cs2EventPump` + `DriftServo` + CS2→DV mirroring (v1.1, incl. setting the interactive-demo-UI flag when `user-demo-ui` advertised) + inference fallback (v1.0) + `LiveSyncCapabilities` matrix | L |
| WI-18 | 3 | DV | Demo-identity handling + "Open in DV" + Re-sync | M |
| WI-19 | 3 | DV | Speed mirroring (v1.1) + NavStrip speed lock (v1.0) | S |
| WI-20 | 3 | DV | `FollowSlotChanged` + DV→CS2 spectate (steamId64 path when `spectate-by-steamid` advertised, name fallback) | S |
| WI-21 | 4 | DV | `VerifyMomentAsync` + Analysis tab "Verify in CS2" | M |
| WI-22 | 5 | Analysis | A1 rich emission + evaluator action-context change + parity tests | M |
| WI-23 | 5 | Analysis | A2 `HighlightConfigFingerprint` + golden test | M |
| WI-24 | 5 | DV | `HighlightsCacheStore` + `AppPaths` entry | M |
| WI-25 | 5 | DV | `HeavyJobGate` + Library tier-2 rewiring (+ shell load coordination) | M |
| WI-26 | 5 | DV | `HighlightScanService` (piggyback + backfill + triggers + harvest) | L |
| WI-27 | 5 | DV | Highlights module/tab/VMs/views + gating + `AppSettings.Highlights` section (auto-scan opt-in, per-type clip-lead overrides, reel output defaults) + Settings-tab rows (per ux-design.md) | L |
| WI-28 | 5 | DV | Clip mapping + coalescing (pure functions; frame-clock windows, `TickOffset` at emission) + tests incl. `TickOffset≠0` and mid-match demos | S |
| WI-29 | 6 | DV | Reel dialog + `IWindowService` method (per ux-design.md) | M |
| WI-30 | 6 | DV | `ReelJobService` + progress + retry-remaining + dry-run gating + reel-vs-interactive-load refusal policy | M |
| WI-31 | 7 | both | Hardening, docs, KNOWN-ISSUES, perf sweep | M |
| WI-32 | 2 | DV tests | New `src/Testing/DemoViewer.NET.LiveSync.Tests/` DV↔mock integration project (TUnit, `[NotInParallel]` shared port key, skip-if-port-busy); extended in Phase 3 with injection-channel cases | M |
| WI-33 | 2 | DV | Crash recovery + uninstall: leftover CSVG backup/patched-install detection on start with offered restore; permanent-disable restore path | S |
