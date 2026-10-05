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
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     The one rules read and the one bare build the background passes share: the core rulesets plus
///     every pack-owned ruleset whose pack is on, evaluated together. Highlights are stamped with
///     <see cref="Fingerprint" />, which covers the core rulesets only; a pack stamps its rows with
///     <see cref="RulesetIdentity" /> of its own ruleset, so a pack toggle moves neither.
/// </summary>
public sealed class MergedRulesBuild
{
    private static ILogger? _diagLog;

    // One bare run per held parse: the highlight scan and round facts read the same run on a retained entry.
    // Stamped with the gate mask it ran under; a run from before a pack toggle is not the merged set's run.
    private readonly ConditionalWeakTable<ParsedDemo, CachedRun> _runs = new();
    private readonly Dictionary<int, HighlightConfigFingerprint.Result> _fingerprints = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<RuleConfigLoadResult> _load;
    private readonly Lazy<IReadOnlyList<GatedRuleset>> _packRulesets;
    private RuleConfigLoadResult? _rules;
    private IReadOnlyList<RulesetDoc>? _coreDocs;
    private (ulong Mask, IReadOnlyList<RulesetDoc> Docs)? _merged;

    /// <summary>The shipped rules with the user's overlay and no pack-owned rulesets.</summary>
    public MergedRulesBuild() : this(LoadShippedWithUserOverlay)
    {
    }

    /// <summary>The shipped rules with the user's overlay, gated by <paramref name="packRulesets" />.</summary>
    public MergedRulesBuild(Func<IReadOnlyList<GatedRuleset>> packRulesets) : this(LoadShippedWithUserOverlay, packRulesets)
    {
    }

