namespace DemoViewer.NET.ViewModels;

/// <summary>
///     One fire-count badge row in the rule-diagnostics panel: how many times a trigger-backed
///     rule's edges applied during the last evaluation (work item 0.2, fed by the always-on
///     counters from 0.1). Per-player rules aggregate across all materialized players.
/// </summary>
/// <param name="ChainId">The declaring chain's config id.</param>
/// <param name="RuleId">The rule's config id (ids, not display names — names may collide).</param>
/// <param name="FireCount">Total edge applies for the rule in the last run; 0 = never fired.</param>
public sealed record RuleFireStat(string ChainId, string RuleId, int FireCount)
{
    /// <summary>Badge text, e.g. "42×".</summary>
    public string CountLabel => $"{FireCount}×";

    /// <summary>True when the rule never fired — the row renders amber and the lint points here.</summary>
    public bool NeverFired => FireCount == 0;

    /// <summary>Row label: "chain · rule".</summary>
    public string Label => $"{ChainId} · {RuleId}";
}
