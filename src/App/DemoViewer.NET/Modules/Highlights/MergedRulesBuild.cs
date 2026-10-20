#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     The one rules read and the one bare build the background passes share: the highlight rulesets plus
///     every stamped ruleset that is on, evaluated together. Highlights are stamped with
///     <see cref="Fingerprint" />, which covers the highlight rulesets only; a stamped ruleset's outputs are
///     stamped with its own <see cref="RulesetIdentity" />, so neither an owner's toggle nor a broken stamped
///     ruleset moves the highlights stamp.
/// </summary>
public sealed class MergedRulesBuild
{
    private static ILogger? _diagLog;

    // One bare run per held parse: the highlight scan and round facts read the same run on a retained entry.
    // Keyed by the stamped rulesets that were off when it ran; a run from before a toggle is not the merged set's run.
    // Recorded is the requested cut, not the build's outputs: an excluded ruleset's table never appears there.
    // Collectors produce output only on the run whose options carried them, so they key the run too.
    private readonly ConditionalWeakTable<ParsedDemo, CachedRun> _runs = new();
    private readonly Dictionary<int, HighlightConfigFingerprint.Result> _fingerprints = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

    // Keyed by the doc instances it was composed from: a reload hands out new ones, and a gate change can drop a dependency.
    private readonly Dictionary<(string Id, int TickRate), (IReadOnlyList<RulesetDoc> Closure, string Identity)> _identities = [];
    private readonly HashSet<string> _reportedExclusions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<RuleConfigLoadResult> _load;
    private readonly Lazy<IReadOnlyList<StampedRuleset>> _stamped;
    private RuleConfigLoadResult? _rules;
    private IReadOnlyList<RulesetDoc>? _coreDocs;
    private (string Off, IReadOnlyList<RulesetDoc> Docs)? _merged;

    /// <summary>The shipped rules with the user's overlay and no stamped rulesets.</summary>
    public MergedRulesBuild() : this(LoadShippedWithUserOverlay)
    {
    }

    /// <summary>The shipped rules with the user's overlay and the <paramref name="stamped" /> rulesets.</summary>
    public MergedRulesBuild(Func<IReadOnlyList<StampedRuleset>> stamped) : this(LoadShippedWithUserOverlay, stamped)
    {
    }

    /// <param name="load">Reads the rule set; called once until <see cref="Invalidate" />.</param>
    /// <param name="stamped">
    ///     The stamped rulesets with their owners and gates; read once, on the first build. Null means there
    ///     are none, so every ruleset read is a highlight ruleset.
    /// </param>
    public MergedRulesBuild(Func<RuleConfigLoadResult> load, Func<IReadOnlyList<StampedRuleset>>? stamped = null)
    {
        _load = load;
        _stamped = new Lazy<IReadOnlyList<StampedRuleset>>(
            stamped is null ? () => [] : () => Distinct(stamped()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger("App.Highlights");

    private RuleConfigLoadResult Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules ??= _load();
            }
        }
    }

    /// <summary>
    ///     A fresh read of the layers this build loads, the extensions' rulesets among them. An open reads this, so
    ///     its run holds the same rulesets as the merged set and can seed its visit.
    /// </summary>
    public RuleConfigLoadResult ReadRules() => _load();

    /// <summary>The stamped rulesets this build knows, with their owners and gates. One entry per id, the first registered.</summary>
    public IReadOnlyList<StampedRuleset> StampedRulesets => _stamped.Value;

    /// <summary>
    ///     Every ruleset the background passes run now: the highlight rulesets plus the stamped ones that are
    ///     on, in the order the directories were read. Re-derived when a gate answer changes.
    /// </summary>
    public IReadOnlyList<RulesetDoc> Docs => MergedDocs().Docs;

