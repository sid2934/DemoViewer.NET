namespace DemoViewer.NET.Configuration;

/// <summary>
///     The <em>preference</em> sections of the app's single consolidated per-user config file
///     (<c>settings.json</c>) — the type <c>IOptionsMonitor&lt;AppSettings&gt;</c> binds. Every property
///     carries a safe, non-null default so a partial, empty, or entirely missing file binds without a
///     null-deref: the configuration binder starts from <c>new AppSettings()</c> and layers file/env values
///     on top, so any section the file omits keeps the default constructed here.
///     <para>
///         <b>Consolidation.</b> The same file ALSO carries the UI session-restore snapshot and the
///         recents list (the former <c>session.json</c> / <c>recent-files.json</c>), but those are
///         deliberately NOT modeled here: they are records with required complex constructor params, which
///         the configuration binder cannot construct when the section is null, so binding <c>AppSettings</c>
///         must not have to touch them. <see cref="SettingsService" /> is the single serializer of the file
///         and preserves those extra sections through a JSON-node merge (so a preference write never clobbers
///         them), reading/writing them via System.Text.Json — never through the config binder or this type.
///     </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>Demo-library configuration (watched folders, …).</summary>
    public LibrarySettings Library { get; set; } = new();

    /// <summary>
    ///     Which feature tier the UI presents. Defaults to <see cref="UserCategory.PowerUser" /> — the
    ///     skip-the-wizard fallback when no first-run choice has been persisted yet.
    /// </summary>
    public UserCategory UserCategory { get; set; } = UserCategory.PowerUser;

    /// <summary>Feature-flag overrides and developer toggles read by the feature-gating layer.</summary>
    public FeatureFlags Features { get; set; } = new();

    /// <summary>
    ///     Active UI theme — a <see cref="Theming.Theme.Id" /> from the central <see cref="Theming.ThemeRegistry" />
    ///     (built-in <c>"dark"</c> / <c>"light"</c> / <c>"system"</c> / <c>"high-contrast"</c> / <c>"egirl"</c>, or a
    ///     user drop-in's id). Resolved case-insensitively by <c>App.WireTheme</c>, so legacy capitalized values
    ///     ("Dark"/"Light"/"System") from before the central theme system still map correctly.
    /// </summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    ///     True once the user has completed (or skipped) the first-run setup wizard. Drives
    ///     <see cref="SettingsService.NeedsFirstRun" />: the wizard shows until this is set, so it is
    ///     independent of whether <c>settings.json</c> exists — the demo-library folder migration can
    ///     create the file as a side effect without wrongly marking setup as done (an upgrading user has
    ///     still never chosen a category, so they should see the wizard). Only the wizard's Finish/Skip
    ///     sets this true.
    /// </summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>Live CS2 sync (CSVG) configuration — desktop-only feature, section always binder-safe.</summary>
    public LiveSyncSettings LiveSync { get; set; } = new();

    /// <summary>Highlights pipeline configuration — desktop-only, binder-safe.</summary>
    public HighlightsSettings Highlights { get; set; } = new();

    /// <summary>Global demo-processing queue configuration, binder-safe.</summary>
    public ProcessingQueueSettings ProcessingQueue { get; set; } = new();

    /// <summary>Unified diagnostics-logging configuration (in-app log window caps + rolling file sink).</summary>
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Idle-mode configuration (auto-close the open demo after inactivity to conserve RAM).</summary>
    public IdleSettings Idle { get; set; } = new();

    /// <summary>
    ///     The app version (x.y.z) whose release notes the user has been shown — the post-update
    ///     "What's new" gate. Null until a launch records it. Compared against the running version at
    ///     startup; a mismatch on an already-set-up install opens the What's New window once, and the
    ///     value is advanced BEFORE the window shows so a crash cannot re-show it in a loop. Not a
    ///     user-edited preference, but it lives here (not a non-reactive section) because it is a
    ///     single scalar with none of the constructor-shape problems that exiled Session/Recents.
    /// </summary>
    public string? LastSeenVersion { get; set; }
}

