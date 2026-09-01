namespace DemoViewer.NET.Services.LiveSync;

/// <summary>
///     App-facing contract for highlight-reel generation. The
///     implementation lives in the desktop-only LiveSync project (CSVG capture APIs) and arrives
///     via <see cref="AppHostHooks.ReelJobFactory" />. The reel dialog hands off to
///     <see cref="Start" /> and closes. Progress surfaces through the Reel status chip bound to
///     <see cref="Status" />/<see cref="StatusChanged" /> (no multi-minute modal). One job at a
///     time; while one runs, live-sync Enable/Reconnect and interactive demo loads are excluded
///     (the F1↔F3b single-CS2 interlock + the HeavyJobGate reel session).
/// </summary>
public interface IReelJobService
{
    /// <summary>The current job status (Idle when none ran yet).</summary>
    ReelJobStatus Status { get; }

    /// <summary>Raised on the UI thread on every status change.</summary>
    event EventHandler<ReelJobStatus>? StatusChanged;

    /// <summary>
    ///     Starts the background job. Throws <see cref="InvalidOperationException" /> when one is
    ///     already running. Suspends an active live-sync session first (its chip shows
    ///     "Paused for reel render"; it ends at a Reconnect prompt, never auto-relaunched).
    /// </summary>
    void Start(ReelRequest request);

    /// <summary>Cancels the running job (stops the capture session, restores the install).</summary>
    Task CancelAsync();

    /// <summary>
    ///     Starts a NEW job from the previous request's unfinished clips (failed + never
    ///     started). No-op when nothing failed or a job is running. Cross-run concatenation is a
    ///     CSVG follow-up: the retry produces its own output files.
    /// </summary>
    void RetryRemaining();
}

/// <summary>
///     One clip of a reel plan (already coalesced: see
///     <c>CS2DemoKit.Analysis.Clips.ClipPlanner</c>).
///     <para>
///         Ticks are the DV <b>frame clock</b>, not CS2 demo ticks. The <c>TickOffset</c> shim
///         is applied exactly once, at emission into CS2 (the LiveSync reel job's <c>Cs2Range</c>),
///         so every clamp upstream stays in one clock and a non-zero offset cannot skew them.
///     </para>
/// </summary>
/// <param name="DemoPath">Rooted demo path (pre-flight existence-checked).</param>
/// <param name="DemoSha256">The cached demo hash: CSVG's MatchChecksum (any stable string).</param>
/// <param name="PlayerSteamId64">Attributed player (steamid spectate when the plugin supports it).</param>
/// <param name="PlayerNameRaw">RAW in-demo name: the spec_player currency.</param>
/// <param name="StartTick">Window start: FRAME CLOCK.</param>
/// <param name="EndTick">Window end: FRAME CLOCK.</param>
/// <param name="TickRate">The demo's tick rate: playback timeouts derive from it (never hardcode 64).</param>
/// <param name="Label">Display label (the merged clip's titles).</param>
public sealed record ReelClip(
    string DemoPath,
    string? DemoSha256,
    long PlayerSteamId64,
    string PlayerNameRaw,
    long StartTick,
    long EndTick,
    int TickRate,
    string Label);

/// <summary>The dialog's hand-off: the clip plan plus output/encoding choices.</summary>
/// <remarks>
///     <see cref="Width" />/<see cref="Height" /> are the reel's capture resolution (the user's choice on
///     the Reels tab). They are trailing-optional: <c>0</c> means "unset, let the job fall back to the CS2
///     window size, then 1080p". The InEngineHooked present-hook backend has no frame header, so this size
///     is BOTH what CS2 launches at AND what ffmpeg is told each raw frame measures: the two must agree.
/// </remarks>
public sealed record ReelRequest(
    IReadOnlyList<ReelClip> Clips,
    string OutputDirectory,
    string BaseFileName,
    string ContainerFormat,
    int Fps,
    bool Concatenate,
    bool CaptureAudio,
    int? Crf,
    string? VideoBitrate,
    bool NoHudPreset,
    bool DryRun,
    int Width = 0,
    int Height = 0);

/// <summary>Reel job lifecycle.</summary>
public enum ReelJobPhase
{
    Idle,

    /// <summary>Suspending sync / starting the capture session (real: CS2+OBS, up to ~2 min).</summary>
    StartingSession,

    /// <summary>Clips are being captured (or dry-run walked); progress = k of N.</summary>
    Capturing,

    Completed,

    Failed,

    Cancelled
}

/// <summary>A point-in-time reel job status (immutable; the chip/flyout render from this).</summary>
public sealed record ReelJobStatus(
    ReelJobPhase Phase,
    int ClipsCompleted,
    int ClipsTotal,
    string? CurrentClipLabel,
    string? Error,
    string? OutputPath,
    IReadOnlyList<int> FailedClipIndices)
{
    /// <summary>The canonical idle status.</summary>
    public static ReelJobStatus Idle { get; } = new(ReelJobPhase.Idle, 0, 0, null, null, null, []);

    /// <summary>True while the job occupies the machine (chip visible, interlocks active).</summary>
    public bool IsRunning => Phase is ReelJobPhase.StartingSession or ReelJobPhase.Capturing;

    /// <summary>True when a finished job left unfinished clips (enables "Retry remaining").</summary>
    public bool HasRetryableClips =>
        !IsRunning && Phase is not ReelJobPhase.Idle && FailedClipIndices.Any();
}