    // The merged set under one gate snapshot, keyed by the ids that were off. Each gate is read once,
    // outside the lock: a gate may be any object, and the first read also resolves the contributions.
    private (string Off, IReadOnlyList<RulesetDoc> Docs) MergedDocs()
    {
        IReadOnlyList<StampedRuleset> stamped = StampedRulesets;
        HashSet<string>? off = null;
        StringBuilder? key = null;
        foreach (StampedRuleset ruleset in stamped)
        {
            if (!ruleset.Enabled())
            {
                (off ??= new HashSet<string>(StringComparer.Ordinal)).Add(ruleset.RulesetId);
                (key ??= new StringBuilder()).Append(ruleset.RulesetId).Append('\n');
            }
        }

        string offKey = key?.ToString() ?? "";
        RuleConfigLoadResult rules = Rules;
        lock (_gate)
        {
            if (ReferenceEquals(rules, _rules) && _merged is { } hit && string.Equals(hit.Off, offKey, StringComparison.Ordinal))
            {
                return hit;
            }

            IReadOnlyList<RulesetDoc> docs = off is null
                ? rules.Rulesets
                : [.. rules.Rulesets.Where(r => !off.Contains(r.Id))];
            if (ReferenceEquals(rules, _rules))
            {
                _merged = (offKey, docs);
            }

            return (offKey, docs);
        }
    }

    /// <summary>
    ///     Every ruleset that is not stamped, whatever the gates say: what the highlights fingerprint covers
    ///     and what the open demo's Stats run evaluates. An always-on stamped ruleset stays out too, so a
    ///     broken copy of it can never stop the fingerprint from composing.
    /// </summary>
    public IReadOnlyList<RulesetDoc> CoreDocs
    {
        get
        {
            IReadOnlyList<StampedRuleset> stamped = StampedRulesets;
            RuleConfigLoadResult rules = Rules;
            lock (_gate)
            {
                if (ReferenceEquals(rules, _rules) && _coreDocs is { } hit)
                {
                    return hit;
                }

                IReadOnlyList<RulesetDoc> docs = WithoutStampedRulesets(rules.Rulesets, stamped);
                if (ReferenceEquals(rules, _rules))
                {
                    _coreDocs = docs;
                }

                return docs;
            }
        }
    }

    /// <summary><paramref name="rulesets" /> minus every stamped ruleset, for a read made elsewhere.</summary>
    public IReadOnlyList<RulesetDoc> WithoutStampedRulesets(IReadOnlyList<RulesetDoc> rulesets)
    {
        ArgumentNullException.ThrowIfNull(rulesets);
        return WithoutStampedRulesets(rulesets, StampedRulesets);
    }

    /// <summary>True when <paramref name="rulesetId" /> is a stamped ruleset, on or off.</summary>
    /// <param name="rulesetId">The ruleset's id.</param>
    public bool IsStamped(string rulesetId) =>
        StampedRulesets.Any(r => string.Equals(r.RulesetId, rulesetId, StringComparison.Ordinal));

    /// <summary>The pass that reads a stamped ruleset's tables off the run, or null when they are library facts.</summary>
    /// <param name="rulesetId">The ruleset's id.</param>
    public string? ReaderOf(string rulesetId) =>
        StampedRulesets.FirstOrDefault(r => string.Equals(r.RulesetId, rulesetId, StringComparison.Ordinal))?.ReadBy;

