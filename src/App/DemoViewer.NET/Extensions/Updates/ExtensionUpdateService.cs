#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     Checks each extension's feed, downloads a newer version and stages it where the loader reads
///     (strat-book-plugin.md §7.10). It loads nothing: a staged copy is picked up by
///     <see cref="ExtensionLoader.Resolve" /> at the next start. Every network and disk step runs as a
///     queue item at user priority, since a click asked for it, and every failure is a state or a result,
///     never an exception.
///     <para>
///         Decision 6 (§10) is pending. The one predicate that decides whether a published version is offered
///         is <see cref="IsOffered" />; option (A) replaces it with a check against a CI-written
///         <c>builtAgainst</c> block, option (B) keeps <see cref="DefaultIsOffered" />.
///     </para>
/// </summary>
public sealed class ExtensionUpdateService
{
    /// <summary>The queue owner tag every item here carries.</summary>
    public const string Owner = "extensions";

    private static readonly TimeSpan _autoCheckInterval = TimeSpan.FromHours(1);

    private readonly string _extensionsDirectory;
    private readonly IReadOnlyList<PackStatus> _statuses;
    private readonly ExtensionHostInfo _host;
    private readonly ITrustPolicy _trust;
    private readonly IExtensionFeedClient _client;
    private readonly Func<string, Uri> _feedUrl;
    private readonly IDemoProcessingQueue? _queue;
    private readonly object _sync = new();
    private readonly Dictionary<string, ExtensionUpdateState> _last = new(StringComparer.Ordinal);

    /// <param name="configRoot">The config root; <c>extensions/</c> under it is the only tree touched.</param>
    /// <param name="statuses">Every declared pack with its verdict (<see cref="FeaturePacks.Statuses" />); the running versions.</param>
    /// <param name="host">What this app provides, for the compatibility check.</param>
    /// <param name="trust">The loader's trust policy, asked about the unpacked directory before it is installed.</param>
    /// <param name="client">The network.</param>
    /// <param name="feedUrl">Each pack id's feed URL (<see cref="ExtensionFeedSource.Resolve" />).</param>
    /// <param name="queue">The processing queue, or null to run on the pool.</param>
    /// <param name="isOffered">The decision-6 predicate; null is <see cref="DefaultIsOffered" />.</param>
    public ExtensionUpdateService(
        string configRoot, IReadOnlyList<PackStatus> statuses, ExtensionHostInfo host, ITrustPolicy trust,
        IExtensionFeedClient client, Func<string, Uri> feedUrl, IDemoProcessingQueue? queue = null,
        Func<ExtensionFeedEntry, ExtensionHostInfo, bool>? isOffered = null)
    {
        ArgumentNullException.ThrowIfNull(configRoot);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(feedUrl);
        _extensionsDirectory = Path.GetFullPath(ExtensionLoader.ExtensionsDirectory(configRoot)!);
        _statuses = statuses;
        _host = host;
        _trust = trust;
        _client = client;
        _feedUrl = feedUrl;
        _queue = queue;
        IsOffered = isOffered ?? DefaultIsOffered;
    }

