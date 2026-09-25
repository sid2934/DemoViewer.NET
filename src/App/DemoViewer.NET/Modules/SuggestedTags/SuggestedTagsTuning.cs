#region

using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     One hand-made tag the recall/precision score is measured against: a round, a window and the
///     code it carries. Deliberately not a <see cref="TagInstance" /> reference: the score is pure over
///     these three numbers, so a test can build one without a Tag Store.
/// </summary>
/// <param name="Round">The round number.</param>
/// <param name="FromTick">The instance's window start, frame clock.</param>
/// <param name="ToTick">The instance's window end, frame clock.</param>
public readonly record struct HandTagWindow(int Round, int FromTick, int ToTick);

/// <summary>
///     One detector's line in the tuning view (suggested-tags.md §3.7): what the verdicts over demos
///     that have any say, and what recall and precision say over demos that also carry hand tags of the
///     same code. <see cref="Recall" /> and <see cref="Precision" /> are null rather than zero when
///     there is nothing to score against — a team that has not hand-tagged this code yet sees "no
///     data", not a discouraging 0%, the same rule the Sight columns use for missing geometry.
/// </summary>
/// <param name="Detector">The detector id, the profile section and the tag code it proposes.</param>
/// <param name="Made">Proposals fired across every demo the evaluator has built.</param>
/// <param name="Accepted">Accepted as proposed.</param>
/// <param name="Edited">Accepted with a change.</param>
/// <param name="Rejected">Rejected, never offered again.</param>
/// <param name="Pending">Still waiting for a verdict.</param>
/// <param name="Recall">Matched hand tags over every hand tag of the code, or null with none to score.</param>
/// <param name="Precision">Matched proposals over every fired proposal, or null with nothing fired.</param>
public sealed record DetectorTuningRow(
    string Detector, int Made, int Accepted, int Edited, int Rejected, int Pending,
    double? Recall, double? Precision);

/// <summary>The tuning view's whole table, plus how many demos fed each half of it.</summary>
/// <param name="Rows">One row per detector, in <see cref="ProposalDetection.All" /> order.</param>
/// <param name="DemosWithVerdicts">Demos the made/accepted/edited/rejected counts were drawn from.</param>
/// <param name="DemosWithHandTags">Demos that also contributed to a recall or precision number.</param>
public sealed record TuningReport(IReadOnlyList<DetectorTuningRow> Rows, int DemosWithVerdicts, int DemosWithHandTags);

/// <summary>
///     The pure half of the tuning view: aggregating verdicts, and scoring fired proposals against hand
///     tags by the overlap rule (suggested-tags.md §7.3 item 3). Neither function touches a file, a Tag
///     Store or a parse, so a parameter sweep is exercised without a demo. The re-run that feeds them
///     with a candidate profile's proposals is <see cref="SuggestedTagsTuningService" />.
/// </summary>
public static class SuggestedTagsTuning
{
    /// <summary>
    ///     A proposal counts as a match for a hand tag when their windows overlap by at least half the
    ///     shorter one (§7.3 item 3). One hand tag matches at most one proposal and vice versa: the
    ///     greedy pairing takes the largest overlaps first, so two close instances do not both claim the
    ///     same tag.
    /// </summary>
    public const double OverlapFraction = 0.5;

    /// <summary>
    ///     Every detector's row: the verdict counts from <paramref name="entries" />, and recall/precision
    ///     from <paramref name="proposalsByDetector" /> scored against <paramref name="handTagsByCode" />.
    ///     A detector with proposals across zero demos still gets a row, all zeros, so the table never
    ///     drops one the moment nothing has fired.
    /// </summary>
    /// <param name="entries">Every proposal entry (with or without a verdict) across the demos scored.</param>
    /// <param name="proposalsByDetector">The same run's fired proposals, keyed by detector id, for scoring.</param>
    /// <param name="handTagsByCode">Hand-tagged windows keyed by code (the detector id), across the same demos.</param>
    /// <param name="demosWithVerdicts">How many demos <paramref name="entries" /> was drawn from.</param>
    /// <param name="demosWithHandTags">How many demos <paramref name="handTagsByCode" /> was drawn from.</param>
    public static TuningReport Build(
        IReadOnlyList<ProposalEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyList<TagProposal>> proposalsByDetector,
        IReadOnlyDictionary<string, IReadOnlyList<HandTagWindow>> handTagsByCode,
        int demosWithVerdicts,
        int demosWithHandTags)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(proposalsByDetector);
        ArgumentNullException.ThrowIfNull(handTagsByCode);

