namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     A ruleset whose outputs are stamped under its own <see cref="MergedRulesBuild.RulesetIdentity" />
///     rather than the highlights fingerprint. It rides the merged bare run while <see cref="Enabled" />
///     answers true and never enters <see cref="MergedRulesBuild.CoreDocs" />, so neither a toggle of its owner
///     nor a broken copy of it moves the highlights stamp or stops the highlights backlog.
/// </summary>
/// <param name="RulesetId">The <c>id:</c> of the ruleset as the rules directories spell it.</param>
/// <param name="Owner"><see cref="CoreOwner" />, or the id of the extension that owns the ruleset.</param>
/// <param name="Enabled">The owner's live answer to "is this ruleset on". Read on every build, never cached.</param>
public sealed record StampedRuleset(string RulesetId, string Owner, Func<bool> Enabled)
{
    /// <summary>The owner of a ruleset the app itself runs for every user.</summary>
    public const string CoreOwner = "core";

    /// <summary>The owner of a ruleset an extension's manifest claims that this launch did not contribute.</summary>
    public const string ClaimedOwner = "claimed";

    /// <summary>A core ruleset, always on.</summary>
    /// <param name="rulesetId">The ruleset's id.</param>
    public static StampedRuleset Core(string rulesetId) => new(rulesetId, CoreOwner, static () => true);
}
