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
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     The one rules read and the one build that highlights and round facts share: every effective
///     ruleset, <c>round_facts</c> included, evaluated together. Highlights are stamped with
///     <see cref="Fingerprint" />, round facts with <see cref="RoundFactsIdentity" />, which folds that in.
/// </summary>
public sealed class MergedRulesBuild
{
    private static ILogger? _diagLog;

    // One bare run per held parse: the highlight scan and round facts read the same run on a retained entry.
    private readonly ConditionalWeakTable<ParsedDemo, AnalysisRun> _runs = new();
    private readonly Dictionary<int, HighlightConfigFingerprint.Result> _fingerprints = [];
    private readonly object _gate = new();
    private readonly Func<RuleConfigLoadResult> _load;
    private RuleConfigLoadResult? _rules;
    private string? _roundFactsSource;

    /// <summary>The shipped rules with the user's overlay.</summary>
    public MergedRulesBuild() : this(LoadShippedWithUserOverlay)
    {
    }

    /// <param name="load">Reads the rule set; called once until <see cref="Invalidate" />.</param>
    public MergedRulesBuild(Func<RuleConfigLoadResult> load)
    {
        _load = load;
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

    /// <summary>Every ruleset the build runs.</summary>
    public IReadOnlyList<RulesetDoc> Docs => Rules.Rulesets;

    /// <summary>The enabled <c>round_facts</c> ruleset (a user's same-id override wins), or null.</summary>
    public RulesetDoc? RoundFactsDoc => Rules.Rulesets.FirstOrDefault(r =>
        r.Enabled && string.Equals(r.Id, RoundFactsFingerprint.RulesetId, StringComparison.Ordinal));

    /// <summary>The merged set's identity at a tick rate. Throws when the set does not compose.</summary>
    public HighlightConfigFingerprint.Result Fingerprint(int tickRate)
    {
        RuleConfigLoadResult rules = Rules;
        lock (_gate)
        {
            if (ReferenceEquals(rules, _rules) && _fingerprints.TryGetValue(tickRate, out HighlightConfigFingerprint.Result? hit))
            {
                return hit;
            }
        }

        HighlightConfigFingerprint.Result result =
            HighlightConfigFingerprint.Compute(rules.Rulesets, tickRate, RulesHighlightHarvester.GotvProfileId);
        lock (_gate)
        {
            if (ReferenceEquals(rules, _rules))
            {
                _fingerprints[tickRate] = result;
            }
        }

        return result;
    }

    /// <summary>
    ///     What the round facts rows are stored under, before the schema is folded in. The engine's
    ///     fingerprint hashes highlight definitions only, so the <c>round_facts</c> source and the engine
    ///     version are added here; without them an edited buy threshold would never re-run.
    /// </summary>
    public string RoundFactsIdentity(int tickRate)
    {
        string highlights = Fingerprint(tickRate).Fingerprint;
        string source;
        lock (_gate)
        {
            source = _roundFactsSource ??= SourceIdentity(RoundFactsDoc);
        }

        string engine = typeof(DemoAnalysis).Assembly.GetName().Version?.ToString() ?? "";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{highlights}\n{source}\n{engine}")));
    }

    /// <summary>The bare run over a held parse, evaluated once however many consumers ask.</summary>
    public AnalysisRun BareRun(ParsedDemo parsed) => Run(parsed, false);

    /// <summary>The snapshot run a forced scan asks for. Cached like <see cref="BareRun" />, which it also serves.</summary>
    public AnalysisRun FullRun(ParsedDemo parsed) => Run(parsed, true);

    /// <summary>Drops the cached read so the next call re-reads the rules directories.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _rules = null;
            _roundFactsSource = null;
            _fingerprints.Clear();
            _runs.Clear();
        }
    }

    private AnalysisRun Run(ParsedDemo parsed, bool snapshots)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(parsed, out AnalysisRun? cached) && (!snapshots || cached.Snapshots is not null))
            {
                return cached;
            }
        }

        BuildResult build = ForwardDemoPass.Build(parsed, Docs);
        RulesetExclusionReport.Report(Log, build);
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, build, new AnalysisOptions { CaptureSnapshots = snapshots });
        lock (_gate)
        {
            _runs.AddOrUpdate(parsed, run);
        }

        return run;
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
