#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoProcessing;
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
    private readonly ConditionalWeakTable<ParsedDemo, CachedRun> _runs = new();
    private readonly Dictionary<int, HighlightConfigFingerprint.Result> _fingerprints = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
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

    /// <summary>The stamped rulesets this build knows, with their owners and gates. One entry per id, the first registered.</summary>
    public IReadOnlyList<StampedRuleset> StampedRulesets => _stamped.Value;

    /// <summary>True once the stamped rulesets were read; reading them resolves the extensions' contributions.</summary>
    public bool StampedRulesetsRead => _stamped.IsValueCreated;

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
    public RulesetDoc? EnabledDoc(string rulesetId) => Docs.FirstOrDefault(r =>
        r.Enabled && string.Equals(r.Id, rulesetId, StringComparison.Ordinal));

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
    ///     One ruleset's own identity, for the outputs stamped under it. Composes the ruleset alone, so a
    ///     broken highlight file never blocks it, and throws when it does not compose or is not enabled.
    ///     The engine's fingerprint hashes highlight definitions only, which a ruleset without highlights
    ///     has none of, so the source and the engine version carry the identity.
    /// </summary>
    public string RulesetIdentity(string rulesetId, int tickRate)
    {
        RulesetDoc doc = EnabledDoc(rulesetId) ?? throw new InvalidOperationException($"no enabled {rulesetId} ruleset");
        string composed = HighlightConfigFingerprint.Compute([doc], tickRate, RulesHighlightHarvester.GotvProfileId).Fingerprint;
        string source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(rulesetId, out string? known))
            {
                known = SourceIdentity(doc);
                _sources[rulesetId] = known;
            }

            source = known;
        }

        string engine = typeof(DemoAnalysis).Assembly.GetName().Version?.ToString() ?? "";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{composed}\n{source}\n{engine}")));
    }

    /// <summary>
    ///     The bare run over a held parse, evaluated once however many consumers ask until
    ///     <see cref="Forget" />.
    /// </summary>
    public AnalysisRun BareRun(ParsedDemo parsed)
    {
        (string off, IReadOnlyList<RulesetDoc> docs) = MergedDocs();
        lock (_gate)
        {
            if (_runs.TryGetValue(parsed, out CachedRun? cached) && string.Equals(cached.Off, off, StringComparison.Ordinal))
            {
                return cached.Run;
            }
        }

        // A held parse is rare and shared by every pass on it, so it records every stamped output, and
        // round_facts whether or not this build stamps it.
        HashSet<string> outputs = [.. StampedOutputs(), ForwardDemoPass.RoundFactsTable];
        BuildResult build = ForwardDemoPass.Build(parsed, docs, outputs);
        RulesetExclusionReport.Report(Log, build);
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = false });
        lock (_gate)
        {
            _runs.AddOrUpdate(parsed, new CachedRun(off, run));
        }

        return run;
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
        RulesetExclusionReport.Report(Log, build);
        return DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = true });
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
            _fingerprints.Clear();
            _runs.Clear();
        }
    }

    private sealed record CachedRun(string Off, AnalysisRun Run);

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
