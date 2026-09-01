#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     What the highlight scanner needs from the rules/analysis stack: abstracted so the
///     scanner's queue/staleness logic is testable without demos or rule files.
/// </summary>
public interface IHighlightHarvester
{
    /// <summary>
    ///     The A2 fingerprint for the CURRENT rule config at a demo's tick rate (fingerprints are
    ///     tickRate/profile-dependent: no global value). Cheap: YAML load + compose + hash, no
    ///     parse, no graph.
    /// </summary>
    (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate);

    /// <summary>
    ///     Build + bare evaluate (no snapshots: the only affordable scan mode). Returns the run
    ///     whose <c>Highlights</c> carry the A1 emission.
    /// </summary>
    AnalysisRun RunBareAnalysis(ParsedDemo demo);

    /// <summary>
    ///     Build + evaluate WITH snapshots: everything <see cref="RunBareAnalysis" /> produces, plus the
    ///     per-message state vectors the per-player stat projectors read.
    ///     <para>
    ///         Reserved for demos the user explicitly asked about (<c>Compute full stats</c>), never the
    ///         background sweep: snapshot mode is the expensive mode, and being snapshot-free is exactly what
    ///         makes a library-wide scan affordable. It costs what opening the demo costs, which is what the
    ///         user just asked for.
    ///     </para>
    ///     <para>
    ///         Defaulted to the bare run so existing implementations, and every test fake, stay valid. A
    ///         harvester that does not override it simply yields no scoreboard, which the caller handles as
    ///         "this run produced no stats" rather than as an error.
    ///     </para>
    /// </summary>
    AnalysisRun RunFullAnalysis(ParsedDemo demo) => RunBareAnalysis(demo);

    /// <summary>Drops the cached rule config (Authoring Workbench save trigger).</summary>
    void InvalidateRules();
}

/// <summary>
///     The real harvester: the same shipped+user-overlay rule load the Analysis tab uses
///     (<c>AnalysisViewModel.BuildFromConfig</c>), cached until invalidated.
/// </summary>
public sealed class RulesHighlightHarvester : IHighlightHarvester
{
    /// <summary>
    ///     The composition profile id: the builder's GOTV profile (GOTV is the only supported
    ///     demo source; multi-source support is deferred). Must match
    ///     <c>RuleChainBuilder.Profile.GetType().Name</c> or fingerprints diverge from builds.
    /// </summary>
    public const string GotvProfileId = "Cs2GotvProfile";

    private static ILogger? _diagLog;

    private readonly object _gate = new();
    private RuleConfigLoadResult? _rules;

    // Static, like the queue's: the harvester is constructed per scan and the seam it reads is
    // ambient and process-wide, so an instance field would just re-resolve the same logger.
    private static ILogger HarvestLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.Highlights");

    private RuleConfigLoadResult Rules
    {
        get
        {
            lock (_gate)
            {
                if (_rules is null)
                {
                    string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
                    string? userDir = OperatingSystem.IsBrowser()
                        ? null
                        : RuleSetLocator.EnsureUserRulesDirectory(shippedDir);
                    _rules = YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
                }

                return _rules;
            }
        }
    }

    /// <inheritdoc />
    public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
    {
        HighlightConfigFingerprint.Result result =
            HighlightConfigFingerprint.Compute(Rules.Rulesets, tickRate, GotvProfileId);
        return (result.Fingerprint, result.HighlightHashes);
    }

    /// <inheritdoc />
    public AnalysisRun RunBareAnalysis(ParsedDemo demo)
    {
        RuleConfigLoadResult rules = Rules;
        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);
        RulesetExclusionReport.Report(HarvestLog, build);
        return DemoAnalysis.Evaluate(
            demo,
            build,
            new AnalysisOptions
            {
                CaptureSnapshots = false
            });
    }

    /// <inheritdoc />
    public AnalysisRun RunFullAnalysis(ParsedDemo demo)
    {
        RuleConfigLoadResult rules = Rules;
        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);
        RulesetExclusionReport.Report(HarvestLog, build);
        return DemoAnalysis.Evaluate(
            demo,
            build,
            new AnalysisOptions
            {
                CaptureSnapshots = true
            });
    }

    /// <inheritdoc />
    public void InvalidateRules()
    {
        lock (_gate)
        {
            _rules = null;
        }
    }
}