    /// <summary>
    ///     The rows <paramref name="table" /> recorded on <paramref name="run" />, for a pass that reads the tables of
    ///     <paramref name="rulesets" />. Throws when no enabled one of them declares it, or the run did not record it.
    /// </summary>
    /// <param name="run">The visit's rules run.</param>
    /// <param name="table">The table's name.</param>
    /// <param name="rulesets">The rulesets the reader may read.</param>
    public IReadOnlyList<FactRow> Table(AnalysisRun run, string table, IReadOnlyList<string> rulesets)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rulesets);
        if (!rulesets.Any(id => EnabledDoc(id)?.Show?.Tables.Any(t => string.Equals(t.Name, table, StringComparison.Ordinal)) == true))
        {
            throw new InvalidOperationException($"table {table} is not declared by an enabled ruleset of the pass ({string.Join(", ", rulesets)})");
        }

        List<OutputDef> output = [.. run.Build.Outputs?.Where(o => string.Equals(o.Id, table, StringComparison.Ordinal)) ?? []];
        if (output.Count == 0)
        {
            throw new InvalidOperationException($"the run did not record table {table}");
        }

        MetricTable projected = (run with { Build = run.Build with { Outputs = output } }).ProjectConfiguredOutputs().Single();
        return
        [
            .. projected.Rows.Select(r => new FactRow(
                r.Dimensions.ToDictionary(kv => kv.Key, kv => FactsCodec.FromEngine(kv.Value), StringComparer.Ordinal),
                r.Values.ToDictionary(kv => kv.Key, kv => FactsCodec.FromEngine(kv.Value), StringComparer.Ordinal)))
        ];
    }

    /// <summary>
    ///     The configured outputs the stamped rulesets that are on declare (their <c>show: tables:</c>), limited to
    ///     those whose ruleset <paramref name="stale" /> answers true for. Null keeps every one.
    /// </summary>
    /// <param name="stale">Whether a stamped ruleset's stored outputs need writing, by its id.</param>
    public IReadOnlySet<string> StampedOutputs(Func<string, bool>? stale = null)
    {
        HashSet<string> outputs = new(StringComparer.Ordinal);
        IReadOnlyList<RulesetDoc> docs = Docs;
        foreach (StampedRuleset ruleset in StampedRulesets)
        {
            RulesetDoc? doc = docs.FirstOrDefault(d => d.Enabled && string.Equals(d.Id, ruleset.RulesetId, StringComparison.Ordinal));
            if (doc?.Show is not { } show || (stale is not null && !stale(ruleset.RulesetId)))
            {
                continue;
            }

            foreach (TableDef table in show.Tables)
            {
                outputs.Add(table.Name);
            }
        }

        return outputs;
    }

    /// <summary>
    ///     The enabled ruleset with this id in the merged set (a user's same-id override wins), or null
    ///     when there is none: not in the directories, disabled by an override, or stamped and off.
    /// </summary>
    public RulesetDoc? EnabledDoc(string rulesetId) => Enabled(Docs, rulesetId);

    /// <summary>
    ///     The core set's identity at a tick rate: what highlights are stamped with. Throws when the set
    ///     does not compose.
    /// </summary>
    public HighlightConfigFingerprint.Result Fingerprint(int tickRate)
    {
        IReadOnlyList<RulesetDoc> core = CoreDocs;
        lock (_gate)
        {
            if (ReferenceEquals(core, _coreDocs) && _fingerprints.TryGetValue(tickRate, out HighlightConfigFingerprint.Result? hit))
            {
                return hit;
            }
        }

        HighlightConfigFingerprint.Result result =
            HighlightConfigFingerprint.Compute(core, tickRate, RulesHighlightHarvester.GotvProfileId);
        lock (_gate)
        {
            if (ReferenceEquals(core, _coreDocs))
            {
                _fingerprints[tickRate] = result;
            }
        }

        return result;
    }

    /// <summary>
    ///     One ruleset's own identity, for the outputs stamped under it. Composes the ruleset with the rulesets
    ///     its <c>use:</c> reads and nothing else, so a broken highlight file never blocks it, and throws when
    ///     that does not compose or any of it is not enabled. The engine's fingerprint hashes highlight
    ///     definitions only, which a ruleset without highlights has none of, so the sources and the engine
    ///     version carry the identity. A ruleset without <c>use:</c> is its own closure, so its stamps never move
    ///     when another ruleset gains one.
    /// </summary>
    public string RulesetIdentity(string rulesetId, int tickRate)
    {
        IReadOnlyList<RulesetDoc> docs = Docs;
        RulesetDoc doc = Enabled(docs, rulesetId) ?? throw new InvalidOperationException($"no enabled {rulesetId} ruleset");
        List<RulesetDoc> closure = UseClosure(docs, doc);
        lock (_gate)
        {
            if (_identities.TryGetValue((rulesetId, tickRate), out (IReadOnlyList<RulesetDoc> Closure, string Identity) hit)
                && hit.Closure.SequenceEqual(closure, ReferenceEqualityComparer.Instance))
            {
                return hit.Identity;
            }
        }

        string composed = HighlightConfigFingerprint.Compute(closure, tickRate, RulesHighlightHarvester.GotvProfileId).Fingerprint;
        List<string> sources = new(closure.Count);
        lock (_gate)
        {
            foreach (RulesetDoc member in closure)
            {
                if (!_sources.TryGetValue(member.Id, out string? known))
                {
                    known = SourceIdentity(member);
                    _sources[member.Id] = known;
                }

                sources.Add(known);
            }
        }

        string source = string.Join("\n", sources);
        string engine = typeof(DemoAnalysis).Assembly.GetName().Version?.ToString() ?? "";
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{composed}\n{source}\n{engine}")));
        lock (_gate)
        {
            _identities[(rulesetId, tickRate)] = (closure, identity);
        }

        return identity;
    }

    /// <summary>
    ///     The identity of an enabled ruleset whose <c>use:</c> reaches one that is not enabled, or null when its
    ///     closure is whole or it is not enabled itself. <see cref="RulesetIdentity" /> has nothing to answer for
    ///     such a ruleset, but its outputs still want a stamp: the build leaves it out, and it should read as
    ///     failed until the dependency comes back. Covers its own source, the missing ids and the engine version.
    /// </summary>
    public string? MissingDependencyIdentity(string rulesetId)
    {
        IReadOnlyList<RulesetDoc> docs = Docs;
        if (Enabled(docs, rulesetId) is not { } doc)
        {
            return null;
        }

        List<string> missing = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Visit(doc);
        if (missing.Count == 0)
        {
            return null;
        }

        string source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(doc.Id, out string? known))
            {
                known = SourceIdentity(doc);
                _sources[doc.Id] = known;
            }

            source = known;
        }

        string engine = typeof(DemoAnalysis).Assembly.GetName().Version?.ToString() ?? "";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"missing:{string.Join(",", missing)}\n{source}\n{engine}")));

        void Visit(RulesetDoc current)
        {
            if (!seen.Add(current.Id))
            {
                return;
            }

            foreach (string id in current.Use)
            {
                if (Enabled(docs, id) is { } dependency)
                {
                    Visit(dependency);
                }
                else if (seen.Add(id))
                {
                    missing.Add(id);
                }
            }
        }
    }

    private static RulesetDoc? Enabled(IReadOnlyList<RulesetDoc> docs, string rulesetId) => docs.FirstOrDefault(r =>
        r.Enabled && string.Equals(r.Id, rulesetId, StringComparison.Ordinal));

    // The doc after every ruleset its use: reaches, each dependency before its readers: the set the engine
    // compiles it with. A dependency that is not enabled leaves the doc nothing to compose against.
    private static List<RulesetDoc> UseClosure(IReadOnlyList<RulesetDoc> docs, RulesetDoc doc)
    {
        if (doc.Use.Count == 0)
        {
            return [doc];
        }

        List<RulesetDoc> order = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Visit(doc);
        return order;

        void Visit(RulesetDoc current)
        {
            if (!seen.Add(current.Id))
            {
                return;
            }

            foreach (string id in current.Use)
            {
                Visit(Enabled(docs, id) ?? throw new InvalidOperationException($"{current.Id} uses {id}, which is not enabled"));
            }

            order.Add(current);
        }
    }

    /// <summary>
    ///     The configured outputs a read of the demo at a path records: the tables of the stamped rulesets whose
    ///     stored outputs are stale for it. The forward read and <see cref="BareRun(ParsedDemo, string)" /> both
    ///     cut by it. Null records every stamped output.
    /// </summary>
    public Func<string, IReadOnlySet<string>?>? OutputsFor { get; set; }

    /// <summary>
    ///     The collectors the visit of the demo at a path carries. A bare run declares them, and a cached run
    ///     is served only when it carried at least these. Null carries none.
    /// </summary>
    internal Func<string, CollectorSet>? CollectorsFor { get; set; }

    /// <summary>
    ///     Every collector an open's visit could carry. The visit is planned after the open's run, so a run
    ///     that seeds it declares all of them. Null declares none.
    /// </summary>
    internal Func<CollectorSet>? OpenCollectors { get; set; }

    /// <summary>
    ///     The guard of the extension with this id, or null. A contributed ruleset a build excludes is reported
    ///     through its owner's guard, once per ruleset content per session. Null reports nothing.
    /// </summary>
    internal Func<string, ExtensionGuard?>? GuardOf { get; set; }

    /// <summary>
    ///     The bare run over a held parse recording every stamped output, evaluated once however many consumers
    ///     ask until <see cref="Forget" />.
    /// </summary>
    public AnalysisRun BareRun(ParsedDemo parsed) => RunCut(parsed, StampedOutputs(), CollectorSet.Empty).Run!;

    /// <summary>
    ///     The bare run over a held parse of the demo at <paramref name="path" />, recording the cut
    ///     <see cref="OutputsFor" /> names. A cached run is served when it recorded at least that cut.
    /// </summary>
    public AnalysisRun BareRun(ParsedDemo parsed, string path) => RunFor(parsed, path).Run!;

    /// <summary>
    ///     What the collectors of the visit of <paramref name="path" /> put on its bare run, the run made first
    ///     when none that carried them is cached.
    /// </summary>
    internal CollectorResults Collected(ParsedDemo parsed, string path) =>
        RunCut(parsed, OutputsFor?.Invoke(path) ?? StampedOutputs(), CollectorsFor?.Invoke(path) ?? CollectorSet.Empty, false).Results;

    /// <summary>
    ///     True when an evaluated run is held for <paramref name="parsed" />, not just collector results. The
    ///     library reads its tables off such a run instead of replaying.
    /// </summary>
    internal bool HoldsRun(ParsedDemo parsed)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(parsed, out CachedRun? cached) && cached.Run is not null;
        }
    }

    private CachedRun RunFor(ParsedDemo parsed, string path) =>
        RunCut(parsed, OutputsFor?.Invoke(path) ?? StampedOutputs(), CollectorsFor?.Invoke(path) ?? CollectorSet.Empty);

    // A caller reading only collector results does not need round_facts recorded on the run it is served.
    private CachedRun RunCut(ParsedDemo parsed, IReadOnlySet<string> cut, CollectorSet collectors, bool readsRoundFacts = true)
    {
        (string off, IReadOnlyList<RulesetDoc> docs) = MergedDocs();

        // round_facts and the library tables always: their readers take them off this run, and a run without
        // them reads as a demo with no rows.
        HashSet<string> outputs = new(cut, StringComparer.Ordinal) { ForwardDemoPass.RoundFactsTable };
        outputs.UnionWith(LibraryTeamTables.Tables);
        lock (_gate)
        {
            if (_runs.TryGetValue(parsed, out CachedRun? cached) && string.Equals(cached.Off, off, StringComparison.Ordinal))
            {
                if (cached.Recorded.IsSupersetOf(readsRoundFacts ? outputs : cut) && cached.Collectors.IsSupersetOf(collectors))
                {
                    return cached;
                }

                // Widened, not swapped, so two callers with different cuts cannot evict each other.
                outputs.UnionWith(cached.Recorded);
                collectors = collectors.Union(cached.Collectors);
            }
        }

        BuildResult build = ForwardDemoPass.Build(parsed, docs, outputs, collectors);
        ReportExclusions(build);
        DecodeSites.Enter(2);
        (AnalysisOptions options, CollectorObserver? observer) = collectors.Declare(new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, options);
        CachedRun fresh = new(off, outputs, collectors, run, observer?.Results ?? CollectorResults.None);
        lock (_gate)
        {
            _runs.AddOrUpdate(parsed, fresh);
        }

        return fresh;
    }

    /// <summary>
    ///     What the open demo's run evaluates. When a stamped output is stale for <paramref name="path" /> and
    ///     <paramref name="read" /> holds the rulesets this build runs, the plan is <paramref name="read" /> minus
    ///     the stamped rulesets that are off, its <see cref="OpenRunPlan.Cut" /> is the stale outputs, and the run
    ///     can <see cref="Seed(ParsedDemo, string, OpenRunPlan, AnalysisRun)" /> the open's visit. Otherwise it is <paramref name="read" /> without the stamped
    ///     rulesets and an empty cut: nothing would read their nodes, and their snapshot columns cost peak memory.
    ///     With nothing stale it still declares the registered collectors, so their results can seed the visit.
    /// </summary>
    /// <param name="read">The rules the shell read for this open.</param>
    /// <param name="path">The open demo's path, or null when it has none on disk.</param>
    public OpenRunPlan PlanOpen(IReadOnlyList<RulesetDoc> read, string? path) =>
        PlanOpen(read, path, path is null ? null : StaleOutputs(path));

    /// <summary>The configured outputs a read of the demo at <paramref name="path" /> records: <see cref="OutputsFor" />, else every stamped one.</summary>
    /// <remarks>Asks the passes that read their own tables, which answer no on the UI thread: take it off that thread.</remarks>
    /// <param name="path">The demo's path.</param>
    public IReadOnlySet<string> StaleOutputs(string path) => OutputsFor?.Invoke(path) ?? StampedOutputs();

    /// <summary><see cref="PlanOpen(IReadOnlyList{RulesetDoc}, string)" /> with <see cref="StaleOutputs" /> already taken.</summary>
    /// <param name="read">The rules the shell read for this open.</param>
    /// <param name="path">The open demo's path, or null when it has none on disk.</param>
    /// <param name="staleOutputs">What <see cref="StaleOutputs" /> answered for <paramref name="path" />.</param>
    public OpenRunPlan PlanOpen(IReadOnlyList<RulesetDoc> read, string? path, IReadOnlySet<string>? staleOutputs)
    {
        ArgumentNullException.ThrowIfNull(read);
        RuleConfigLoadResult rules = Rules;
        (string off, IReadOnlyList<RulesetDoc> merged) = MergedDocs();
        HashSet<string> offIds = new(off.Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        IReadOnlyList<RulesetDoc> docs = offIds.Count == 0 ? read : [.. read.Where(r => !offIds.Contains(r.Id))];
        HashSet<string> stampedIds = new(StampedRulesets.Select(r => r.RulesetId), StringComparer.Ordinal);
        HashSet<string> stampedTables = new(StringComparer.Ordinal) { ForwardDemoPass.RoundFactsTable };
        foreach (RulesetDoc doc in docs)
        {
            if (stampedIds.Contains(doc.Id) && doc.Show is { } show)
            {
                stampedTables.UnionWith(show.Tables.Select(t => t.Name));
            }
        }

        OpenRunPlan plan = new(WithoutStampedRulesets(read), new HashSet<string>(StringComparer.Ordinal), stampedTables, stampedIds)
            { Rules = rules, Off = off };
        if (path is null)
        {
            return plan;
        }

        IReadOnlySet<string> stale = staleOutputs ?? StaleOutputs(path);
        if (stale.Count == 0)
        {
            // Core rulesets only, but the visit's collectors still ride this run instead of a second one.
            return plan with { Collectors = OpenCollectorSet() };
        }

        if (!SameRulesets(docs, merged))
        {
            MergedRulesBuildLog.OpenRulesDiffer(Log);
            return plan;
        }

        HashSet<string> cut = new(stale, StringComparer.Ordinal) { ForwardDemoPass.RoundFactsTable };
        cut.UnionWith(LibraryTeamTables.Tables);
        return plan with
        {
            Docs = docs,
            Cut = cut,
            Collectors = OpenCollectorSet()
        };
    }

    // A fault here leaves the open run without collectors; a visit needing one then makes its own run.
    private CollectorSet OpenCollectorSet()
    {
        try
        {
            return OpenCollectors?.Invoke() ?? CollectorSet.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MergedRulesBuildLog.OpenCollectorsFailed(Log, ex.Message);
            return CollectorSet.Empty;
        }
    }

    /// <summary>
    ///     Stores the open's run as the bare run of <paramref name="parsed" />, so the open's visit reads it
    ///     instead of evaluating again. Dropped when the rules, a gate or the cut moved since
    ///     <see cref="PlanOpen(IReadOnlyList{RulesetDoc}, string)" />. True when stored.
    /// </summary>
    public bool Seed(ParsedDemo parsed, string path, OpenRunPlan plan, AnalysisRun run) => Seed(parsed, path, plan, run, null);

    /// <summary>
    ///     <see cref="Seed(ParsedDemo, string, OpenRunPlan, AnalysisRun)" /> with what the run's observer
    ///     collected for <see cref="OpenRunPlan.Collectors" />. Without it the run is stored as carrying none.
    /// </summary>
    internal bool Seed(ParsedDemo parsed, string path, OpenRunPlan plan, AnalysisRun run, CollectorResults? collected)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(run);
        if (plan.Cut.Count == 0 && (collected is null || plan.Collectors.IsEmpty))
        {
            return false;
        }

        (string off, _) = MergedDocs();
        IReadOnlySet<string> wanted = OutputsFor?.Invoke(path) ?? StampedOutputs();
        string? reason = !string.Equals(off, plan.Off, StringComparison.Ordinal) ? "a stamped ruleset was turned on or off"
            : !plan.Cut.IsSupersetOf(wanted) ? "more outputs went stale"
            : null;
        lock (_gate)
        {
            if (reason is null && !ReferenceEquals(plan.Rules, _rules))
            {
                reason = "the rules were re-read";
            }

            if (reason is null)
            {
                _runs.AddOrUpdate(parsed, collected is null
                    ? new CachedRun(off, plan.Cut, CollectorSet.Empty, run, CollectorResults.None)
                    // An empty cut is served to collector readers only; the snapshot run is not kept for them.
                    : new CachedRun(off, plan.Cut, plan.Collectors, plan.Cut.Count == 0 ? null : run, collected));
                return true;
            }
        }

        MergedRulesBuildLog.SeedDropped(Log, reason);
        return false;
    }

    // Two reads hold the same rulesets: the same ids in the same order, each serializing alike. A doc
    // that cannot be serialized never matches.
    private static bool SameRulesets(IReadOnlyList<RulesetDoc> a, IReadOnlyList<RulesetDoc> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        try
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)
                    || !string.Equals(JsonSerializer.Serialize(a[i]), JsonSerializer.Serialize(b[i]), StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Drops the cached bare run. The queue calls it when every owner of a parse has run, the coordinator
    ///     after a fan-out, so an open demo does not keep the run for the session.
    /// </summary>
    public void Forget(ParsedDemo parsed)
    {
        lock (_gate)
        {
            _runs.Remove(parsed);
        }
    }

    /// <summary>
    ///     The snapshot run a forced scan asks for, over <see cref="CoreDocs" /> only: the scoreboard is
    ///     projected from these snapshots, and a stamped ruleset would add nodes and memory to both.
    /// </summary>
    public AnalysisRun FullRun(ParsedDemo parsed)
    {
        BuildResult build = DemoAnalysis.Build(parsed, CoreDocs);
        ReportExclusions(build);
        return DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = true });
    }

    /// <summary>
    ///     Logs every ruleset <paramref name="build" /> excluded, and reports each one an extension contributed
    ///     through its <see cref="GuardOf" />. A user's same-id override is the user's file, never the extension's.
    /// </summary>
    internal void ReportExclusions(BuildResult build)
    {
        RulesetExclusionReport.Report(Log, build);
        if (GuardOf is not { } guardOf || build.ExcludedRulesets.Count == 0)
        {
            return;
        }

        RuleConfigLoadResult rules = Rules;
        foreach (ExcludedRuleset excluded in build.ExcludedRulesets)
        {
            StampedRuleset? stamped = StampedRulesets.FirstOrDefault(r => string.Equals(r.RulesetId, excluded.Id, StringComparison.Ordinal));
            RulesetDoc? doc = rules.Rulesets.FirstOrDefault(r => string.Equals(r.Id, excluded.Id, StringComparison.Ordinal));
            if (stamped is null || doc is null || RuleLayers.SourceOf(doc) is not { } source)
            {
                continue;
            }

            // Every visit builds, so a report per build would trip the fault window on the third demo.
            lock (_gate)
            {
                if (!_reportedExclusions.Add(excluded.Id + "\n" + source))
                {
                    continue;
                }
            }

            guardOf(stamped.Owner)?.Report("ruleset " + excluded.Id,
                new InvalidOperationException(RulesetExclusionReport.Describe(excluded)));
        }
    }

    /// <summary>Drops the cached read so the next call re-reads the rules directories.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _rules = null;
            _coreDocs = null;
            _merged = null;
            _sources.Clear();
            _identities.Clear();
            _fingerprints.Clear();
            _runs.Clear();
        }
    }

    // Run is null only on an entry that recorded no round_facts, which RunCut serves to collector readers alone.
    private sealed record CachedRun(string Off, IReadOnlySet<string> Recorded, CollectorSet Collectors, AnalysisRun? Run, CollectorResults Results);

    // The first registration of an id wins: the composition root lists core's before any extension's.
    private static IReadOnlyList<StampedRuleset> Distinct(IReadOnlyList<StampedRuleset> stamped) =>
        [.. stamped.DistinctBy(r => r.RulesetId, StringComparer.Ordinal)];

    private static IReadOnlyList<RulesetDoc> WithoutStampedRulesets(IReadOnlyList<RulesetDoc> rulesets, IReadOnlyList<StampedRuleset> stamped)
    {
        if (stamped.Count == 0)
        {
            return rulesets;
        }

        HashSet<string> owned = new(stamped.Select(p => p.RulesetId), StringComparer.Ordinal);
        return [.. rulesets.Where(r => !owned.Contains(r.Id))];
    }

    // The file the effective doc was read from, the YAML an extension supplied, or the doc's JSON where
    // there is no readable file (Browser).
    private static string SourceIdentity(RulesetDoc? doc)
    {
        if (doc is null)
        {
            return "";
        }

        if (RuleLayers.SourceOf(doc) is { } contributed)
        {
            return contributed;
        }

        try
        {
            if (doc.Position.File is { } file && File.Exists(file))
            {
                return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            }
        }
        catch (Exception)
        {
            // fall through to the serialized doc
        }

        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc))));
        }
        catch (Exception)
        {
            return doc.Id;
        }
    }

    /// <summary>The shipped rules, then <paramref name="extensions" />, then the user's overlay (<see cref="RuleLayers" />).</summary>
    /// <param name="extensions">The rulesets the extensions contributed.</param>
    public static RuleConfigLoadResult LoadShippedExtensionsUser(IReadOnlyList<ContributedRuleset> extensions)
    {
        string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
        string? userDir = OperatingSystem.IsBrowser() ? null : RuleSetLocator.EnsureUserRulesDirectory(shippedDir);
        return RuleLayers.Load(shippedDir, userDir, extensions, Log);
    }

    private static RuleConfigLoadResult LoadShippedWithUserOverlay()
    {
        string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
        string? userDir = OperatingSystem.IsBrowser() ? null : RuleSetLocator.EnsureUserRulesDirectory(shippedDir);
        return YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
    }
}

