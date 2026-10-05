#region

using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Services.DemoCache;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The evaluator that walks a demo's grenades: an <see cref="IExtensionPass" />
///     on the same one-parse fan-out as the others, registered last because it reads nothing they write.
///     Per demo the work is one <see cref="GrenadeWalker.Walk" /> on the parse the queue already paid for,
///     the rows sibling, then one record stamp, the stamp last so a crash between them leaves "not walked"
///     and never a stamp without its file.
///     <para>
///         <b>Background indexing is off by default</b> (the <c>HighlightsSettings.BackgroundScan</c>
///         precedent: two to three seconds per demo is half an hour over a large library). Off, the demo
///         the user has open is still walked on the parse its open paid for, and any demo can be forced
///         from Match Overview at user priority. A Library tier-2 pass does not walk a demo on its own
///         parse while the opt-in is off: that would turn the sweep the user left off back on, the
///         Suggested Tags rule.
///     </para>
///     <para>
///         A throw stamps <see cref="DemoAnalysisState.Failed" />, which the backlog excludes until the
///         user asks again; the parse itself failing is not this evaluator's to mark.
///     </para>
/// </summary>
public sealed class GrenadeIndexEvaluator : IExtensionPass
{
    /// <summary>The queue owner tag and the pass id for this evaluator.</summary>
    public const string EvaluatorId = "grenades";

    private static ILogger? _diagLog;

    private readonly Func<bool> _backgroundIndex;
    private readonly DemoCacheStore _demoCache;
    private readonly Func<bool> _enabled;
    private readonly HashSet<string> _forcedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, List<TrajectoryPoint>>> _flights = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Func<string?> _openDemo;
    private readonly Action<Action> _post;
    private readonly Func<int> _stride;

    // Test seam: the walk for a parse. Null is the real walker; a synthetic parse has no entities to walk.
    private readonly Func<ParsedDemo, GrenadeWalk>? _walk;

    /// <param name="demoCache">The unified demo cache: the stamp and the siblings live there.</param>
    /// <param name="backgroundIndex">The live <c>GrenadesSettings.BackgroundIndex</c>; forced paths and the open demo ignore it.</param>
    /// <param name="openDemo">The open demo's path, resolved at call time; null when none.</param>
    /// <param name="stride">The live <c>GrenadesSettings.TrajectoryStride</c>; null defaults to 4.</param>
    /// <param name="post">UI-thread marshal for <see cref="Indexed" />; defaults to synchronous.</param>
    /// <param name="walk">The walk to run; null walks the parse through <see cref="GrenadeWalker" />.</param>
    /// <param name="enabled">The owning pack's gate; off, nothing is wanted, not even the open demo. Defaults to always-on.</param>
    public GrenadeIndexEvaluator(
        DemoCacheStore demoCache,
        Func<bool> backgroundIndex,
        Func<string?>? openDemo = null,
        Func<int>? stride = null,
        Action<Action>? post = null,
        Func<ParsedDemo, GrenadeWalk>? walk = null,
        Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(backgroundIndex);
        _demoCache = demoCache;
        _backgroundIndex = backgroundIndex;
        _openDemo = openDemo ?? (() => null);
        _stride = stride ?? (() => 4);
        _post = post ?? (action => action());
        _walk = walk;
        _enabled = enabled ?? (() => true);
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category);

    /// <summary>The scheduling of the extension's passes, so a forced request can ask for the demo again. Null in tests.</summary>
    public IExtensionPasses? Passes { get; set; }

    /// <summary>True while the scheduler has a walk in flight.</summary>
    public bool IsIndexing => Passes?.IsBusy(EvaluatorId) ?? false;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <summary>A demo's grenades were written and stamped. Raised through the post delegate with its path.</summary>
    public event Action<string>? Indexed;

    /// <summary>
    ///     A demo's grenades were written and stamped. Raised on the walking thread, inside the demo's visit, before
    ///     <see cref="Indexed" />, so a later pass on the same visit sees what a handler did with them.
    /// </summary>
    public event Action<string>? Walked;

    /// <summary>Whether the demo needs this pass now.</summary>
    /// <remarks>
    ///     From the index row alone: the owning pack's gate on, a parsed demo whose grenades are missing,
    ///     stale under the walker version or the schema, and not failed, with the sweep on or the demo forced.
    ///     The open demo is wanted whatever the opt-in says: its walk runs on the parse the open paid for.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!_enabled())
        {
            return false;
        }

        DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
        lock (_gate)
        {
            if (_forcedPaths.Contains(path))
            {
                return entry is { ParseSchema: > 0 } && !entry.IsGrenadesCurrent(GrenadeWalker.Version);
            }
        }

        return NeedsWalk(entry) && (_backgroundIndex() || IsOpen(path));
    }

    /// <summary>Whether the demo will need this pass once the pass it runs after has written.</summary>
    /// <remarks>
    ///     A demo the Library has not parsed yet, with the sweep on, the demo forced or the demo open: the
    ///     walk follows the parse stamp.
    /// </remarks>
    public bool WantsAfterUpstream(string path)
    {
        if (!_enabled() || _demoCache.TryGetIndex(path) is { ParseSchema: > 0 })
        {
            return false;
        }

        lock (_gate)
        {
            return _forcedPaths.Contains(path) || _backgroundIndex() || IsOpen(path);
        }
    }

    /// <inheritdoc />
    public JobPriority PriorityFor(string demoPath)
    {
        lock (_gate)
        {
            return _forcedPaths.Contains(demoPath) ? JobPriority.UserRequested : JobPriority.Background;
        }
    }

    /// <inheritdoc />
    public long OrderHint(string demoPath) => _demoCache.TryGetIndex(demoPath)?.ModifiedTicks ?? 0;

    /// <inheritdoc />
    public DemoInterest Interest(string demoPath) =>
        Wants(demoPath) ? DemoInterest.Yes : WantsAfterUpstream(demoPath) ? DemoInterest.AfterUpstream : DemoInterest.No;

    /// <inheritdoc />
    public void Run(IPassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Evaluate(context.DemoPath, context.Parsed);
    }

    /// <summary>Does the pass's work on <paramref name="parsed" />.</summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="parsed">The demo's parse.</param>
    public void Evaluate(string path, ParsedDemo parsed) => Refresh(path, parsed);

    /// <inheritdoc />
    public void OnFailed(string demoPath) => ClearForced(demoPath);

    /// <summary>
    ///     The demos the sweep or a force would submit, newest first, for the pending counts. Derived from
    ///     the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
        if (!_enabled())
        {
            return [];
        }

        bool background = _backgroundIndex();
        HashSet<string> forced;
        lock (_gate)
        {
            forced = [.. _forcedPaths];
        }

        if (!background && forced.Count == 0)
        {
            return [];
        }

        return
        [
            .. _demoCache.Index
                .Where(e => forced.Contains(e.Path)
                    ? e.ParseSchema > 0 && !e.IsGrenadesCurrent(GrenadeWalker.Version)
                    : background && NeedsWalk(e))
                .OrderByDescending(e => e.ModifiedTicks)
                .Select(e => e.Path)
        ];
    }

    /// <summary>Whether a demo's grenades are walked and current: the Match Overview action hides when they are.</summary>
    /// <param name="path">The demo's path.</param>
    public bool IsCurrent(string path) =>
        _demoCache.TryGetIndex(path)?.IsGrenadesCurrent(GrenadeWalker.Version) ?? false;

    /// <summary>
    ///     Walks one demo at user priority regardless of the opt-in and of an earlier failure: Match
    ///     Overview's "Index grenades". A failed row is lifted back to Pending first. A no-op while the
    ///     owning pack's gate is off: Match Overview hides the chip for this reason too, but a forced path
    ///     left behind here would walk unasked the moment the pack came back on.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    public void Request(string path)
    {
        if (!_enabled())
        {
            return;
        }

        try
        {
            if (_demoCache.TryGetIndex(path)?.GrenadesStamp() is { State: DemoAnalysisState.Failed })
            {
                _demoCache.UpdateExisting(path, r => r.ClearFailed(EvaluatorId));
            }
        }
        catch (Exception)
        {
            // The forced set below still carries it into the queue.
        }

        lock (_gate)
        {
            _forcedPaths.Add(path);
        }

        Passes?.Request(path);
    }

    private static bool NeedsWalk(DemoCacheIndexEntry? entry) =>
        entry is { ParseSchema: > 0 } && entry.NeedsGrenades(GrenadeWalker.Version);

    private bool IsOpen(string path) =>
        _openDemo() is { } open && string.Equals(open, path, StringComparison.OrdinalIgnoreCase);

    private void Refresh(string path, ParsedDemo parsed)
    {
        string fileName = Path.GetFileName(path);
        // A parse without user commands would stamp rows current with no inputs. The demo stays wanted,
        // so the next pass submits it on a full parse.
        if ((parsed.Plan.Categories & MessageCategories.UserCmds) == 0)
        {
            GrenadeIndexLog.NoUserCommands(Log, fileName);
            return;
        }

        try
        {
            GrenadeWalk walk = _walk?.Invoke(parsed)
                               ?? GrenadeWalker.Walk(parsed, new GrenadeWalkOptions(TrajectoryStride: Math.Max(1, _stride())));
            DemoCacheRecord? record = _demoCache.TryLoadRecord(path);
            (GrenadeDocument rows, _) = GrenadeSidecar.Build(path, record, parsed, walk);

            // Rows first, stamp last: a crash between them leaves the demo "not walked".
            GrenadeSidecar.WriteRows(_demoCache, path, rows);
            lock (_gate)
            {
                // Nothing takes them when no index listens (a test, a host without the Utility Book).
                if (_flights.Count >= 16)
                {
                    _flights.Clear();
                }

                _flights[path] = walk.Rows.ToDictionary(r => r.Id, r => r.Trajectory, StringComparer.Ordinal);
            }

            _demoCache.UpdateExisting(path, r =>
            {
                r.SetGrenades(GrenadeWalker.Version, rows.Grenades.Count);
                _demoCache.UpdatePayload(r, p => p.GrenadeInputCoverage = rows.Source.InputCoverage);
            });
            if (!GrenadeSidecar.DeleteLegacy(_demoCache, path))
            {
                GrenadeIndexLog.LegacyKept(Log, fileName);
            }
            _demoCache.SaveIndex();
            GrenadeIndexLog.Walked(Log, fileName, rows.Grenades.Count, rows.Source.InputCoverage);
            RaiseWalked(path);
            _post(() => Indexed?.Invoke(path));
        }
        catch (Exception ex)
        {
            GrenadeIndexLog.WalkFailed(Log, fileName, ex);
            try
            {
                _demoCache.UpdateExisting(path, r => r.MarkFailed(EvaluatorId));
            }
            catch (Exception)
            {
                // The row could not be marked; the next pass will try the demo again.
            }
        }
        finally
        {
            ClearForced(path);
        }
    }

    /// <summary>The flights of a demo this evaluator just walked, handed over once; null when there are none.</summary>
    /// <param name="path">The demo's path.</param>
    public IReadOnlyDictionary<string, List<TrajectoryPoint>>? TakeFlights(string path)
    {
        lock (_gate)
        {
            return _flights.Remove(path, out Dictionary<string, List<TrajectoryPoint>>? flights) ? flights : null;
        }
    }

    // A handler's throw must not mark a walk that was written as failed.
    private void RaiseWalked(string path)
    {
        try
        {
            Walked?.Invoke(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string fileName = Path.GetFileName(path);
            GrenadeIndexLog.WalkedHandlerFailed(Log, fileName, ex);
        }
    }

    private void ClearForced(string path)
    {
        lock (_gate)
        {
            _forcedPaths.Remove(path);
        }
    }
}

