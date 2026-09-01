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
///         GitHub Release. Velopack picks the channel matching the running build, which is why the
///         workflow derives it from the file vpk produced rather than hardcoding it.
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

    private UpdateManager? _manager;

    /// <summary>
    ///     Resolved by the last successful <see cref="CheckAsync" />; consumed by
    ///     <see cref="DownloadAndApplyAsync" />. Velopack's apply step needs the same UpdateInfo the
    ///     check produced, so we hold it rather than re-checking and risking a different answer.
    /// </summary>
    private UpdateInfo? _pending;

    /// <inheritdoc />
    public string? CurrentVersion => TryGetManager()?.CurrentVersion?.ToString();

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        UpdateManager? mgr = TryGetManager();
        if (mgr is null || !mgr.IsInstalled)
        {
            // A dev `dotnet run`, the headless capture host, or a portable copy without Velopack
            // metadata. Nothing is wrong. There is simply no install to update.
            return UpdateCheckResult.NotSupported();
        }

        try
        {
            UpdateInfo? info = await Task.Run(() => mgr.CheckForUpdatesAsync(), ct).ConfigureAwait(false);
            _pending = info;
            return info is null
                ? UpdateCheckResult.UpToDate()
                : UpdateCheckResult.UpdateAvailable(info.TargetFullRelease.Version.ToString());
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
        UpdateManager? mgr = TryGetManager();
        if (mgr is null || _pending is null)
        {
            return false;
        }

        try
        {
            await Task.Run(() => mgr.DownloadUpdatesAsync(_pending, progress is null ? null : progress.Report, ct), ct)
                .ConfigureAwait(false);

            // Replaces the process. Nothing after this line runs on the success path, which is why
            // the caller must have already persisted anything it cares about.
            mgr.ApplyUpdatesAndRestart(_pending);
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
    ///     Builds the manager lazily and never throws: constructing it touches the filesystem to
    ///     locate install metadata, which fails in unpackaged runs. `prerelease: true` is REQUIRED:
    ///     `release.yml` uploads with `--pre true`, so every release is a GitHub prerelease and a
    ///     stable-only source would report "up to date" forever.
    /// </summary>
    private UpdateManager? TryGetManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, true));
        }
        catch (Exception)
        {
            return null;
        }

        return _manager;
    }
}