        Dictionary<string, VerdictCounts> counts = Aggregate(entries);
        List<DetectorTuningRow> rows = [];
        foreach (IProposalDetector detector in ProposalDetection.All)
        {
            VerdictCounts c = counts.GetValueOrDefault(detector.Id);
            IReadOnlyList<TagProposal> fired = proposalsByDetector.GetValueOrDefault(detector.Id, []);
            IReadOnlyList<HandTagWindow> hand = handTagsByCode.GetValueOrDefault(detector.Code, []);
            (double? recall, double? precision) = Score(fired, hand);
            rows.Add(new DetectorTuningRow(detector.Id, c.Made, c.Accepted, c.Edited, c.Rejected, c.Pending,
                recall, precision));
        }

        return new TuningReport(rows, demosWithVerdicts, demosWithHandTags);
    }

    /// <summary>
    ///     Made/accepted/edited/rejected/pending per detector, over whatever entries are handed in: every
    ///     proposal is "made" once, regardless of its verdict.
    /// </summary>
    /// <param name="entries">The entries to fold.</param>
    public static Dictionary<string, VerdictCounts> Aggregate(IEnumerable<ProposalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<string, VerdictCounts> counts = new(StringComparer.Ordinal);
        foreach (ProposalEntry entry in entries)
        {
            string id = entry.Proposal.Detector;
            VerdictCounts c = counts.GetValueOrDefault(id);
            counts[id] = entry.Verdict?.Verdict switch
            {
                SuggestionVerdicts.Accepted => c with { Made = c.Made + 1, Accepted = c.Accepted + 1 },
                SuggestionVerdicts.Edited => c with { Made = c.Made + 1, Edited = c.Edited + 1 },
                SuggestionVerdicts.Rejected => c with { Made = c.Made + 1, Rejected = c.Rejected + 1 },
                _ => c with { Made = c.Made + 1, Pending = c.Pending + 1 }
            };
        }

        return counts;
    }

    /// <summary>
    ///     Recall (matched hand tags / all hand tags) and precision (matched proposals / all fired), or
    ///     null for either side with nothing to divide by. A hand tag and a proposal match on the same
    ///     round and an overlap of at least <see cref="OverlapFraction" /> of the shorter window; the
    ///     pairing is greedy by overlap size, largest first, each side used at most once.
    /// </summary>
    /// <param name="fired">The detector's proposals across the scored demos.</param>
    /// <param name="hand">The hand-tagged windows of the same code across the same demos.</param>
    public static (double? Recall, double? Precision) Score(
        IReadOnlyList<TagProposal> fired, IReadOnlyList<HandTagWindow> hand)
    {
        ArgumentNullException.ThrowIfNull(fired);
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.Count == 0)
        {
            // No ground truth for this code at all: a fired proposal cannot be judged right or wrong,
            // so both numbers are "no data" rather than a discouraging 0%.
            return (null, null);
        }

        List<(int Fired, int Hand, int Overlap)> candidates = [];
        for (int f = 0; f < fired.Count; f++)
        {
            for (int h = 0; h < hand.Count; h++)
            {
                if (fired[f].Round != hand[h].Round)
                {
                    continue;
                }

                int overlap = Overlap(fired[f].FromTick, fired[f].ToTick, hand[h].FromTick, hand[h].ToTick);
                int shorter = Math.Min(fired[f].ToTick - fired[f].FromTick, hand[h].ToTick - hand[h].FromTick);
                if (shorter > 0 && overlap >= shorter * OverlapFraction)
                {
                    candidates.Add((f, h, overlap));
                }
            }
        }

        HashSet<int> usedFired = [];
        HashSet<int> usedHand = [];
        int matched = 0;
        foreach ((int f, int h, int _) in candidates.OrderByDescending(c => c.Overlap))
        {
            if (usedFired.Contains(f) || usedHand.Contains(h))
            {
                continue;
            }

            usedFired.Add(f);
            usedHand.Add(h);
            matched++;
        }

        double recall = (double)matched / hand.Count;
        double? precision = fired.Count == 0 ? null : (double)matched / fired.Count;
        return (recall, precision);
    }

    private static int Overlap(int aFrom, int aTo, int bFrom, int bTo) =>
        Math.Max(0, Math.Min(aTo, bTo) - Math.Max(aFrom, bFrom));
}

/// <summary>One detector's verdict tally. Default value is all zeros, so a lookup miss reads as "nothing yet".</summary>
/// <param name="Made">Every proposal fired, regardless of verdict.</param>
/// <param name="Accepted">Accepted as proposed.</param>
/// <param name="Edited">Accepted with a change.</param>
/// <param name="Rejected">Rejected.</param>
/// <param name="Pending">No verdict yet.</param>
public readonly record struct VerdictCounts(int Made, int Accepted, int Edited, int Rejected, int Pending);