/// <summary>
///     Configuration for Idle Mode — after a configurable span with no user interaction (and no active
///     playback), the app captures where to resume, closes the open demo, and drops resource usage via the
///     same deterministic-close path the "Close Demo" button uses. Desktop-only; a no-op on WASM (no real
///     memory pressure there, and the global input hook / demo-close semantics differ).
///     <para>
///         The wait is a <see cref="TimeSpan" /> (serialized as <c>"00:15:00"</c>) rather than a whole-minute
///         count so the exact duration is user-controllable. "Tick" terminology is deliberately avoided here:
///         in this codebase a tick is a CS2/demo discrete-time unit, never a wall-clock idle measure — idle
///         timing is plain <see cref="DateTime" /> wall-clock.
///     </para>
/// </summary>
public sealed class IdleSettings
{
    /// <summary>
    ///     Master switch. Default ON. When off, the idle countdown never fires and the app never
    ///     auto-closes a demo — but the enable state is read live, so toggling it takes effect at once.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     How long with NO user interaction (and no active playback) before the app enters idle mode.
    ///     Serialized as an <c>"hh:mm:ss"</c> <see cref="TimeSpan" /> for exact control. Default 15 minutes.
    ///     A non-positive value disables the countdown (treated like <see cref="Enabled" /> off).
    /// </summary>
    public TimeSpan IdleTimeoutWait { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     When true (default), background demo processing continues while the app is idle — only the
    ///     FOREGROUND open demo is closed. When false, the queue is transiently paused on entering idle
    ///     and resumed on leaving it, so an idle machine does no background parsing at all.
    /// </summary>
    public bool KeepBackgroundProcessing { get; set; } = true;
}

/// <summary>
///     Configuration for the unified diagnostics-logging pillar — the first-party internal
///     <c>ILogger</c> stream (coarse load/analysis lifecycle + warnings/errors) that shares the
///     Diagnostics tab's log surface with the CSVG host logs, plus the optional rolling file sink.
///     <para>
///         Both the in-app window and the file are <b>bounded</b> by the caps here — the design
///         invariant is that no diagnostics buffer is ever unbounded (a past leak). The coarse
///         internal logs are low-rate, so the pillar defaults ON at
///         <see cref="LiveSyncLogLevel.Information" />: the standing cost is negligible and the
///         realtime visibility helps both developer iteration and end-user issue reports.
///     </para>
/// </summary>
public sealed class DiagnosticsSettings
{
    /// <summary>
    ///     Master switch for the first-party internal logging pillar (feeds the Diagnostics tab's
    ///     log window and — when <see cref="WriteLogFile" /> — the rolling file). Default ON. Off
    ///     leaves the ambient logger a NullLogger, so emit sites cost a single predicted branch.
    /// </summary>
    public bool EnableInternalLogging { get; set; } = true;

    /// <summary>
    ///     Minimum severity captured by the internal pillar (hub + file). <b>Live-adjustable</b>
    ///     like the CSVG level — lowering it starts surfacing more detail immediately. Default
    ///     <see cref="LiveSyncLogLevel.Information" />; keep it at Information+ for the always-live
    ///     window (Debug/Trace is for the file / a focused investigation).
    /// </summary>
    public LiveSyncLogLevel MinimumLogLevel { get; set; } = LiveSyncLogLevel.Information;

    /// <summary>
    ///     Max rows retained in the Diagnostics tab's in-app log window (the bounded ring across
    ///     BOTH internal and CSVG rows). Oldest drop first. ~200 bytes/row, so 5000 ≈ 1 MiB.
    /// </summary>
    public int MaxLogRows { get; set; } = 5000;

    /// <summary>
    ///     Mirror captured logs to a rolling file under the app-data root (see
    ///     <c>AppPaths.LogsDir</c>) so a copied diagnostics report can attach recent history for
    ///     user-reported issues. Default ON; always a no-op on WASM (no filesystem).
    /// </summary>
    public bool WriteLogFile { get; set; } = true;

    /// <summary>Max size of a single rolling log file before it rolls over, in kilobytes (default 4 MiB).</summary>
    public int FileMaxSizeKilobytes { get; set; } = 4096;

    /// <summary>How many rolled log files to retain (the active file plus this many older ones bound disk use).</summary>
    public int FileMaxCount { get; set; } = 5;
}

/// <summary>
///     Settings for the global demo-processing queue — the single source
///     all background demo parse/analyse work is pulled from.
///     <para>
///         <b>Pause is NOT here</b> — it is a transient runtime toggle (a Pause/Resume button); the app
///         always starts un-paused. Only the persisted values live here.
///     </para>
/// </summary>
public sealed class ProcessingQueueSettings
{
    /// <summary>
    ///     Master switch for background processing (the persisted "disable" control). Default ON.
    ///     When off, the queue does no background work at all; a user OPENING a demo (foreground) always
    ///     runs regardless.
    /// </summary>
    public bool BackgroundProcessingEnabled { get; set; } = true;

