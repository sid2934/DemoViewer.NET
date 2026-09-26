#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>How much an issue matters: a refusal blocks a save, a warning and an info never do.</summary>
public enum StratIssueSeverity
{
    Info,
    Warning,
    Refusal
}

/// <summary>One validator finding, pointed at the field it is about.</summary>
/// <param name="Severity">Whether it blocks a save.</param>
/// <param name="Field">An RFC 6901 pointer into the file; empty for the whole file.</param>
/// <param name="Message">What is wrong, for the tab's issue list.</param>
public sealed record StratIssue(StratIssueSeverity Severity, string Field, string Message);

/// <summary>
///     The rules of strat-model.md §3.10, run on load (the strat opens whatever they say) and on save (a refusal
///     blocks it). Refusals are the structural rules other code relies on: five slots in order, a closed verb
///     list, a non-increasing clock, branches that point somewhere. Everything a team may reasonably bend (a CT
///     "execute", a place a map update renamed) only warns.
/// </summary>
public static class StratValidator
{
    private const double EarliestAfterTimerSeconds = -60;

    // §3.3.1: the usual side (null for either) and what targetSite should hold.
    private static readonly Dictionary<string, (string? Side, SiteRule Site)> Applicability = new(StringComparer.Ordinal)
    {
        ["execute"] = (StratVocabulary.SideT, SiteRule.Required),
        ["rush"] = (StratVocabulary.SideT, SiteRule.Required),
        ["explode"] = (StratVocabulary.SideT, SiteRule.Required),
        ["split"] = (StratVocabulary.SideT, SiteRule.Required),
        ["wrap"] = (StratVocabulary.SideT, SiteRule.Required),
        ["fake"] = (StratVocabulary.SideT, SiteRule.Required),
        ["default"] = (StratVocabulary.SideT, SiteRule.None),
        ["setup"] = (StratVocabulary.SideCt, SiteRule.Optional),
        ["retake"] = (StratVocabulary.SideCt, SiteRule.Required),
        ["anti-eco"] = (null, SiteRule.Optional),
        ["save"] = (null, SiteRule.None)
    };

    private enum SiteRule
    {
        Required,
        Optional,
        None
    }

    /// <summary>Every rule of §3.10 that reads the strat file.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="places">The map's canonical places and the owner's aliases; null skips the place rules.</param>
    /// <param name="index">The store's index, for the branch-target rule; null skips it.</param>
    /// <param name="lineupExists">
    ///     Whether a lineup id resolves on the document's map (<c>GrenadeIndex.DescribeLineup</c> is not
    ///     null); null keeps the pre-Lineup-On-A-Strat-Step wording ("there is no Utility Book index yet").
    /// </param>
    public static IReadOnlyList<StratIssue> Validate(StratDocument document, CalloutResolver? places = null,
        IReadOnlyList<StratIndexEntry>? index = null, Func<string, Guid, bool>? lineupExists = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<StratIssue> issues = [];

        ValidateHeader(document, issues);
        ValidateSlots(document, issues);
        HashSet<Guid> stepIds = ValidateSteps(document, places, lineupExists, issues);
        ValidateBranches(document, stepIds, index, issues);

        if (document.Extra is { Count: > 0 } extra)
        {
            issues.Add(Info("", "unknown fields kept: " + string.Join(", ", extra.Keys.Order(StringComparer.Ordinal))));
        }

        return issues;
    }

    /// <summary>The <c>callouts.json</c> rules: duplicate or empty aliases refuse; a place the map lacks warns.</summary>
    /// <param name="table">The table.</param>
    /// <param name="places">The map's canonical places; null skips the place rules.</param>
    public static IReadOnlyList<StratIssue> ValidateCallouts(CalloutTable table, CalloutResolver? places = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        List<StratIssue> issues = [];
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < table.Aliases.Count; i++)
        {
            CalloutAlias alias = table.Aliases[i];
            string pointer = $"/aliases/{i}";
            string key = CalloutResolver.Fold(alias.Alias ?? "");
            if (key.Length == 0)
            {
                issues.Add(Refuse(pointer + "/alias", "an alias is empty"));
            }
            else if (!seen.TryAdd(key, i))
            {
                issues.Add(Refuse(pointer + "/alias", $"alias '{alias.Alias}' is already defined at /aliases/{seen[key]}"));
            }

            if (string.IsNullOrWhiteSpace(alias.Place))
            {
                issues.Add(Refuse(pointer + "/place", $"alias '{alias.Alias}' maps to no place"));
            }
            else if (places is not null && !places.IsCanonical(alias.Place))
            {
                issues.Add(Warn(pointer + "/place", $"'{alias.Place}' is not a place on {table.Map}"));
            }
        }

