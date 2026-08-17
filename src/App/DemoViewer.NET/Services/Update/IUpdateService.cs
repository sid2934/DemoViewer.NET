namespace DemoViewer.NET.Services.Update;

/// <summary>
///     App-facing contract for the in-app updater. The implementation is Velopack-backed and lives
///     in the desktop-only entry-point project, reaching the App through
///     <see cref="DemoViewer.NET.Services.AppHostHooks.UpdateServiceFactory" /> — the same static
///     seam Live Sync uses, and for the same reason: the <c>Velopack</c> package is referenced only
///     by <c>DemoViewer.NET.Desktop</c>, so no Velopack type may appear in this project (the Browser
///     head references it directly).
///     <para>
///         A null service means "updates are not a thing here" — Browser, tests, designer, and any
///         unpackaged <c>dotnet run</c>. Callers must null-tolerate rather than branch on platform.
///     </para>
///     <para>
///         Threading: implementations do their network and disk work off the UI thread. Both methods
///         are safe to await from the UI thread and neither throws — failures are reported as
///         <see cref="UpdateCheckResult.Failed" /> / a false return, because a broken update check
///         must never be the reason the app is unusable.
///     </para>
/// </summary>
public interface IUpdateService
{
    /// <summary>
    ///     The running application version as the updater sees it (e.g. <c>0.5.2</c>), or null when
    ///     the app is not running from a packaged install. Displayed in Settings, and worth showing
    ///     even when it is null — "not a packaged build" is the useful answer in that case.
    /// </summary>
    string? CurrentVersion { get; }

    /// <summary>
    ///     Looks for a newer release. Never throws: offline, rate-limited, and malformed-feed cases
    ///     all come back as <see cref="UpdateCheckResult.Failed" /> with a human-readable reason.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>
    ///     Downloads the pending update and restarts into it. Only meaningful after
    ///     <see cref="CheckAsync" /> returned <see cref="UpdateCheckResult.UpdateAvailable" /> — the
    ///     implementation holds the resolved update from that call.
    ///     <para>
    ///         On success this does not return in any useful sense: the process is replaced. A
    ///         <c>false</c> return means the download or apply failed and the app is still running,
    ///         so the caller should surface the failure rather than assume a restart is coming.
    ///     </para>
    /// </summary>
    Task<bool> DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}

/// <summary>
///     Outcome of <see cref="IUpdateService.CheckAsync" />. A struct-like closed set rather than an
///     enum + out-params, so the "why did it fail" text travels with the failure.
/// </summary>
public readonly record struct UpdateCheckResult
{
    private UpdateCheckResult(UpdateCheckStatus status, string? version, string? message)
    {
        Status = status;
        Version = version;
        Message = message;
    }

    /// <summary>What happened.</summary>
    public UpdateCheckStatus Status { get; }

    /// <summary>The available version, set only when <see cref="Status" /> is UpdateAvailable.</summary>
    public string? Version { get; }

    /// <summary>Human-readable detail — the failure reason, or null.</summary>
    public string? Message { get; }

    /// <summary>True when there is something to install.</summary>
    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable;

    /// <summary>A newer release exists.</summary>
    public static UpdateCheckResult UpdateAvailable(string version) =>
        new(UpdateCheckStatus.UpdateAvailable, version, null);

    /// <summary>Checked successfully; already current.</summary>
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate, null, null);

    /// <summary>
    ///     Not an installed build (dev run, portable-without-metadata), so there is nothing to
    ///     update. Distinct from <see cref="Failed" />: nothing is wrong.
    /// </summary>
    public static UpdateCheckResult NotSupported() => new(UpdateCheckStatus.NotSupported, null, null);

    /// <summary>The check could not complete. <paramref name="message" /> is shown to the user.</summary>
    public static UpdateCheckResult Failed(string message) => new(UpdateCheckStatus.Failed, null, message);
}

/// <summary>Status half of <see cref="UpdateCheckResult" />.</summary>
public enum UpdateCheckStatus
{
    /// <summary>Already on the newest release.</summary>
    UpToDate,

    /// <summary>A newer release is available.</summary>
    UpdateAvailable,

    /// <summary>Not a packaged install — updates do not apply.</summary>
    NotSupported,

    /// <summary>The check failed (offline, rate-limited, bad feed).</summary>
    Failed
}