    /// <summary>
    ///     Max background-tier items held in the queue at once (a resource/clutter guard; the
    ///     durable backlogs re-feed as slots free). A user's manual/foreground request bypasses it.
    /// </summary>
    public int MaxQueueSize { get; set; } = 200;

    /// <summary>
    ///     Max concurrent HEAVY parses.
    ///     <b>
    ///         DEFAULT 1 — this is a 16 GB OOM-safety invariant, not a
    ///         preference.
    ///     </b>
    ///     Two concurrent multi-GB parses exhaust RAM; &gt; 1 is advanced/opt-in and
    ///     clamped to <see cref="Services.HeavyJobGate.HardCapConcurrency" />. See the design notes in git history.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;
}

/// <summary>
///     Highlights pipeline settings. Clip paddings are the
///     lead-in/lead-out defaults (rising edges fire at the END of the action, hence the long
///     lead-in); reel defaults seed the reel dialog so preferences are set once.
/// </summary>
public sealed class HighlightsSettings
{
    /// <summary>
    ///     Background library scan opt-in (default OFF for every category: a 200-demo library
    ///     is ~30 min of churn). Off, the tab still shows the open demo's harvested highlights and
    ///     any previously-cached rows.
    /// </summary>
    public bool BackgroundScan { get; set; }

    /// <summary>Default clip lead-in seconds before the highlight's firing tick.</summary>
    public double ClipLeadInSeconds { get; set; } = 15;

    /// <summary>Default clip lead-out seconds after the firing tick.</summary>
    public double ClipLeadOutSeconds { get; set; } = 5;

    // v0.6.0: the per-type LeadInOverrides/LeadOutOverrides dictionaries were REMOVED — nothing ever
    // read them (the global defaults above are the real knobs, editable in Settings and the reel
    // pane), and a settings surface that silently does nothing is worse than none. A settings.json
    // still carrying the keys binds fine (unknown sections are ignored). Re-add alongside a real
    // per-type editor if per-type padding is ever actually wanted.

    /// <summary>Reel output directory; null/empty = prompt in the dialog.</summary>
    public string? ReelOutputDirectory { get; set; }

    /// <summary>Reel container format (e.g. "mp4").</summary>
    public string ReelContainerFormat { get; set; } = "mp4";

    /// <summary>Reel capture frame rate.</summary>
    public int ReelFps { get; set; } = 60;

    /// <summary>Concatenate the reel's clips into one video.</summary>
    public bool ReelConcatenate { get; set; } = true;

    /// <summary>Capture game audio in reels.</summary>
    public bool ReelCaptureAudio { get; set; } = true;

    /// <summary>Reel CRF quality (used when <see cref="ReelBitrateKbps" /> is null; lower = better).</summary>
    public int ReelCrf { get; set; } = 20;

    /// <summary>Reel bitrate in kbps — mutually exclusive with CRF (null = CRF mode).</summary>
    public int? ReelBitrateKbps { get; set; }

    /// <summary>
    ///     Reel capture width in pixels (the Reels-tab resolution picker). This is BOTH the size CS2 is
    ///     launched at AND the size ffmpeg is told each raw present-hook frame measures, so it must be a
    ///     concrete pixel count. Default 1920×1080.
    /// </summary>
    public int ReelWidth { get; set; } = 1920;

    /// <summary>Reel capture height in pixels; see <see cref="ReelWidth" />.</summary>
    public int ReelHeight { get; set; } = 1080;
}

/// <summary>
///     Live CS2 sync (CSVG) settings. Whether the
///     feature is AVAILABLE is the <c>chrome.livesync</c> override in <see cref="FeatureFlags.Overrides" />
///; whether a session is RUNNING is never persisted — the engine always starts Off.
/// </summary>
public sealed class LiveSyncSettings
{
    /// <summary>Run against the bundled CSVG mock_server instead of a real CS2 install (developer/testing).</summary>
    public bool MockMode { get; set; }

    /// <summary>Optional externally-built mock_server path; setting it implies mock mode (CSVG semantics).</summary>
    public string? ExternalMockServerPath { get; set; }

    /// <summary>
    ///     Tick-identity shim: added to DV frame-clock ticks when mapping to CS2 demo ticks
    ///     (<c>cs2DemoTick = max(0, ServerTick) + TickOffset</c>). Default 0 — the identity
    ///     hypothesis; override only if validation on a real Windows CS2 install finds a fixed skew.
    /// </summary>
    public int TickOffset { get; set; }