    private static ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory);

    // Windows and macOS file systems fold case by default; a path there is the same path in any case.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>How long a check result stands before opening Settings checks again.</summary>
    public static TimeSpan AutoCheckInterval => _autoCheckInterval;

    /// <summary>
    ///     Whether a published version may be offered to this app. Decision 6's seam: option (B), as built,
    ///     is <see cref="DefaultIsOffered" />; option (A) swaps in a predicate over a <c>builtAgainst</c> block.
    /// </summary>
    public Func<ExtensionFeedEntry, ExtensionHostInfo, bool> IsOffered { get; }

    /// <summary>The packs this service knows, as handed in.</summary>
    public IReadOnlyList<PackStatus> Statuses => _statuses;

    /// <summary>
    ///     The most recent state of every extension that has been checked this run, in declaration order;
    ///     empty before any check. Settings seeds its rows from this when it opens within the hour.
    /// </summary>
    public IReadOnlyList<ExtensionUpdateState> LastStates
    {
        get
        {
            lock (_sync)
            {
                List<ExtensionUpdateState> states = [];
                foreach (PackStatus status in _statuses)
                {
                    if (_last.TryGetValue(status.Pack.Id, out ExtensionUpdateState? state))
                    {
                        states.Add(state);
                    }
                }

                return states;
            }
        }
    }

    /// <summary>The most recent state of <paramref name="packId" />, or null when it has not been checked this run.</summary>
    public ExtensionUpdateState? LastState(string packId)
    {
        ArgumentNullException.ThrowIfNull(packId);
        lock (_sync)
        {
            return _last.GetValueOrDefault(packId);
        }
    }

    /// <summary>Option (B): <see cref="PackCompatibility.Check" /> accepts the entry's manifest.</summary>
    public static bool DefaultIsOffered(ExtensionFeedEntry entry, ExtensionHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(host);
        return PackCompatibility.Check(entry.Manifest, host).IsCompatible;
    }

    /// <summary>
    ///     A staged version newer than the running copy that the loader would take at the next start: the
    ///     highest under <c>extensions/&lt;id&gt;/</c> above the running version that passes the compatibility
    ///     check. Null when none, or when the running copy's version is unknown.
    /// </summary>
    public SemVersion? PendingVersion(string packId)
    {
        ArgumentNullException.ThrowIfNull(packId);
        PackStatus? status = _statuses.FirstOrDefault(s => string.Equals(s.Pack.Id, packId, StringComparison.Ordinal));
        if (status?.Manifest is not { } manifest)
        {
            return null;
        }

        try
        {
            return ExtensionLoader.Discover(_extensionsDirectory).Candidates
                .Where(c => string.Equals(c.Manifest.Id, packId, StringComparison.Ordinal)
                            && c.Manifest.Version > manifest.Version
                            && PackCompatibility.Check(c.Manifest, _host).IsCompatible)
                .Select(c => c.Manifest.Version)
                .OrderByDescending(v => v)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when any extension has a staged version waiting for a restart.</summary>
    public bool PendingRestart => _statuses.Any(s => PendingVersion(s.Pack.Id) is not null);

    /// <summary>
    ///     Fetches every extension's feed as one queue item at user priority and computes each one's
    ///     <see cref="ExtensionUpdateState" />. Never throws: a feed that cannot be fetched or read is a state.
    /// </summary>
    public async Task<IReadOnlyList<ExtensionUpdateState>> CheckAsync(CancellationToken ct = default)
    {
        ExtensionUpdateState?[] found = new ExtensionUpdateState?[_statuses.Count];
        await QueueWork.RunJob(_queue, QueueJobKind.ExtensionUpdate, "Check for extension updates", Owner, async ctx =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.CancellationToken);
            for (int i = 0; i < _statuses.Count; i++)
            {
                found[i] = await CheckCore(_statuses[i], linked.Token).ConfigureAwait(false);
            }
        }, DemoJobPriority.UserRequested).ConfigureAwait(false);

        // A pack the item never reached (cancelled, or the queue dropped it) reads as unreachable.
        ExtensionUpdateState[] states = new ExtensionUpdateState[_statuses.Count];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = found[i] ?? Unreachable(_statuses[i], "the check did not finish", null);
        }

        Remember(states);
        return states;
    }

    /// <summary><see cref="CheckAsync(CancellationToken)" /> for one extension.</summary>
    public async Task<ExtensionUpdateState> CheckAsync(string packId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packId);
        PackStatus status = _statuses.FirstOrDefault(s => string.Equals(s.Pack.Id, packId, StringComparison.Ordinal))
                            ?? throw new ArgumentException($"'{packId}' is not a declared extension.", nameof(packId));
        ExtensionUpdateState? state = null;
        string name = status.Manifest?.Name ?? status.Pack.Id;
        await QueueWork.RunJob(_queue, QueueJobKind.ExtensionUpdate, $"Check for updates: {name}", Owner, async ctx =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.CancellationToken);
            state = await CheckCore(status, linked.Token).ConfigureAwait(false);
        }, DemoJobPriority.UserRequested).ConfigureAwait(false);

        state ??= Unreachable(status, "the check did not finish", null);
        Remember([state]);
        return state;
    }

    /// <summary>
    ///     Downloads <paramref name="entry" />'s zip and stages it, as one queue item at user priority, in this
    ///     order: the bytes to <c>.staging/&lt;id&gt;/&lt;version&gt;.zip.part</c>, bounded by the feed's size;
    ///     the size and sha256 against the feed; a bounded extraction into <c>.staging/&lt;id&gt;/&lt;version&gt;/</c>
    ///     that refuses any entry escaping it; the manifest at the root, naming this id and version and an
    ///     assembly that is present; the trust policy over the unpacked directory; then one rename to
    ///     <c>&lt;id&gt;/&lt;version&gt;/</c>. Any refusal removes the staging files. An already present target is
    ///     <see cref="StageOutcome.AlreadyInstalled" />, and nothing is changed.
    /// </summary>
    public async Task<StageResult> DownloadAndStageAsync(ExtensionFeedEntry entry, IProgress<ExtensionDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StageResult? result = null;
        await QueueWork.RunJob(_queue, QueueJobKind.ExtensionUpdate, $"Download extension update: {entry.Manifest.Name} {entry.Version}", Owner,
            async ctx =>
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.CancellationToken);
                result = await StageCore(entry, ctx, progress, linked.Token).ConfigureAwait(false);
            }, DemoJobPriority.UserRequested).ConfigureAwait(false);

        return result ?? new StageResult(StageOutcome.Cancelled, null);
    }

    /// <summary>
    ///     The start-of-run sweep, as a background queue item: removes <c>extensions/.staging/</c> whole (a
    ///     download that did not finish), then every staged version of a known extension that is not newer
    ///     than the running copy and is not the directory the running copy loaded from. Newer versions stay:
    ///     one the loader refused today may load after an app update. Touches nothing outside
    ///     <c>extensions/</c> and never follows a link.
    /// </summary>
    public Task CleanupOnStartAsync() =>
        QueueWork.RunJob(_queue, QueueJobKind.ExtensionUpdate, "Tidy staged extension updates", Owner, _ =>
        {
            Cleanup();
            return Task.CompletedTask;
        });

    /// <summary>The sweep behind <see cref="CleanupOnStartAsync" />, inline. Never throws.</summary>
    public void Cleanup()
    {
        try
        {
            string staging = ExtensionStaging.StagingRoot(_extensionsDirectory);
            if (Directory.Exists(staging) && ExtensionStaging.IsInside(_extensionsDirectory, staging))
            {
                ExtensionStaging.DeleteTree(staging);
                AppLog.ExtensionStagingRemoved(Log, staging, "unfinished download");
            }

            if (!Directory.Exists(_extensionsDirectory))
            {
                return;
            }

            ExtensionLoader.Discovery discovery = ExtensionLoader.Discover(_extensionsDirectory);
            foreach (PackStatus status in _statuses)
            {
                if (status.Manifest is not { } running)
                {
                    continue;
                }

                string? runningDirectory = status.Source is PackSource.Staged staged ? Path.GetFullPath(staged.Directory) : null;
                foreach (ExtensionCandidate candidate in discovery.Candidates)
                {
                    if (!string.Equals(candidate.Manifest.Id, status.Pack.Id, StringComparison.Ordinal)
                        || candidate.Manifest.Version > running.Version)
                    {
                        continue;
                    }

                    string directory = Path.GetFullPath(candidate.Directory);
                    if (string.Equals(directory, runningDirectory, PathComparison)
                        || !ExtensionStaging.IsInside(_extensionsDirectory, directory))
                    {
                        continue;
                    }

                    ExtensionStaging.DeleteTree(directory);
                    string reason = "superseded by the running " + running.Version;
                    AppLog.ExtensionStagingRemoved(Log, directory, reason);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.OperationFailed(Log, "tidy staged extension updates", ex);
        }
    }

    private async Task<ExtensionUpdateState> CheckCore(PackStatus status, CancellationToken ct)
    {
        string packId = status.Pack.Id;
        if (status.Manifest is not { } manifest)
        {
            return new ExtensionUpdateState(packId, packId, null, status.Source, ExtensionUpdateStatus.Unknown,
                Error: "the extension's own manifest could not be read", CheckedAt: DateTimeOffset.UtcNow);
        }

        string text;
        try
        {
            text = await _client.GetFeedAsync(_feedUrl(packId), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.ExtensionFeedCheckFailed(Log, packId, nameof(ExtensionUpdateStatus.FeedUnreachable), ex.Message);
            return Unreachable(status, "the update feed could not be reached", ex.Message);
        }

        ExtensionFeed feed;
        try
        {
            feed = ExtensionFeed.Parse(text);
        }
        catch (ExtensionFeedException ex)
        {
            AppLog.ExtensionFeedCheckFailed(Log, packId, nameof(ExtensionUpdateStatus.FeedInvalid), ex.Message);
            return new ExtensionUpdateState(packId, manifest.Name, manifest.Version, status.Source, ExtensionUpdateStatus.FeedInvalid,
                Error: "the update feed could not be read", LogDetail: ex.Message, CheckedAt: DateTimeOffset.UtcNow);
        }

        if (!string.Equals(feed.Id, packId, StringComparison.Ordinal))
        {
            string detail = $"the feed is for '{feed.Id}'";
            AppLog.ExtensionFeedCheckFailed(Log, packId, nameof(ExtensionUpdateStatus.FeedInvalid), detail);
            return new ExtensionUpdateState(packId, manifest.Name, manifest.Version, status.Source, ExtensionUpdateStatus.FeedInvalid,
                Error: "the update feed is for another extension", LogDetail: detail, CheckedAt: DateTimeOffset.UtcNow);
        }

        SemVersion installed = manifest.Version;
        SemVersion? pending = PendingVersion(packId);
        ExtensionFeedEntry? latest = feed.Latest;
        ExtensionFeedEntry? offered = feed.Entries.FirstOrDefault(e => e.Version > installed && IsOfferedSafely(e));

        ExtensionUpdateStatus state;
        PackCompatibility? latestCompatibility = null;
        if (offered is not null && (pending is null || offered.Version > pending))
        {
            state = ExtensionUpdateStatus.UpdateAvailable;
        }
        else if (pending is not null)
        {
            state = ExtensionUpdateStatus.PendingRestart;
            offered = null;
        }
        else if (latest is not null && latest.Version > installed)
        {
            state = ExtensionUpdateStatus.NeedsNewerApp;
            latestCompatibility = PackCompatibility.Check(latest.Manifest, _host);
        }
        else
        {
            state = ExtensionUpdateStatus.UpToDate;
        }

        return new ExtensionUpdateState(packId, manifest.Name, installed, status.Source, state, offered, latest, latestCompatibility, pending,
            CheckedAt: DateTimeOffset.UtcNow);
    }

    private void Remember(IEnumerable<ExtensionUpdateState> states)
    {
        lock (_sync)
        {
            foreach (ExtensionUpdateState state in states)
            {
                _last[state.PackId] = state;
            }
        }
    }

    // A predicate that throws offers nothing, the way a trust policy that throws trusts nothing.
    private bool IsOfferedSafely(ExtensionFeedEntry entry)
    {
        try
        {
            return IsOffered(entry, _host);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static ExtensionUpdateState Unreachable(PackStatus status, string error, string? logDetail) =>
        new(status.Pack.Id, status.Manifest?.Name ?? status.Pack.Id, status.Manifest?.Version, status.Source,
            ExtensionUpdateStatus.FeedUnreachable, Error: error, LogDetail: logDetail, CheckedAt: DateTimeOffset.UtcNow);

    private async Task<StageResult> StageCore(ExtensionFeedEntry entry, IQueueJobContext ctx, IProgress<ExtensionDownloadProgress>? progress,
        CancellationToken ct)
    {
        string id = entry.Manifest.Id;
        SemVersion version = entry.Version;
        string name = entry.Manifest.Name;
        string installed = ExtensionStaging.InstalledPath(_extensionsDirectory, id, version);
        string part = ExtensionStaging.PartPath(_extensionsDirectory, id, version);
        string extracted = ExtensionStaging.ExtractPath(_extensionsDirectory, id, version);

        // The id was validated by the feed parser and the version is a SemVersion's own text, so these
        // cannot leave the tree; the check stands so a future caller cannot either.
        if (!ExtensionStaging.IsInside(_extensionsDirectory, installed) || !ExtensionStaging.IsInside(_extensionsDirectory, extracted))
        {
            return Refuse(entry, "the extension's id or version is not a valid folder name", null);
        }

        if (Directory.Exists(installed))
        {
            return new StageResult(StageOutcome.AlreadyInstalled, installed);
        }

        if (entry.Size > ExtensionStaging.MaxBytes)
        {
            return Refuse(entry, "the update is larger than the allowed size", null);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            ExtensionStaging.DeleteTree(extracted);
            File.Delete(part);

            long total = entry.Size;
            // Inline, not Progress<T>: that one posts through a synchronization context, and the caller's
            // own IProgress does its marshalling.
            InlineProgress bytes = new(received =>
            {
                progress?.Report(new ExtensionDownloadProgress(received, total));
                ctx.Report((int)Math.Min(100, received * 100 / Math.Max(1, total)), 100, $"{Megabytes(received)} of {Megabytes(total)} MB");
            });
            await using (FileStream destination = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await _client.DownloadAsync(entry.Url, destination, total, bytes, ct).ConfigureAwait(false);
            }

            if (ExtensionStaging.VerifyDownload(part, entry.Size, entry.Sha256) is { } mismatch)
            {
                return Refuse(entry, mismatch, null);
            }

            if (ExtensionStaging.Extract(part, extracted) is { } badArchive)
            {
                return Refuse(entry, badArchive, null);
            }

            ExtensionManifest? manifest = ExtensionStaging.ReadStagedManifest(extracted, id, version, out string? badManifest);
            if (manifest is null)
            {
                return Refuse(entry, badManifest!, null);
            }

            TrustVerdict verdict;
            try
            {
                verdict = _trust.Judge(extracted, manifest);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                verdict = TrustVerdict.No("the trust check failed", ex.Message);
            }

            if (!verdict.Trusted)
            {
                return Refuse(entry, verdict.Reason ?? "the copy is not signed by this app's publisher", verdict.LogDetail);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
            try
            {
                Directory.Move(extracted, installed);
            }
            catch (IOException) when (Directory.Exists(installed))
            {
                RemoveStaging(part, extracted);
                return new StageResult(StageOutcome.AlreadyInstalled, installed);
            }

            RemoveStaging(part, extracted);
            string versionText = version.ToString();
            AppLog.ExtensionUpdateStaged(Log, name, versionText, installed);
            return new StageResult(StageOutcome.Installed, installed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RemoveStaging(part, extracted);
            return new StageResult(StageOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or ExtensionDownloadException
                                       or InvalidOperationException or OperationCanceledException)
        {
            string detail = ex is HttpRequestException or ExtensionDownloadException or OperationCanceledException
                ? "the download failed"
                : "the update could not be written to the app's data folder";
            return Refuse(entry, detail, ex.Message);
        }
    }

    private StageResult Refuse(ExtensionFeedEntry entry, string detail, string? logDetail)
    {
        RemoveStaging(ExtensionStaging.PartPath(_extensionsDirectory, entry.Manifest.Id, entry.Version),
            ExtensionStaging.ExtractPath(_extensionsDirectory, entry.Manifest.Id, entry.Version));
        string versionText = entry.Version.ToString();
        string suffix = logDetail is null ? string.Empty : " [" + logDetail + "]";
        AppLog.ExtensionUpdateRefused(Log, entry.Manifest.Name, versionText, detail, suffix);
        return new StageResult(StageOutcome.Refused, null, detail, logDetail);
    }

    // Both staging paths, then the id folder under .staging when it is empty. Never the installed tree.
    private void RemoveStaging(string part, string extracted)
    {
        try
        {
            if (ExtensionStaging.IsInside(ExtensionStaging.StagingRoot(_extensionsDirectory), extracted))
            {
                ExtensionStaging.DeleteTree(extracted);
            }

            if (File.Exists(part) && ExtensionStaging.IsInside(ExtensionStaging.StagingRoot(_extensionsDirectory), part))
            {
                File.Delete(part);
            }

            string? idFolder = Path.GetDirectoryName(part);
            if (idFolder is not null && Directory.Exists(idFolder) && !Directory.EnumerateFileSystemEntries(idFolder).Any())
            {
                Directory.Delete(idFolder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The start-of-run sweep removes whatever this could not.
        }
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
