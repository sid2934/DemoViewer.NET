#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The evaluator that writes round facts: an <see cref="IDemoEvaluator" /> on the tier-2 fan-out, so
///     it runs on the Library's held parse and costs no second parse. It evaluates ONLY the
///     <c>round_facts</c> ruleset through its <see cref="IRoundFactsRowSource" />, projects the table onto
///     the record and stores the rows in the Analysis tier under <see cref="RoundFactsFingerprint" />.
///     <para>
///         Registered after the highlight scanner and before the round index (overview correction 19):
///         the index reads these rows in the same pass.
///     </para>
///     <para>
///         Nothing here is gated by a setting: the rows ride tier 2, and wherever a parse runs this
///         runs. A user who disables the ruleset (a same-id override with <c>enabled: false</c>) removes
///         it from the effective set; the identity then answers null, and this evaluator wants nothing
///         and writes nothing.
///     </para>
/// </summary>
public sealed class RoundFactsEvaluator : IDemoEvaluator
{
    /// <summary>The queue owner tag and the coordinator's id for this evaluator.</summary>
    public const string EvaluatorId = "roundfacts";

    // The rules fingerprint is tick-rate dependent and the backlog spans demos of several rates. 64 is
    // what every supported CS2 demo records at, the same probe the highlight scanner uses.
    private const int BacklogTickRate = 64;

    private static ILogger? _diagLog;

    private readonly DemoCacheStore _demoCache;
    private readonly IRoundFactsRulesetIdentity _identity;
    private readonly Action<Action> _post;
    private readonly IRoundFactsRowSource _rows;

    private int _reportedAbsent;

    /// <param name="demoCache">The unified demo cache: the rows live in its Analysis tier.</param>
    /// <param name="rows">The engine seam that evaluates the ruleset on a held parse.</param>
    /// <param name="identity">The effective ruleset's fingerprint, or null when there is none.</param>
    /// <param name="post">UI-thread marshal for <see cref="Updated" />; defaults to synchronous.</param>
    public RoundFactsEvaluator(
        DemoCacheStore demoCache,
        IRoundFactsRowSource rows,
        IRoundFactsRulesetIdentity identity,
        Action<Action>? post = null)
    {
        _demoCache = demoCache;
        _rows = rows;
        _identity = identity;
        _post = post ?? (action => action());
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger(RoundFactsLog.Category);

    /// <summary>A demo's rows were (re)written. Raised through the post delegate.</summary>
    public event Action<string>? Updated;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <inheritdoc />
    /// <remarks>
    ///     Interested in a demo the Library has already parsed whose rows are missing or were written
    ///     under another fingerprint. A demo the cache has never seen is the Library's to parse first;
    ///     its tier-2 fan-out reaches <see cref="OnParsedOpportunistically" /> on that parse.
    /// </remarks>
    public bool Wants(string path)
    {
        DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(path);
        return entry is { ParseSchema: > 0 } && entry.NeedsRoundFacts(TryFingerprint(BacklogTickRate));
    }

    /// <inheritdoc />
    public long OrderHint(string path) => _demoCache.TryGetIndex(path)?.ModifiedTicks ?? 0;

    /// <inheritdoc />
    public void Evaluate(string path, ParsedDemo parsed) => Refresh(path, parsed);

    /// <inheritdoc />
    /// <remarks>
    ///     Not gated on <see cref="Wants" />: the Library's tier-2 slot fans out BEFORE it writes the
    ///     record, so the row this refreshes may not exist yet. The common fresh case costs one
    ///     fingerprint compare.
    /// </remarks>
    public void OnParsedOpportunistically(string path, ParsedDemo parsed) => Refresh(path, parsed);

    /// <summary>
    ///     The demos whose rows are missing or stale under the current fingerprint: the coordinator's
    ///     candidate universe for this evaluator. Derived from the index, never stored.
    /// </summary>
    public IReadOnlyList<string> PendingPaths()
    {
        string? fingerprint = TryFingerprint(BacklogTickRate);
        if (fingerprint is null)
        {
            return [];
        }

        return
        [
            .. _demoCache.Index
                .Where(e => e.ParseSchema > 0 && e.NeedsRoundFacts(fingerprint))
                .OrderByDescending(e => e.ModifiedTicks)
                .Select(e => e.Path)
        ];
    }

    private void Refresh(string path, ParsedDemo parsed)
    {
        string fileName = Path.GetFileName(path);
        try
        {
            string? fingerprint = TryFingerprint(parsed.TickRate);
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

            RoundFactsTable table = _rows.Rows(parsed);
            foreach (string diagnostic in table.Diagnostics)
            {
                RoundFactsLog.SourceDiagnostic(Log, fileName, diagnostic);
            }

            // A source with no rows has nothing to say about this demo; writing an empty payload would
            // mark it current and hide that the engine never ran.
            if (table.Rows.Count == 0)
            {
                return;
            }

            RoundFactsRows rows = RoundFactsProjection.Project(ClipRounds.Derive(parsed), table);
            rows.Clock = RoundFactsClock.From(FrameClock.IdentityFor(parsed));
            foreach (string warning in rows.Warnings)
            {
                RoundFactsLog.ProjectionWarning(Log, fileName, warning);
            }

            _demoCache.UpdateExisting(path, record =>
            {
                rows.DemoSha256 = record.Sha256;
                record.RoundFacts = rows;
                record.RoundFactsFingerprint = fingerprint;
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