/// <summary>
///     The open demo's rulesets from <see cref="MergedRulesBuild.PlanOpen(IReadOnlyList{RulesetDoc}, string)" />. <see cref="Cut" /> is the
///     stamped outputs its run records, empty when the run cannot seed the visit.
/// </summary>
/// <param name="Docs">The rulesets the open's run builds.</param>
/// <param name="Cut">The stamped tables to record.</param>
/// <param name="StampedTables">Every stamped table name, recorded or not.</param>
/// <param name="StampedIds">Every stamped ruleset id.</param>
public sealed record OpenRunPlan(
    IReadOnlyList<RulesetDoc> Docs,
    IReadOnlySet<string> Cut,
    IReadOnlySet<string> StampedTables,
    IReadOnlySet<string> StampedIds)
{
    internal RuleConfigLoadResult? Rules { get; init; }

    internal string? Off { get; init; }

    /// <summary>The collectors the run declares; set whenever a visit of the demo can read their results off it.</summary>
    internal CollectorSet Collectors { get; init; } = CollectorSet.Empty;

    /// <summary>The build's outputs cut to the core tables plus <see cref="Cut" />.</summary>
    public BuildResult Cuts(BuildResult build) =>
        build with { Outputs = build.Outputs?.Where(o => !StampedTables.Contains(o.Id) || Cut.Contains(o.Id)).ToList() };

    /// <summary>The run as the core rulesets alone would show it: no stamped table and no stamped highlight.</summary>
    public AnalysisRun Shown(AnalysisRun run) => run with
    {
        Build = run.Build with { Outputs = run.Build.Outputs?.Where(o => !StampedTables.Contains(o.Id)).ToList() },
        Highlights = [.. run.Highlights.Where(h => !StampedIds.Contains(h.RulesetId))]
    };
}

/// <summary>The merged build's log seams.</summary>
internal static partial class MergedRulesBuildLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "rules: the open's read differs from the background read, so its visit runs rules on its own")]
    public static partial void OpenRulesDiffer(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "rules: the open's run was not handed to its visit: {reason}")]
    public static partial void SeedDropped(ILogger logger, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "rules: the open's run declares no collectors, the registered set could not be read: {reason}")]
    public static partial void OpenCollectorsFailed(ILogger logger, string reason);
}
