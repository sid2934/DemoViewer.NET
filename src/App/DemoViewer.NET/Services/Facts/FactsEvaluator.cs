#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     The core pass that stores the tables of every stamped ruleset (<see cref="StampedFacts" />) as library
///     facts. It reads them out of the visit's one merged rules run, the forward read's or the held parse's, and
///     writes each output's sidecar before the ruleset's stamp, so a crash between the two only re-runs.
///     <para>
///         A ruleset the engine left out of the build is stamped failed under its fingerprint: it reads as
///         <see cref="FactStatus.Failed" /> and is tried again only once the ruleset changes.
///     </para>
/// </summary>
public sealed class FactsEvaluator : IDemoEvaluator
{
    /// <summary>The pass id, and the queue owner tag.</summary>
    public const string EvaluatorId = HostIds.FactsPass;

    // The rules fingerprint depends on the tick rate; the backlog asks at the rate every supported demo records at.
    private const int BacklogTickRate = 64;

    private static ILogger? _diagLog;

    private readonly DemoCacheStore _cache;
    private readonly StampedFacts _facts;
    private readonly Action<Action> _post;

    /// <param name="cache">The demo cache the facts and their stamps live in.</param>
    /// <param name="facts">The stamped rulesets and their storage.</param>
    /// <param name="post">UI-thread marshal for <see cref="Updated" />; synchronous when null.</param>
    public FactsEvaluator(DemoCacheStore cache, StampedFacts facts, Action<Action>? post = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _post = post ?? (static action => action());
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger("App.Facts");

    /// <summary>A demo's facts were written; the argument is its path. Raised through the post delegate.</summary>
    public event Action<string>? Updated;

    /// <inheritdoc />
    public string Id => EvaluatorId;

    /// <inheritdoc />
    public bool ReadsUserCommands => false;

    /// <summary>A demo the library has parsed with a facet whose facts are missing or stale.</summary>
    public bool Wants(string path) =>
        _cache.TryGetIndex(path) is { ParseSchema: > 0 } entry
        && _facts.Facets().Any(f => _facts.Fingerprint(f.RulesetId, BacklogTickRate) is { } fp
                                    && StampedFacts.NeedsWrite(entry, f.RulesetId, fp));

    /// <summary>A demo the library has not parsed yet, while any facet is on: its facts follow the library's write.</summary>
    public bool WantsAfterUpstream(string path) =>
        _cache.TryGetIndex(path) is not { ParseSchema: > 0 } && _facts.Facets().Count > 0;

    /// <inheritdoc />
    public long OrderHint(string path) => _cache.TryGetIndex(path)?.ModifiedTicks ?? 0;

    /// <summary>The tables come out of the visit's rules run, so a forward read serves this pass.</summary>
    public ForwardNeeds? ForwardFor(string path) => ForwardNeeds.Rules;

    /// <inheritdoc />
    public void Evaluate(string path, ParsedDemo parsed) => Record(path, _facts.Rules.BareRun(parsed), parsed.TickRate);

    /// <inheritdoc />
    public void EvaluateForward(string path, ForwardDemoResult pass)
    {
        if (pass.Run is { } run)
        {
            Record(path, run, pass.Demo.TickRate);
        }
    }

    /// <summary>
    ///     Whether a read of the demo should record the ruleset's outputs: its facts need writing, or the library
    ///     has not parsed the demo yet, so nothing is known about them.
    /// </summary>
    /// <param name="path">The demo's path.</param>
    /// <param name="rulesetId">The stamped ruleset's id.</param>
    public bool Records(string path, string rulesetId)
    {
        if (_facts.Fingerprint(rulesetId, BacklogTickRate) is not { } fingerprint)
        {
            return false;
        }

        return _cache.TryGetIndex(path) is not { ParseSchema: > 0 } entry || StampedFacts.NeedsWrite(entry, rulesetId, fingerprint);
    }

    private void Record(string path, AnalysisRun run, int tickRate)
    {
        string fileName = Path.GetFileName(path);
        DemoCacheIndexEntry? entry = _cache.TryGetIndex(path);
        HashSet<string> recorded = new((run.Build.Outputs ?? []).Select(o => o.Id), StringComparer.Ordinal);
        Dictionary<string, MetricTable>? projected = null;
        List<PackStamp> stamps = [];
        foreach (StampedFacet facet in _facts.Facets())
        {
            string? fingerprint = _facts.Fingerprint(facet.RulesetId, tickRate);
            if (fingerprint is null || (entry is not null && !StampedFacts.NeedsWrite(entry, facet.RulesetId, fingerprint)))
            {
                continue;
            }

            try
            {
                if (run.Build.ExcludedRulesets.FirstOrDefault(r => string.Equals(r.Id, facet.RulesetId, StringComparison.Ordinal)) is { } excluded)
                {
                    string detail = string.Join("; ", excluded.Diagnostics.Take(5).Select(d => $"[{d.Code}] {d.Message}"));
                    FactsLog.Excluded(Log, facet.RulesetId, fileName, detail);

                    stamps.Add(new PackStamp(StampedFacts.StampId(facet.RulesetId), StampedFacts.Schema, fingerprint)
                    {
                        State = DemoAnalysisState.Failed,
                        ComputedAtTicks = DateTime.UtcNow.Ticks
                    });
                    continue;
                }

                // A table the read was told not to record is not an empty table; leave the facet for a read that records it.
                if (facet.Tables.Any(t => !recorded.Contains(t.Name)))
                {
                    continue;
                }

                projected ??= run.ProjectConfiguredOutputs().ToDictionary(t => t.Name, StringComparer.Ordinal);
                int rows = 0;
                foreach (TableDef table in facet.Tables)
                {
                    FactKey key = new(facet.RulesetId, table.Name);
                    FactTable facts = projected.TryGetValue(table.Name, out MetricTable? metric)
                        ? FactsCodec.FromMetric(metric, key, fingerprint, StampedFacts.Schema, table.Per ?? "")
                        : new FactTable(key, fingerprint, StampedFacts.Schema, table.Per ?? "", [], [], new Dictionary<string, string>(), []);
                    _cache.WriteSiblingBytes(path, StampedFacts.Suffix(key), FactsCodec.Encode(facts));
                    rows += facts.Rows.Count;
                }

                stamps.Add(new PackStamp(StampedFacts.StampId(facet.RulesetId), StampedFacts.Schema, fingerprint)
                {
                    Count = rows,
                    ComputedAtTicks = DateTime.UtcNow.Ticks
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One ruleset's write never stops the next one's.
                FactsLog.WriteFailed(Log, facet.RulesetId, fileName, ex);
            }
        }

        if (stamps.Count == 0)
        {
            return;
        }

        _cache.UpdateExisting(path, record =>
        {
            foreach (PackStamp stamp in stamps)
            {
                record.SetStamp(stamp);
            }
        });
        _cache.SaveIndex();
        _post(() => Updated?.Invoke(path));
    }
}

/// <summary>The facts pass's log seams.</summary>
internal static partial class FactsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "facts: ruleset {rulesetId} was left out of the build for {fileName}: {diagnostics}")]
    public static partial void Excluded(ILogger logger, string rulesetId, string fileName, string diagnostics);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "facts: writing {rulesetId} for {fileName} failed")]
    public static partial void WriteFailed(ILogger logger, string rulesetId, string fileName, Exception exception);
}