    /// <summary>CS2 install root override (path ending "Counter-Strike Global Offensive"); null/empty = auto-detect.</summary>
    public string? Cs2RootInstallationDirectory { get; set; }

    /// <summary>
    ///     Proceed even when the plugin/game version pair is known-incompatible (advanced/developer;
    ///     maps to CSVG's <c>ForceIncompatiblePlugin</c>). Unknown pairs only ever warn regardless.
    /// </summary>
    public bool ForceIncompatiblePlugin { get; set; }

    /// <summary>CS2 game window width. CSVG's windowed default keeps the game visible next to DV.</summary>
    public int GameWindowWidth { get; set; } = 1280;

    /// <summary>CS2 game window height.</summary>
    public int GameWindowHeight { get; set; } = 800;

    /// <summary>Launch CS2 fullscreen instead of windowed.</summary>
    public bool GameFullscreen { get; set; }

    /// <summary>
    ///     Minimum severity for CSVG-host logs surfaced in the Output panel + Diagnostics tab.
    ///     <b>Live-adjustable</b>: lowering it (e.g. to <see cref="LiveSyncLogLevel.Debug" /> /
    ///     <see cref="LiveSyncLogLevel.Trace" />) starts surfacing more detail on the RUNNING
    ///     session with no reconnect (the log bridge reads this live). Default
    ///     <see cref="LiveSyncLogLevel.Information" />; <see cref="LiveSyncLogLevel.None" /> silences.
    /// </summary>
    public LiveSyncLogLevel MinimumLogLevel { get; set; } = LiveSyncLogLevel.Information;

    /// <summary>
    ///     Also surface framework (ASP.NET Core / gRPC / System) log categories — the per-request
    ///     Hosting.Diagnostics + gRPC transport lines useful when debugging plugin dial-back /
    ///     transport issues. Off by default (noisy: a line per gRPC call). Still honors
    ///     <see cref="MinimumLogLevel" />; live-adjustable like it.
    /// </summary>
    public bool CaptureFrameworkLogs { get; set; }
}

/// <summary>
///     Severity levels for the CSVG log surface — a UI-head-local mirror of
///     <c>Microsoft.Extensions.Logging.LogLevel</c> (same order/values, so the mapping in
///     <c>DemoViewer.NET.LiveSync</c> is a 1:1 cast). Kept here so the App/Browser head needs no
///     <c>Microsoft.Extensions.Logging</c> dependency and <c>settings.json</c> stays human-editable
///     (serialized by name via the shared <c>JsonStringEnumConverter</c>).
/// </summary>
public enum LiveSyncLogLevel
{
    /// <summary>Most verbose — every diagnostic line.</summary>
    Trace,

    /// <summary>Diagnostic detail.</summary>
    Debug,

    /// <summary>Informational (default).</summary>
    Information,

    /// <summary>Recoverable problems and above.</summary>
    Warning,

    /// <summary>Failures and above.</summary>
    Error,

    /// <summary>Only fatal/critical failures.</summary>
    Critical,

    /// <summary>Silence the CSVG log surface entirely.</summary>
    None
}

/// <summary>Demo-library settings.</summary>
public sealed class LibrarySettings
{
    /// <summary>Folders the library indexer watches. Empty by default.</summary>
    public string[] Folders { get; set; } = [];
}

/// <summary>Feature-flag state read by the feature-gating layer.</summary>
public sealed class FeatureFlags
{
    /// <summary>Per-feature explicit on/off overrides, keyed by feature id.</summary>
    public Dictionary<string, bool> Overrides { get; set; } = new();

    /// <summary>Master developer-mode toggle (unlocks developer-tier surfaces regardless of category).</summary>
    public bool DeveloperMode { get; set; }
}

/// <summary>
///     User feature tier. Serialized by its string name (via <c>JsonStringEnumConverter</c>) so
///     <c>settings.json</c> stays human-editable, and bound back from that name by the configuration binder.
/// </summary>
public enum UserCategory
{
    /// <summary>End user — only consumer-facing surfaces.</summary>
    Consumer,

    /// <summary>Power user — the default tier; consumer surfaces plus advanced tooling.</summary>
    PowerUser,

    /// <summary>Developer — everything, including parser/RE workbenches.</summary>
    Developer
}
