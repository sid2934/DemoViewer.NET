#region

using DemoViewer.NET.Services.Update;
using Velopack;
using Velopack.Sources;

#endregion

namespace DemoViewer.NET.Desktop;

/// <summary>
///     Velopack-backed <see cref="IUpdateService" />. Lives here rather than in the App project
///     because the <c>Velopack</c> package reference is Desktop-only (the Browser head references
///     the App project directly, and Velopack is not WASM-viable); it reaches the App through
///     <c>AppHostHooks.UpdateServiceFactory</c>.
///     <para>
///         Reads the <c>releases.{channel}.json</c> feed that <c>release.yml</c> publishes onto the
///         GitHub Release. The stable channel is the OS short name (<c>win</c>, <c>osx</c>, <c>linux</c>),
///         the same name vpk defaults to at pack time and the workflow reads back from the feed file.
///     </para>
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    /// <summary>
    ///     The repository whose Releases carry the update feed. Hardcoded on purpose: this is the
    ///     app's own update channel, not user configuration, and a settable endpoint would be a way
    ///     to point an installed app at attacker-controlled packages.
    ///     <para>
    ///         Must be a repo GitHub serves unauthenticated: no token is sent (embedding one in a
    ///         shipped desktop binary would be extractable), so a private repo here would answer
    ///         every update check with a 404 that looks like "no updates". Releases publish
    ///         directly on the public source repo.
    ///     </para>
    /// </summary>
    private const string RepoUrl = "https://github.com/sid2934/DemoViewer.NET";

    /// <summary>
    ///     Appended to the OS channel (<c>win</c>, <c>osx</c>, <c>linux</c>) to name the beta feed, so a
    ///     beta pack is <c>vpk pack --channel win-beta</c>. No release publishes one yet.
    /// </summary>
    private const string BetaChannelSuffix = "-beta";

    private readonly Func<bool> _subscribedToBeta;

    private UpdateManager? _stable;
    private UpdateManager? _beta;

    /// <summary>
    ///     Resolved by the last successful <see cref="CheckAsync" />; consumed by
    ///     <see cref="DownloadAndApplyAsync" />. Velopack's apply step needs the same UpdateInfo the
    ///     check produced, on the manager that produced it.
    /// </summary>
    private (UpdateManager Manager, UpdateInfo Info, bool IsBeta)? _pending;

    /// <param name="subscribedToBeta">Read on every check, so the Settings toggle applies without a restart.</param>
    public VelopackUpdateService(Func<bool> subscribedToBeta) => _subscribedToBeta = subscribedToBeta;

    /// <inheritdoc />
    public string? CurrentVersion => StableManager()?.CurrentVersion?.ToString();

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        UpdateManager? stable = StableManager();
        if (stable is null || !stable.IsInstalled)
        {
            // A dev `dotnet run`, the headless capture host, or a portable copy without Velopack
            // metadata. Nothing is wrong. There is simply no install to update.
            return UpdateCheckResult.NotSupported();
        }

        try
        {
            _pending = null;
            UpdateInfo? best = await Task.Run(() => stable.CheckForUpdatesAsync(), ct).ConfigureAwait(false);
            (UpdateManager Manager, UpdateInfo Info, bool IsBeta)? pending = best is null ? null : (stable, best, false);

            if (_subscribedToBeta() && BetaManager() is { } beta)
            {
                UpdateInfo? betaInfo = await Task.Run(() => beta.CheckForUpdatesAsync(), ct).ConfigureAwait(false);
                if (betaInfo is not null &&
                    (best is null || betaInfo.TargetFullRelease.Version > best.TargetFullRelease.Version))
                {
                    best = betaInfo;
                    pending = (beta, betaInfo, true);
                }
            }

            if (pending is { } chosen && chosen.IsBeta != IsBetaVersion(stable.CurrentVersion?.ToString()))
            {
                // Crossing between stable and beta. Velopack picks deltas by version range alone and does not
                // check the rebuilt package, so a delta built on the other line's base must not be used.
                pending = chosen with { Info = new UpdateInfo(chosen.Info.TargetFullRelease, false) };
            }

            _pending = pending;

            return best is null
                ? UpdateCheckResult.UpToDate()
                : UpdateCheckResult.UpdateAvailable(best.TargetFullRelease.Version.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, GitHub rate limit (60/hr unauthenticated), malformed feed. None
            // of these should ever be more than a message. The app is fully usable without updates.
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // A beta found before the user unsubscribed is not installed after it.
        if (_pending is not { } pending || (pending.IsBeta && !_subscribedToBeta()))
        {
            return false;
        }

        try
        {
            await Task.Run(() => pending.Manager.DownloadUpdatesAsync(pending.Info, progress is null ? null : progress.Report, ct), ct)
                .ConfigureAwait(false);

            // Replaces the process. Nothing after this line runs on the success path, which is why
            // the caller must have already persisted anything it cares about.
            pending.Manager.ApplyUpdatesAndRestart(pending.Info);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed download leaves the install untouched. Velopack stages into a packages dir
            // and only swaps on apply. Reporting false keeps the user on a working version.
            return false;
        }
    }

    /// <summary>
    ///     Stable releases only. The channel is explicit because Velopack otherwise defaults to the
    ///     channel the running build was packed with, and a beta install would keep reading the beta
    ///     feed after the user unsubscribes. No version downgrade: leaving beta waits for stable to
    ///     catch up rather than installing an older build over newer config and cache data.
    /// </summary>
    private UpdateManager? StableManager() => _stable ??= TryCreate(OsChannel, false);

    /// <summary>
    ///     The beta feed, which lives on GitHub prereleases. Checked alongside <see cref="StableManager" />
    ///     and the newer of the two wins, so a subscriber is never held on an old beta.
    /// </summary>
    private UpdateManager? BetaManager() => _beta ??= TryCreate(OsChannel + BetaChannelSuffix, true);

    private static bool IsBetaVersion(string? version) => version?.Contains(BetaChannelSuffix, StringComparison.OrdinalIgnoreCase) == true;

    private static string OsChannel => VelopackRuntimeInfo.GetOsShortName(VelopackRuntimeInfo.SystemOs);

    /// <summary>
    ///     Never throws: constructing a manager touches the filesystem to locate install metadata,
    ///     which fails in unpackaged runs.
    /// </summary>
    private static UpdateManager? TryCreate(string channel, bool prerelease)
    {
        try
        {
            return new UpdateManager(
                new GithubSource(RepoUrl, null, prerelease),
                new UpdateOptions { ExplicitChannel = channel });
        }
        catch (Exception)
        {
            return null;
        }
    }
}