    /// <param name="load">Reads the rule set; called once until <see cref="Invalidate" />.</param>
    /// <param name="packRulesets">
    ///     The pack-owned rulesets and their gates; read once, on the first build. Null means no pack owns
    ///     any ruleset, so the merged set is the whole read.
    /// </param>
    public MergedRulesBuild(Func<RuleConfigLoadResult> load, Func<IReadOnlyList<GatedRuleset>>? packRulesets = null)
    {
        _load = load;
        _packRulesets = new Lazy<IReadOnlyList<GatedRuleset>>(
            packRulesets is null ? () => [] : packRulesets, LazyThreadSafetyMode.ExecutionAndPublication);
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

    /// <summary>The pack-owned rulesets this build knows, gated by their owners.</summary>
    public IReadOnlyList<GatedRuleset> PackRulesets
    {
        get
        {
            IReadOnlyList<GatedRuleset> packs = _packRulesets.Value;
            // The gate snapshot is one bit per pack ruleset in a ulong.
            if (packs.Count > 64)
            {
                throw new InvalidOperationException($"{packs.Count} pack-owned rulesets; the gate mask holds 64.");
            }

            return packs;
        }
    }

    /// <summary>
    ///     Every ruleset the background passes run now: the core rulesets plus the pack-owned ones whose
    ///     pack is on, in the order the directories were read. Re-derived when a gate answer changes.
    /// </summary>
    public IReadOnlyList<RulesetDoc> Docs => MergedDocs().Docs;

    // The merged set under one gate snapshot, with the snapshot it was derived from. Each gate is read
    // once, outside the lock: a gate may be any object, and the first read also resolves the contributions.
    private (ulong Mask, IReadOnlyList<RulesetDoc> Docs) MergedDocs()
    {
        IReadOnlyList<GatedRuleset> packs = PackRulesets;
        ulong mask = 0;
        HashSet<string>? off = null;
        for (int i = 0; i < packs.Count; i++)
        {
            if (packs[i].Enabled())
            {
                mask |= 1UL << i;
            }
            else
            {
                (off ??= new HashSet<string>(StringComparer.Ordinal)).Add(packs[i].RulesetId);
            }
        }

        RuleConfigLoadResult rules = Rules;
        lock (_gate)
        {
            if (ReferenceEquals(rules, _rules) && _merged is { } hit && hit.Mask == mask)
            {
                return hit;
            }

            IReadOnlyList<RulesetDoc> docs = off is null
                ? rules.Rulesets
                : [.. rules.Rulesets.Where(r => !off.Contains(r.Id))];
            if (ReferenceEquals(rules, _rules))
            {
                _merged = (mask, docs);
            }

            return (mask, docs);
        }
    }

    /// <summary>
    ///     The rulesets no pack owns, whatever the gates say: what the highlights fingerprint covers and
    ///     what the open demo's Stats run evaluates.
    /// </summary>
    public IReadOnlyList<RulesetDoc> CoreDocs
    {
        get
        {
            IReadOnlyList<GatedRuleset> packs = PackRulesets;
            RuleConfigLoadResult rules = Rules;
            lock (_gate)
            {
                if (ReferenceEquals(rules, _rules) && _coreDocs is { } hit)
                {
                    return hit;
                }

                IReadOnlyList<RulesetDoc> docs = WithoutPackRulesets(rules.Rulesets, packs);
                if (ReferenceEquals(rules, _rules))
                {
                    _coreDocs = docs;
                }

                return docs;
            }
        }
    }

    /// <summary><paramref name="rulesets" /> minus every pack-owned ruleset, for a read made elsewhere.</summary>
    public IReadOnlyList<RulesetDoc> WithoutPackRulesets(IReadOnlyList<RulesetDoc> rulesets)
    {
        ArgumentNullException.ThrowIfNull(rulesets);
        return WithoutPackRulesets(rulesets, PackRulesets);
    }

    /// <summary>
    ///     Whether the owner of a ruleset is on: true for a ruleset no pack claims, otherwise the claiming
    ///     pack's gate. Independent of the user's overlay, so a reader can hide an owner's rows while its
    ///     writer's own gate reads the same answer.
    /// </summary>
    public bool IsOwnerOn(string rulesetId) =>
        PackRulesets.FirstOrDefault(p => string.Equals(p.RulesetId, rulesetId, StringComparison.Ordinal))?.Enabled() ?? true;

    /// <summary>
    ///     The enabled ruleset with this id in the merged set (a user's same-id override wins), or null
    ///     when there is none: not in the directories, disabled by an override, or owned by a pack that is off.
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
    ///     One ruleset's own identity, for rows a pack stores under it. Composes the ruleset alone, so a
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
        (ulong mask, IReadOnlyList<RulesetDoc> docs) = MergedDocs();
        lock (_gate)
        {
            if (_runs.TryGetValue(parsed, out CachedRun? cached) && cached.Mask == mask)
            {
                return cached.Run;
            }
        }

        BuildResult build = ForwardDemoPass.Build(parsed, docs);
        RulesetExclusionReport.Report(Log, build);
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = false });
        lock (_gate)
        {
            _runs.AddOrUpdate(parsed, new CachedRun(mask, run));
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
    ///     The snapshot run a forced scan asks for, over the core rulesets only: the scoreboard is
    ///     projected from these snapshots, and a pack's index-time ruleset would add nodes and memory to both.
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

    private sealed record CachedRun(ulong Mask, AnalysisRun Run);

    private static IReadOnlyList<RulesetDoc> WithoutPackRulesets(IReadOnlyList<RulesetDoc> rulesets, IReadOnlyList<GatedRuleset> packs)
    {
        if (packs.Count == 0)
        {
            return rulesets;
        }

        HashSet<string> owned = new(packs.Select(p => p.RulesetId), StringComparer.Ordinal);
        return [.. rulesets.Where(r => !owned.Contains(r.Id))];
    }

    // The file the effective doc was read from; the doc's JSON where there is no readable file (Browser).
    private static string SourceIdentity(RulesetDoc? doc)
    {
        if (doc is null)
        {
            return "";
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

    private static RuleConfigLoadResult LoadShippedWithUserOverlay()
    {
        string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
        string? userDir = OperatingSystem.IsBrowser() ? null : RuleSetLocator.EnsureUserRulesDirectory(shippedDir);
        return YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
    }
}
