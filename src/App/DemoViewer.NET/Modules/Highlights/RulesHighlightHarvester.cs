#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Parser;

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

    /// <summary>
    ///     True when highlights of <paramref name="rulesetId" /> belong in the highlights tier. A stamped
    ///     ruleset rides the same run but stays out of the fingerprint, so its firings stay out of the tier too.
    /// </summary>
    bool IsHighlightRuleset(string rulesetId) => true;
}

/// <summary>
///     The real harvester over the <see cref="MergedRulesBuild" />: highlights come out of the same build
///     as round facts, and are stamped with that build's fingerprint.
/// </summary>
public sealed class RulesHighlightHarvester : IHighlightHarvester
{
    /// <summary>
    ///     The composition profile id: the builder's GOTV profile (GOTV is the only supported
    ///     demo source; multi-source support is deferred). Must match
    ///     <c>RuleChainBuilder.Profile.GetType().Name</c> or fingerprints diverge from builds.
    /// </summary>
    public const string GotvProfileId = "Cs2GotvProfile";

    private readonly MergedRulesBuild _rules;

    /// <summary>A harvester over its own read of the shipped-plus-user rules.</summary>
    public RulesHighlightHarvester() : this(new MergedRulesBuild())
    {
    }

    /// <param name="rules">The shared rules read, so the fingerprint matches the one round facts are stored under.</param>
    public RulesHighlightHarvester(MergedRulesBuild rules)
    {
        _rules = rules;
    }

    /// <inheritdoc />
    public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate)
    {
        HighlightConfigFingerprint.Result result = _rules.Fingerprint(tickRate);
        return (result.Fingerprint, result.HighlightHashes);
    }

    /// <inheritdoc />
    public AnalysisRun RunBareAnalysis(ParsedDemo demo) => _rules.BareRun(demo);

    /// <inheritdoc />
    public AnalysisRun RunFullAnalysis(ParsedDemo demo) => _rules.FullRun(demo);

    /// <inheritdoc />
    public void InvalidateRules() => _rules.Invalidate();

    /// <inheritdoc />
    public bool IsHighlightRuleset(string rulesetId) => !_rules.IsStamped(rulesetId);
}
