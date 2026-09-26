#region

using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The evaluator that walks a demo's grenades (grenade-walk.md §3.8): an <see cref="IDemoEvaluator" />
///     on the same one-parse fan-out as the others, registered last because it reads nothing they write.
///     Per demo the work is one <see cref="GrenadeWalker.Walk" /> on the parse the queue already paid for,
///     the paths sibling then the rows sibling, then one record stamp, the stamp last so a crash between
///     them leaves "not walked" and never a stamp without both files.
///     <para>
///         <b>Background indexing is off by default</b> (D4, the <c>HighlightsSettings.BackgroundScan</c>
///         precedent: two to three seconds per demo is half an hour over a large library). Off, the demo
///         the user has open is still walked on the parse its open paid for, and any demo can be forced
///         from Match Overview at user priority. A Library tier-2 pass does not walk a demo on its own
///         parse while the opt-in is off: that would turn the sweep the user left off back on, the
///         Suggested Tags rule (correction 20).
///     </para>
///     <para>
///         A throw stamps <see cref="DemoAnalysisState.Failed" />, which the backlog excludes until the
///         user asks again; the parse itself failing is not this evaluator's to mark.
///     </para>
/// </summary>
public sealed class GrenadeIndexEvaluator : IDemoEvaluator
{
    /// <summary>The queue owner tag and the coordinator's id for this evaluator.</summary>
    public const string EvaluatorId = "grenades";

    private static ILogger? _diagLog;

    private readonly Func<bool> _backgroundIndex;
    private readonly DemoCacheStore _demoCache;
    private readonly HashSet<string> _forcedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Func<string?> _openDemo;
    private readonly Action<Action> _post;
    private readonly Func<int> _stride;

    // Test seam: the walk for a parse. Null is the real walker; a synthetic parse has no entities to walk.
    private readonly Func<ParsedDemo, GrenadeWalk>? _walk;

    /// <param name="demoCache">The unified demo cache: the stamp and the siblings live there.</param>
    /// <param name="backgroundIndex">The live <c>GrenadesSettings.BackgroundIndex</c>; forced paths and the open demo ignore it.</param>
    /// <param name="openDemo">The open demo's path, resolved at call time; null when none.</param>
    /// <param name="stride">The live <c>GrenadesSettings.TrajectoryStride</c>; null is the design's 4.</param>
    /// <param name="post">UI-thread marshal for <see cref="Indexed" />; defaults to synchronous.</param>
    /// <param name="walk">The walk to run; null walks the parse through <see cref="GrenadeWalker" />.</param>
    public GrenadeIndexEvaluator(
        DemoCacheStore demoCache,
        Func<bool> backgroundIndex,
        Func<string?>? openDemo = null,
        Func<int>? stride = null,
        Action<Action>? post = null,
        Func<ParsedDemo, GrenadeWalk>? walk = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(backgroundIndex);
        _demoCache = demoCache;
        _backgroundIndex = backgroundIndex;
        _openDemo = openDemo ?? (() => null);
        _stride = stride ?? (() => 4);
        _post = post ?? (action => action());
        _walk = walk;
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category);

    /// <summary>The coordinator, so a forced request can ask it to reconsider. Null in tests.</summary>
    public DemoEvaluationCoordinator? Coordinator { get; set; }

    /// <summary>True while the coordinator has a walk in flight.</summary>
    public bool IsIndexing => Coordinator?.HasOutstanding(EvaluatorId) ?? false;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <summary>A demo's grenades were written and stamped. Raised through the post delegate with its path.</summary>
    public event Action<string>? Indexed;

    /// <inheritdoc />
    /// <remarks>
    ///     From the index row alone: a parsed demo whose grenades are missing, stale under the walker
    ///     version or the schema, and not failed, with the sweep on or the demo forced.
    /// </remarks>
    public bool Wants(string path)
    {
        DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
        lock (_gate)
        {
            if (_forcedPaths.Contains(path))
            {
                return entry is { ParseSchema: > 0 } && !entry.IsGrenadesCurrent(GrenadeWalker.Version);
            }
        }

        return NeedsWalk(entry) && _backgroundIndex();
    }