        if (places is not null && table.Canonical is { Names.Count: > 0 } snapshot)
        {
            foreach (string gone in snapshot.Names.Where(n => !places.IsCanonical(n)))
            {
                issues.Add(Warn("/canonical/names", $"'{gone}' was a place when the aliases were edited and is not one now"));
            }
        }

        return issues;
    }

    private static void ValidateHeader(StratDocument document, List<StratIssue> issues)
    {
        if (document.SchemaVersion < 1)
        {
            issues.Add(Refuse("/schemaVersion", "schemaVersion is missing or below 1"));
        }

        if (document.Id == Guid.Empty)
        {
            issues.Add(Refuse("/id", "id is not a GUID"));
        }

        if (string.IsNullOrWhiteSpace(document.Map))
        {
            issues.Add(Refuse("/map", "map is empty"));
        }

        bool sideKnown = document.Side is StratVocabulary.SideT or StratVocabulary.SideCt;
        if (!sideKnown)
        {
            issues.Add(Refuse("/side", $"side '{document.Side}' is not T or CT"));
        }

        if (document.StatusValue is null)
        {
            issues.Add(Refuse("/status", $"status '{document.Status}' is not Theory, InProgress, Active or Archived"));
        }

        if (!Applicability.TryGetValue(document.Type, out (string? Side, SiteRule Site) rule))
        {
            issues.Add(Warn("/type", $"type '{document.Type}' is not in the vocabulary"));
        }
        else
        {
            if (sideKnown && rule.Side is not null && rule.Side != document.Side)
            {
                issues.Add(Warn("/side", $"a {document.Type} is usually a {rule.Side} strat"));
            }

            bool hasSite = document.TargetSite is not null;
            if (rule.Site == SiteRule.Required && !hasSite)
            {
                issues.Add(Warn("/targetSite", $"a {document.Type} names its target site"));
            }
            else if (rule.Site == SiteRule.None && hasSite)
            {
                issues.Add(Warn("/targetSite", $"a {document.Type} has no target site"));
            }
        }

        if (document.TargetSite is { } site && !StratVocabulary.TargetSites.Contains(site))
        {
            issues.Add(Warn("/targetSite", $"target site '{site}' is not A or B"));
        }

        if (document.Economy is { } economy && !StratVocabulary.Economies.Contains(economy))
        {
            issues.Add(Warn("/economy", $"economy '{economy}' is not one of {string.Join(", ", StratVocabulary.Economies)}"));
        }

        if (!string.Equals(document.Clock.Kind, StratClock.RoundKind, StringComparison.Ordinal))
        {
            // The plant clock is reserved, not defined: the steps are read as round clock regardless.
            issues.Add(Warn("/clock/kind", $"clock '{document.Clock.Kind}' is not defined in this version; read as the round clock"));
        }
    }

    private static void ValidateSlots(StratDocument document, List<StratIssue> issues)
    {
        if (document.Slots.Count != StratVocabulary.Slots.Count
            || !document.Slots.Select(s => s.Slot).SequenceEqual(StratVocabulary.Slots, StringComparer.Ordinal))
        {
            issues.Add(Refuse("/slots", "slots are not exactly A, B, C, D, E in order"));
        }
    }

    private static HashSet<Guid> ValidateSteps(StratDocument document, CalloutResolver? places,
        Func<string, Guid, bool>? lineupExists, List<StratIssue> issues)
    {
        HashSet<Guid> ids = [];
        double roundSeconds = document.Clock.RoundSeconds;
        for (int i = 0; i < document.Steps.Count; i++)
        {
            StratStep step = document.Steps[i];
            string pointer = $"/steps/{i}";

            if (!ids.Add(step.Id))
            {
                issues.Add(Refuse(pointer + "/id", $"step id {step.Id} is duplicated"));
            }

            if (i > 0 && step.AtSeconds > document.Steps[i - 1].AtSeconds)
            {
                issues.Add(Refuse(pointer + "/atSeconds",
                    $"{StratClock.Format(step.AtSeconds)} comes after {StratClock.Format(document.Steps[i - 1].AtSeconds)}; the clock counts down"));
            }

            if (step.AtSeconds > roundSeconds || step.AtSeconds < EarliestAfterTimerSeconds)
            {
                issues.Add(Warn(pointer + "/atSeconds",
                    string.Create(CultureInfo.InvariantCulture, $"{step.AtSeconds} s is outside the round clock ({roundSeconds} to {EarliestAfterTimerSeconds})")));
            }

            if (step.Actor != StratVocabulary.ActorAll && !StratVocabulary.Slots.Contains(step.Actor))
            {
                issues.Add(Refuse(pointer + "/actor", $"actor '{step.Actor}' is not a slot or 'all'"));
            }

            if (!StratVocabulary.Verbs.Contains(step.Verb))
            {
                issues.Add(Refuse(pointer + "/verb", $"verb '{step.Verb}' is not in the vocabulary"));
            }

            if (step.Verb == "move" && string.IsNullOrEmpty(step.To?.Place))
            {
                issues.Add(Warn(pointer + "/to", "a move has no destination place"));
            }

            CheckPlace(places, step.From?.Place, pointer + "/from/place", document.Map, issues);
            CheckPlace(places, step.To?.Place, pointer + "/to/place", document.Map, issues);

            if (step.Utility is { } utility)
            {
                if (!StratVocabulary.UtilityKinds.Contains(utility.Kind))
                {
                    issues.Add(Warn(pointer + "/utility/kind", $"utility '{utility.Kind}' is not a grenade kind"));
                }

                CheckPlace(places, utility.Landing?.Place, pointer + "/utility/landing/place", document.Map, issues);
                if (utility.LineupId is { } lineupId)
                {
                    if (lineupExists is null)
                    {
                        issues.Add(Info(pointer + "/utility/lineupId", "lineup not checked: there is no Utility Book index yet"));
                    }
                    else if (!lineupExists(document.Map, lineupId))
                    {
                        issues.Add(Warn(pointer + "/utility/lineupId", $"lineup {lineupId} was not found on {document.Map}"));
                    }
                }
            }

            for (int p = 0; p < step.Positions.Count; p++)
            {
                string slot = step.Positions[p].Slot;
                if (!StratVocabulary.Slots.Contains(slot) && !StratVocabulary.OpponentSlots.Contains(slot))
                {
                    issues.Add(Warn($"{pointer}/positions/{p}/slot", $"position slot '{slot}' is not A to E or O1 to O5"));
                }
            }

            if (step.Interpolation is { } interpolation && !StratVocabulary.Interpolations.Contains(interpolation))
            {
                // Overview correction 17: path is reserved, and a later build's value still opens.
                issues.Add(Warn(pointer + "/interpolation", $"interpolation '{interpolation}' is not defined in this version; read as linear"));
            }

            if (step.Extra is { Count: > 0 } extra)
            {
                issues.Add(Info(pointer, "unknown fields kept: " + string.Join(", ", extra.Keys.Order(StringComparer.Ordinal))));
            }
        }

        return ids;
    }

    private static void ValidateBranches(StratDocument document, HashSet<Guid> stepIds, IReadOnlyList<StratIndexEntry>? index,
        List<StratIssue> issues)
    {
        List<Guid> order = [.. document.Steps.Select(s => s.Id)];
        for (int i = 0; i < document.Branches.Count; i++)
        {
            StratBranch branch = document.Branches[i];
            string pointer = $"/branches/{i}";
            bool afterKnown = stepIds.Contains(branch.AfterStepId);
            if (!afterKnown)
            {
                issues.Add(Refuse(pointer + "/afterStepId", "the branch follows a step that is not in this strat"));
            }

            if (branch.Target.StratId == document.Id)
            {
                if (branch.Target.StepId is not { } stepId || !stepIds.Contains(stepId))
                {
                    issues.Add(Refuse(pointer + "/target/stepId", "the branch continues at a step that is not in this strat"));
                }
                else if (afterKnown && order.IndexOf(stepId) <= order.IndexOf(branch.AfterStepId))
                {
                    issues.Add(Refuse(pointer + "/target/stepId", "the branch continues at a step that is not later than the one it follows"));
                }
            }
            else if (index is not null
                     && !index.Any(e => e.Id == branch.Target.StratId && e.Owner.Equals(document.Owner)
                                                                      && string.Equals(e.Map, document.Map, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(Warn(pointer + "/target/stratId", "the branch continues at a strat that is not in this book on this map"));
            }
        }
    }

    private static void CheckPlace(CalloutResolver? places, string? place, string pointer, string map, List<StratIssue> issues)
    {
        if (places is not null && !string.IsNullOrEmpty(place) && !places.IsCanonical(place))
        {
            issues.Add(Warn(pointer, $"'{place}' is not a place on {map}"));
        }
    }

    private static StratIssue Refuse(string pointer, string message) => new(StratIssueSeverity.Refusal, pointer, message);

    private static StratIssue Warn(string pointer, string message) => new(StratIssueSeverity.Warning, pointer, message);

    private static StratIssue Info(string pointer, string message) => new(StratIssueSeverity.Info, pointer, message);
}