/// <summary>Source-generated log lines for the grenade walk evaluator.</summary>
internal static partial class GrenadeIndexLog
{
    /// <summary>Category (the "App" source tag) for grenade walk lines.</summary>
    public const string Category = "App.Grenades";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{fileName}: grenades were not walked")]
    public static partial void WalkFailed(ILogger logger, string fileName, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "{fileName}: {count} grenades walked, input coverage {coverage}")]
    public static partial void Walked(ILogger logger, string fileName, int count, double coverage);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "{fileName}: grenade rows ignored by the index (missing, unreadable or another demo's)")]
    public static partial void RowsIgnored(ILogger logger, string fileName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "grenade index loaded {demos} demos in {ms} ms")]
    public static partial void Loaded(ILogger logger, int demos, long ms);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "lineup clips: {line}")]
    public static partial void LineupClip(ILogger logger, string line);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "{fileName}: new grenade sidecars did not read back; pre-gzip files kept")]
    public static partial void LegacyKept(ILogger logger, string fileName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "{fileName}: parse carried no user commands; grenades left for a full parse")]
    public static partial void NoUserCommands(ILogger logger, string fileName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "the lineup store was not saved")]
    public static partial void LineupsNotSaved(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "{fileName}: a handler of the walked grenades failed")]
    public static partial void WalkedHandlerFailed(ILogger logger, string fileName, Exception exception);
}