    /// <inheritdoc />
    public DemoJobPriority PriorityFor(string path)
    {
        lock (_gate)
        {
            return _forcedPaths.Contains(path) ? DemoJobPriority.UserRequested : DemoJobPriority.Background;
        }
    }

    /// <inheritdoc />
    public long OrderHint(string path) => _demoCache.TryGetIndex(path)?.ModifiedTicks ?? 0;

    /// <inheritdoc />
    public void Evaluate(string path, ParsedDemo parsed) => Refresh(path, parsed);

    /// <inheritdoc />
    /// <remarks>
    ///     The open demo is walked on the parse its open paid for whatever the opt-in says (D4); any other
    ///     demo only when it would have been wanted.
    /// </remarks>
    public void OnParsedOpportunistically(string path, ParsedDemo parsed)
    {
        if (Wants(path) || (IsOpen(path) && NeedsWalk(_demoCache.TryGetIndex(path))))
        {
            Refresh(path, parsed);
        }
    }

    /// <inheritdoc />
    public void OnFailed(string path) => ClearForced(path);

    /// <summary>
    ///     The demos that would be submitted: the coordinator's candidate universe for this evaluator,
    ///     newest first. Derived from the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
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
    ///     Overview's "Index grenades". A failed row is lifted back to Pending first.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    public void Request(string path)
    {
        try
        {
            if (_demoCache.TryGetIndex(path) is { GrenadeState: DemoAnalysisState.Failed })
            {
                _demoCache.UpdateExisting(path, r => r.GrenadeState = DemoAnalysisState.Pending);
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

        Coordinator?.Consider(path);
    }

    private static bool NeedsWalk(DemoCacheIndexEntry? entry) =>
        entry is { ParseSchema: > 0 } && entry.NeedsGrenades(GrenadeWalker.Version);

    private bool IsOpen(string path) =>
        _openDemo() is { } open && string.Equals(open, path, StringComparison.OrdinalIgnoreCase);

    private void Refresh(string path, ParsedDemo parsed)
    {
        string fileName = Path.GetFileName(path);
        try
        {
            GrenadeWalk walk = _walk?.Invoke(parsed)
                               ?? GrenadeWalker.Walk(parsed, new GrenadeWalkOptions(TrajectoryStride: Math.Max(1, _stride())));
            DemoCacheRecord? record = _demoCache.TryLoadRecord(path);
            (GrenadeDocument rows, GrenadePathsDocument paths) = GrenadeSidecar.Build(path, record, parsed, walk);

            // Paths first, rows second, stamp last: a crash between any two leaves "not walked" or a file
            // nothing reads, never rows whose cards have no path to draw.
            _demoCache.WriteSibling(path, GrenadeSidecar.PathsSuffix, GrenadeSidecar.Serialize(paths));
            _demoCache.WriteSibling(path, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(rows));
            _demoCache.UpdateExisting(path, r =>
            {
                DemoCacheStore.StampGrenades(r);
                r.GrenadeState = DemoAnalysisState.Indexed;
                r.GrenadeCount = rows.Grenades.Count;
                r.GrenadeWalker = GrenadeWalker.Version;
                r.GrenadeInputCoverage = rows.Source.InputCoverage;
            });
            _demoCache.SaveIndex();
            GrenadeIndexLog.Walked(Log, fileName, rows.Grenades.Count, rows.Source.InputCoverage);
            _post(() => Indexed?.Invoke(path));
        }
        catch (Exception ex)
        {
            GrenadeIndexLog.WalkFailed(Log, fileName, ex);
            try
            {
                _demoCache.UpdateExisting(path, r => r.GrenadeState = DemoAnalysisState.Failed);
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
}
