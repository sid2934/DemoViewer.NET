#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.DemoCache;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The pass that writes round facts: an <see cref="IExtensionPass" /> on the demo's visit, so it runs on
///     the parse the visit already holds and costs no second parse. It runs the merged rules on that parse
///     and reads the <c>round_facts</c> table out of the run through its <see cref="IRoundFactsRowSource" />,
///     projects it onto the record and stores the rows in the pack's payload, stamped under the
///     <see cref="RoundFactsFingerprint" /> (<see cref="RoundFactsRecords.SetRoundFacts" />).
///     <para>
///         Registered after the highlight scanner and before the round index:
///         the index reads these rows in the same pass.
///     </para>
///     <para>
///         Gated by the pack alone: off, it wants nothing, lists nothing pending and writes nothing. A user who disables the ruleset (a same-id override with
///         <c>enabled: false</c>) removes it from the effective set; the identity then answers null, and
///         the evaluator wants nothing either way.
///     </para>
/// </summary>
public sealed class RoundFactsEvaluator : IExtensionPass
{
    /// <summary>The queue owner tag and the pass id for this evaluator.</summary>
    public const string EvaluatorId = "roundfacts";

    // The rules fingerprint is tick-rate dependent and the backlog spans demos of several rates. 64 is
    // what every supported CS2 demo records at, the same probe the highlight scanner uses.
    private const int BacklogTickRate = 64;

    private static ILogger? _diagLog;

    private readonly DemoCacheStore _demoCache;
    private readonly Func<bool> _enabled;
    private readonly IRoundFactsRulesetIdentity _identity;
    private readonly Action<Action> _post;
    private readonly IRoundFactsRowSource _rows;

    private int _reportedAbsent;

    // Demos whose engine run produced no rows under a fingerprint, this session only. Nothing is written
    // (an empty payload would read as current), so without this the scheduler re-parses them forever.
    private readonly HashSet<string> _noRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _noRowsGate = new();

    /// <param name="demoCache">The unified demo cache: the rows live in its Analysis tier.</param>
    /// <param name="rows">The engine seam that evaluates the ruleset on a held parse.</param>
    /// <param name="identity">The effective ruleset's fingerprint, or null when there is none.</param>
    /// <param name="post">UI-thread marshal for <see cref="Updated" />; defaults to synchronous.</param>
    /// <param name="enabled">The owning pack's live gate; null means always on.</param>
    public RoundFactsEvaluator(
        DemoCacheStore demoCache,
        IRoundFactsRowSource rows,
        IRoundFactsRulesetIdentity identity,
        Action<Action>? post = null,
        Func<bool>? enabled = null)
    {
        _demoCache = demoCache;
        _rows = rows;
        _identity = identity;
        _post = post ?? (action => action());
        _enabled = enabled ?? (() => true);
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundFactsLog.Category);

    /// <summary>A demo's rows were (re)written. Raised through the post delegate.</summary>
    public event Action<string>? Updated;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <inheritdoc />
    public bool ReadsUserCommands => false;

    /// <summary>Whether the demo needs this pass now.</summary>
    /// <remarks>
    ///     Interested in a demo the Library has already parsed whose rows are missing or were written
    ///     under another fingerprint. A demo the cache has never seen is the Library's to parse first; this
    ///     pass joins that visit through <see cref="WantsAfterUpstream" /> and runs once the Library has written.
    /// </remarks>
    public bool Wants(string path)
    {
        if (!_enabled())
        {
            return false;
        }

        DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
        string? fingerprint = TryFingerprint(BacklogTickRate);
        return entry is { ParseSchema: > 0 } && entry.NeedsRoundFacts(fingerprint) && !TriedWithoutRows(path, fingerprint);
    }

    /// <summary>Whether the demo will need this pass once the pass it runs after has written.</summary>
    /// <remarks>A demo the Library has not parsed yet: its rows can only be written once that parse has landed.</remarks>
    public bool WantsAfterUpstream(string path) =>
        _enabled() && TryFingerprint(BacklogTickRate) is not null && _demoCache.TryGetIndex(path) is not { ParseSchema: > 0 };

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

    /// <summary>
    ///     Rewrites a demo's index row from its record: for a row whose stamp claims rows the record does not
    ///     hold, so the stamp goes and the demo is wanted again. Nothing happens for a demo with no record.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    public void ReprojectRow(string path)
    {
        if (_demoCache.TryLoadRecord(path) is { } record)
        {
            _demoCache.Upsert(record);
            _demoCache.SaveIndex();
        }
    }

    /// <summary>
    ///     The demos whose rows are missing or stale under the current fingerprint, for the pending counts.
    ///     Derived from the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
        if (!_enabled())
        {
            return [];
        }

        string? fingerprint = TryFingerprint(BacklogTickRate);
        if (fingerprint is null)
        {
            return [];
        }

        return
        [
            .. _demoCache.Index
                .Where(e => e.ParseSchema > 0 && e.NeedsRoundFacts(fingerprint) && !TriedWithoutRows(e.Path, fingerprint))
                .OrderByDescending(e => e.ModifiedTicks)
                .Select(e => e.Path)
        ];
    }

    private void Refresh(string path, ParsedDemo parsed) =>
        Refresh(path, parsed.TickRate, () => _rows.Rows(parsed), () => ClipRounds.Derive(parsed),
            () => FrameClock.IdentityFor(parsed));

    private void Refresh(string path, int tickRate, Func<RoundFactsTable> rowsOf,
        Func<IReadOnlyList<ClipRound>> roundsOf, Func<ClockIdentity> clockOf)
    {
        if (!_enabled())
        {
            return;
        }

        string fileName = Path.GetFileName(path);
        try
        {
            string? fingerprint = TryFingerprint(tickRate);
            if (fingerprint is null)
            {
                // No ruleset to run. Said once per process: with the ruleset disabled every tier-2 pass lands here.
                if (Interlocked.Exchange(ref _reportedAbsent, 1) == 0)
                {
                    RoundFactsLog.RulesetAbsent(Log, EngineRoundFactsRowSource.NoRulesetDiagnostic);
                }

                return;
            }

            DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
            if (entry is not null && !entry.NeedsRoundFacts(fingerprint))
            {
                return;
            }

            RoundFactsTable table = rowsOf();
            foreach (string diagnostic in table.Diagnostics)
            {
                RoundFactsLog.SourceDiagnostic(Log, fileName, diagnostic);
            }

            // A source with no rows has nothing to say about this demo; writing an empty payload would
            // mark it current and hide that the engine never ran.
            if (table.Rows.Count == 0)
            {
                lock (_noRowsGate)
                {
                    _noRows.Add(NoRowsKey(path, fingerprint));
                }

                RoundFactsLog.NoRows(Log, fileName);
                return;
            }

            RoundFactsRows rows = RoundFactsProjection.Project(roundsOf(), table);
            rows.Clock = RoundFactsClock.From(clockOf());
            foreach (string warning in rows.Warnings)
            {
                RoundFactsLog.ProjectionWarning(Log, fileName, warning);
            }

            _demoCache.UpdateExisting(path, record =>
            {
                rows.DemoSha256 = record.Sha256;
                _demoCache.SetRoundFacts(record, rows, fingerprint);
            });
            _demoCache.SaveIndex();
            _post(() => Updated?.Invoke(path));
        }
        catch (Exception ex)
        {
            // Isolated like every evaluator: the parse and the other evaluators are not this one's to fail.
            RoundFactsLog.WriteFailed(Log, fileName, ex);
        }
    }

    private bool TriedWithoutRows(string path, string? fingerprint)
    {
        lock (_noRowsGate)
        {
            return _noRows.Contains(NoRowsKey(path, fingerprint));
        }
    }

    private static string NoRowsKey(string path, string? fingerprint) => fingerprint + "|" + path;

    // A config that cannot load yields null, which reads as "nothing to run": one broken rule file must
    // never mark the whole library stale.
    private string? TryFingerprint(int tickRate)
    {
        try
        {
            return _identity.Fingerprint(tickRate);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
