#region

using System.Globalization;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>How one run of a strat ended, for the record (strat-model.md §3.6).</summary>
public enum RunOutcome
{
    Won,
    Lost,
    Aborted,

    /// <summary>Neither Round Facts' <c>winner</c> nor a human <c>outcome</c> label says. Never folded into lost.</summary>
    Unknown
}

/// <summary>
///     One cell of the record: every run, split by how it ended. <see cref="Won" />, <see cref="Lost" />,
///     <see cref="Aborted" /> and <see cref="Unknown" /> sum to <see cref="Run" />.
/// </summary>
public sealed record RecordSplit(int Run, int Won, int Lost, int Aborted, int Unknown)
{
    public static RecordSplit Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Won over won plus lost; null when no run has a result. Aborted and unknown runs are not in it.</summary>
    public double? WinRate => Won + Lost == 0 ? null : (double)Won / (Won + Lost);

    /// <summary>The split of a set of runs.</summary>
    /// <param name="runs">The runs.</param>
    public static RecordSplit Of(IEnumerable<StratRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        int run = 0, won = 0, lost = 0, aborted = 0, unknown = 0;
        foreach (StratRun r in runs)
        {
            run++;
            switch (r.Outcome)
            {
                case RunOutcome.Won:
                    won++;
                    break;
                case RunOutcome.Lost:
                    lost++;
                    break;
                case RunOutcome.Aborted:
                    aborted++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        return new RecordSplit(run, won, lost, aborted, unknown);
    }
}

/// <summary>
///     One evidence instance: a tagged round in which the strat was run. <see cref="Ref" /> opens the clip.
///     <see cref="Revision" /> is the <c>strat.rev</c> label, 0 when the instance carries none (revisions start
///     at 1, so 0 cannot be mistaken for one). <see cref="Round" /> is nullable because an instance's round is.
/// </summary>
public sealed record StratRun(
    TagInstanceRef Ref,
    string Sha256,
    int? Round,
    int Revision,
    string? Provenance,
    RunOutcome Outcome,
    IReadOnlyList<string> Failures);

/// <summary>
///     The record of one strat, computed on demand and never stored (strat-model.md §3.6). Every count is
///     backed by the runs that produced it, so a number opens its clips.
/// </summary>
/// <param name="StratId">The strat.</param>
/// <param name="Revision">The strat's revision when the record was computed.</param>
/// <param name="Runs">One per evidence instance, in document then instance order.</param>
/// <param name="Total">Every run.</param>
/// <param name="ByProvenance">Keyed by the demo's provenance label; <see cref="StratEvidence.Unlabeled" /> for none.</param>
/// <param name="ByRevision">Keyed by <c>strat.rev</c>; 0 for runs tagged without one.</param>
/// <param name="FailureBreakdown">Keyed by <c>strat.failure</c> value: runs that name it, each counted once.</param>
/// <param name="SmallSample">Fewer than <see cref="StratEvidence.SmallSampleBelow" /> runs: the plan's "caution under eight".</param>
public sealed record StratRecord(
    Guid StratId,
    int Revision,
    IReadOnlyList<StratRun> Runs,
    RecordSplit Total,
    IReadOnlyDictionary<string, RecordSplit> ByProvenance,
    IReadOnlyDictionary<int, RecordSplit> ByRevision,
    IReadOnlyDictionary<string, int> FailureBreakdown,
    bool SmallSample)
{
    /// <summary>
    ///     Runs either side of history entry <paramref name="revision" /> (§3.8): tagged at a revision below it, and
    ///     at it or later. Runs tagged without a revision sit on neither side.
    /// </summary>
    /// <param name="revision">The history entry's revision.</param>
    public (RecordSplit Before, RecordSplit After) SplitAround(int revision) =>
        (RecordSplit.Of(Runs.Where(r => r.Revision > 0 && r.Revision < revision)),
            RecordSplit.Of(Runs.Where(r => r.Revision >= revision)));
}

/// <summary>
///     The evidence rule (strat-model.md §3.6, decisions 3, 4 and 7): which tag instances are runs of a strat and
///     how each one ended. Pure over documents the caller loaded; <see cref="StratEvidenceService" /> does the
///     loading.
///     <para>
///         <b>Won and lost.</b> The strat's own side against the round's <c>winner</c> fact, which the facts
///         refresh writes from Round Facts (overview correction 10: no side fact on the instance is needed). When
///         the fact is absent, or <c>none</c>, the human <c>outcome</c> label of the shipped palette decides; when
///         both are absent the run is <see cref="RunOutcome.Unknown" />. <c>strat.result: aborted</c> beats both:
///         a strat abandoned in a round that was won anyway is not a win for the strat.
///     </para>
/// </summary>
public static class StratEvidence
{
    /// <summary>The run's revision when it was tagged (reserved, overview correction 23).</summary>
    public const string RevisionGroup = "strat.rev";

    /// <summary><c>completed</c> or <c>aborted</c>; absent means completed.</summary>
    public const string ResultGroup = "strat.result";

    /// <summary>Why it failed; repeatable.</summary>
    public const string FailureGroup = "strat.failure";

    public const string Completed = "completed";
    public const string Aborted = "aborted";

    /// <summary>The shipped palette's human outcome group: the fallback when Round Facts has not run.</summary>
    public const string OutcomeGroup = "outcome";

    public const string OutcomeWon = "won";
    public const string OutcomeLost = "lost";

    /// <summary>The Round Facts fact the refresh writes: <c>T</c>, <c>CT</c> or <c>none</c>.</summary>
    public const string WinnerFact = "winner";

    /// <summary>The <see cref="StratRecord.ByProvenance" /> key for a demo with no label.</summary>
    public const string Unlabeled = "unlabeled";

    /// <summary>A record with fewer runs than this carries <see cref="StratRecord.SmallSample" />.</summary>
    public const int SmallSampleBelow = 8;

    /// <summary>The shipped failure vocabulary (decision 7), in the order the panel lists it. A palette may add values.</summary>
    public static readonly IReadOnlyList<string> Failures =
        ["utility-late", "entry-lost", "early-contact", "rotation-early", "info-lost", "economy", "other"];

    /// <summary>
    ///     The record of <paramref name="stratId" /> over <paramref name="docs" />. A run is an instance whose human
    ///     labels carry <c>strat: &lt;id&gt;</c> and whose source <see cref="TagQuery" />'s default slice admits
    ///     (human or accepted suggestion, never an import). The id matches in any GUID spelling.
    /// </summary>
    /// <param name="stratId">The strat.</param>
    /// <param name="revision">Its current revision, carried on the record.</param>
    /// <param name="side">Its side, <c>T</c> or <c>CT</c>, which the <c>winner</c> fact is read against.</param>
    /// <param name="docs">Tag documents; ones without the strat cost a scan and nothing else.</param>
    /// <param name="provenance">Labels by content hash, the batch <see cref="IDemoProvenanceSource.LabelsFor" />; null for none.</param>
    public static StratRecord Build(Guid stratId, int revision, string side, IEnumerable<TagDocument> docs,
        Func<IEnumerable<string>, IReadOnlyDictionary<string, string?>>? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(docs);
        List<TagDocument> documents = [.. docs];

        // TagQuery matches values ordinally, so it is asked for the spellings of this id that actually occur:
        // a hand-edited sidecar with an upper-case GUID is still evidence.
        HashSet<string> spellings = new(
            documents.SelectMany(d => d.Instances).SelectMany(i => i.Labels)
                .Where(l => IsStratLabelFor(l, stratId)).Select(l => l.Value),
            StringComparer.Ordinal);

        List<StratRun> runs = [];
        if (spellings.Count > 0)
        {
            TagSlice slice = new(null, null, [new LabelPredicate(LabelNamespace.Human, TagStore.StratGroup, spellings)], null, null, null);
            IReadOnlyList<TagInstanceRef> refs = TagQuery.Find(documents, slice);

            Dictionary<(string, Guid), TagInstance> instances = [];
            foreach (TagDocument document in documents)
            {
                foreach (TagInstance instance in document.Instances)
                {
                    instances.TryAdd((document.Demo.Sha256, instance.Id), instance);
                }
            }

            IReadOnlyDictionary<string, string?> labels = provenance?.Invoke(refs.Select(r => r.Sha256).Distinct(StringComparer.Ordinal))
                                                          ?? new Dictionary<string, string?>();
            foreach (TagInstanceRef found in refs)
            {
                if (!instances.TryGetValue((found.Sha256, found.Id), out TagInstance? instance))
                {
                    continue;
                }

                runs.Add(new StratRun(
                    found,
                    found.Sha256,
                    found.Round,
                    RevisionOf(instance, stratId),
                    labels.GetValueOrDefault(found.Sha256),
                    OutcomeOf(instance, side),
                    [.. ValuesOf(instance.Labels, FailureGroup).Distinct(StringComparer.Ordinal)]));
            }
        }

        return Record(stratId, revision, runs);
    }

    /// <summary>The record over runs already classified: the splits, the breakdown and the caution flag.</summary>
    /// <param name="stratId">The strat.</param>
    /// <param name="revision">Its current revision.</param>
    /// <param name="runs">The runs.</param>
    public static StratRecord Record(Guid stratId, int revision, IReadOnlyList<StratRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        Dictionary<string, RecordSplit> byProvenance = runs
            .GroupBy(r => r.Provenance ?? Unlabeled, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => RecordSplit.Of(g), StringComparer.Ordinal);
        Dictionary<int, RecordSplit> byRevision = runs
            .GroupBy(r => r.Revision)
            .ToDictionary(g => g.Key, g => RecordSplit.Of(g));

        // A won run's failure label is a note on a win, not a reason the strat failed, so the breakdown reads
        // the runs that did not win: lost, aborted, and unknown (which may yet turn out lost).
        Dictionary<string, int> failures = new(StringComparer.Ordinal);
        foreach (StratRun run in runs.Where(r => r.Outcome != RunOutcome.Won))
        {
            foreach (string failure in run.Failures)
            {
                failures[failure] = failures.GetValueOrDefault(failure) + 1;
            }
        }

        return new StratRecord(stratId, revision, runs, RecordSplit.Of(runs), byProvenance, byRevision, failures,
            runs.Count < SmallSampleBelow);
    }

    /// <summary>How one instance ended for a strat on <paramref name="side" />: aborted, then the fact, then the label.</summary>
    /// <param name="instance">The evidence instance.</param>
    /// <param name="side">The strat's side.</param>
    public static RunOutcome OutcomeOf(TagInstance instance, string side)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (ValuesOf(instance.Labels, ResultGroup).Any(v => string.Equals(v, Aborted, StringComparison.Ordinal)))
        {
            return RunOutcome.Aborted;
        }

        bool knownSide = side is StratVocabulary.SideT or StratVocabulary.SideCt;
        string? winner = ValuesOf(instance.Facts, WinnerFact).FirstOrDefault();
        if (knownSide && winner is StratVocabulary.SideT or StratVocabulary.SideCt)
        {
            return string.Equals(winner, side, StringComparison.Ordinal) ? RunOutcome.Won : RunOutcome.Lost;
        }

        return ValuesOf(instance.Labels, OutcomeGroup).FirstOrDefault() switch
        {
            OutcomeWon => RunOutcome.Won,
            OutcomeLost => RunOutcome.Lost,
            _ => RunOutcome.Unknown
        };
    }

    /// <summary>
    ///     The <c>strat.rev</c> that belongs to this strat's <c>strat</c> label. The tagging action writes the two
    ///     as a pair, so on an instance that ran several strats the revision is the first one after this strat's
    ///     label and before the next strat's; on an instance with one strat, any. 0 when none parses.
    /// </summary>
    /// <param name="instance">The evidence instance.</param>
    /// <param name="stratId">The strat.</param>
    public static int RevisionOf(TagInstance instance, Guid stratId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        List<TagLabel> labels = instance.Labels;
        int at = labels.FindIndex(l => IsStratLabelFor(l, stratId));
        if (at >= 0)
        {
            for (int i = at + 1; i < labels.Count && !IsGroup(labels[i], TagStore.StratGroup); i++)
            {
                if (IsGroup(labels[i], RevisionGroup) && ParseRevision(labels[i].Value) is { } paired)
                {
                    return paired;
                }
            }
        }

        bool oneStrat = labels.Where(l => IsGroup(l, TagStore.StratGroup)).All(l => IsStratLabelFor(l, stratId));
        return oneStrat
            ? labels.Where(l => IsGroup(l, RevisionGroup)).Select(l => ParseRevision(l.Value)).FirstOrDefault(r => r is not null) ?? 0
            : 0;
    }

    private static int? ParseRevision(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int revision) && revision > 0 ? revision : null;

    private static bool IsGroup(TagLabel label, string group) => string.Equals(label.Group, group, StringComparison.Ordinal);

    private static bool IsStratLabelFor(TagLabel label, Guid stratId) =>
        IsGroup(label, TagStore.StratGroup) && Guid.TryParse(label.Value, out Guid id) && id == stratId;

    private static IEnumerable<string> ValuesOf(IEnumerable<TagLabel> labels, string group) =>
        labels.Where(l => IsGroup(l, group)).Select(l => l.Value);
}

/// <summary>
///     Computes a strat's record from the Tag Store (strat-model.md §3.6). Loads only the documents whose index
///     row lists the strat (<see cref="TagIndexEntry.StratIds" />, overview correction 23), then applies
///     <see cref="StratEvidence.Build" />. Not for the UI thread: the scan is the Tag Store's
///     <see cref="TagStore.LoadDocuments" />, measured at 222 ms warm over 1000 documents.
/// </summary>
public sealed class StratEvidenceService
{
    private readonly IDemoProvenanceSource? _provenance;
    private readonly TagStore _tags;

    /// <param name="tags">The tag documents.</param>
    /// <param name="provenance">Demo Provenance Labels; null leaves every run unlabeled.</param>
    public StratEvidenceService(TagStore tags, IDemoProvenanceSource? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags = tags;
        _provenance = provenance;
    }

    /// <summary>The record of a strat as it stands now.</summary>
    /// <param name="strat">The strat; its id, revision and side are read.</param>
    public StratRecord Compute(StratDocument strat)
    {
        ArgumentNullException.ThrowIfNull(strat);
        Guid id = strat.Id;
        List<TagDocument> documents = _tags.LoadDocuments(e => e.StratIds.Any(v => Guid.TryParse(v, out Guid g) && g == id));
        return StratEvidence.Build(id, strat.Revision, strat.Side, documents,
            _provenance is null ? null : _provenance.LabelsFor);
    }

    /// <summary><see cref="Compute" /> on the thread pool, for the record pane.</summary>
    /// <param name="strat">The strat; a snapshot, since the session may edit the live one meanwhile.</param>
    /// <param name="cancellationToken">Cancels before the scan starts.</param>
    public Task<StratRecord> ComputeAsync(StratDocument strat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strat);
        StratDocument snapshot = strat.Clone();
        return Task.Run(() => Compute(snapshot), cancellationToken);
    }
}

/// <summary>One line of the record pane: a key, what it shows, its split, and the runs behind every number.</summary>
/// <param name="Key">The dictionary key it came from (<c>scrim</c>, <c>3</c>); <c>total</c> for the total.</param>
/// <param name="Display">The words the pane shows.</param>
/// <param name="Split">The counts.</param>
/// <param name="Runs">The runs the counts were made from.</param>
public sealed record StratRecordRow(string Key, string Display, RecordSplit Split, IReadOnlyList<StratRun> Runs)
{
    /// <summary>The runs behind one count: what a click on that number opens.</summary>
    /// <param name="outcome">The count's outcome.</param>
    public IReadOnlyList<StratRun> RunsWith(RunOutcome outcome) => [.. Runs.Where(r => r.Outcome == outcome)];
}

/// <summary>One failure value, how many non-winning runs named it, and those runs.</summary>
public sealed record StratFailureRow(string Failure, int Count, IReadOnlyList<StratRun> Runs);

/// <summary>
///     What the Strat Record Panel shows, ordered and worded, with no UI type in it. The panel binds to this; the
///     record stays the data.
/// </summary>
public sealed class StratRecordPane
{
    private StratRecordPane(StratRecord record)
    {
        Record = record;
        Total = new StratRecordRow("total", "All runs", record.Total, record.Runs);

        // The vocabulary's order first, so the panel reads the same from strat to strat; a value the
        // vocabulary does not have (a palette's own) after it, ordinal.
        ByProvenance =
        [
            .. record.ByProvenance.Keys
                .OrderBy(k => ProvenanceRank(k)).ThenBy(k => k, StringComparer.Ordinal)
                .Select(k => new StratRecordRow(k, k, record.ByProvenance[k],
                    [.. record.Runs.Where(r => string.Equals(r.Provenance ?? StratEvidence.Unlabeled, k, StringComparison.Ordinal))]))
        ];

        ByRevision =
        [
            .. record.ByRevision.Keys.Order()
                .Select(n => new StratRecordRow(n.ToString(CultureInfo.InvariantCulture),
                    n == 0 ? "revision not tagged" : "revision " + n.ToString(CultureInfo.InvariantCulture),
                    record.ByRevision[n], [.. record.Runs.Where(r => r.Revision == n)]))
        ];

        Failures =
        [
            .. record.FailureBreakdown.Keys
                .OrderBy(FailureRank).ThenBy(k => k, StringComparer.Ordinal)
                .Select(f => new StratFailureRow(f, record.FailureBreakdown[f],
                    [.. record.Runs.Where(r => r.Outcome != RunOutcome.Won && r.Failures.Contains(f, StringComparer.Ordinal))]))
        ];
    }

    public StratRecord Record { get; }

    public StratRecordRow Total { get; }

    /// <summary>The four provenance labels in their vocabulary order, then any other value, then unlabeled.</summary>
    public IReadOnlyList<StratRecordRow> ByProvenance { get; }

    /// <summary>Revisions ascending; runs tagged without one first, as "revision not tagged".</summary>
    public IReadOnlyList<StratRecordRow> ByRevision { get; }

    /// <summary>The shipped failure values in vocabulary order, then any other value.</summary>
    public IReadOnlyList<StratFailureRow> Failures { get; }

    /// <summary>The caution line, or null at eight runs and more.</summary>
    public string? Caution => Record.SmallSample
        ? $"{Record.Runs.Count} {(Record.Runs.Count == 1 ? "run" : "runs")}: a small sample, read with caution"
        : null;

    /// <summary>How many runs have no result yet, said rather than folded into lost; null when none.</summary>
    public string? UnknownNote => Record.Total.Unknown == 0
        ? null
        : $"{Record.Total.Unknown} {(Record.Total.Unknown == 1 ? "run has" : "runs have")} no result yet";

    /// <summary>The pane for a record.</summary>
    /// <param name="record">The record.</param>
    public static StratRecordPane From(StratRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new StratRecordPane(record);
    }

    /// <summary>A split's win rate as the pane prints it: <c>63%</c>, or <c>no result</c> when nothing was won or lost.</summary>
    /// <param name="split">The split.</param>
    public static string WinRateText(RecordSplit split)
    {
        ArgumentNullException.ThrowIfNull(split);
        return split.WinRate is { } rate
            ? Math.Round(rate * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%"
            : "no result";
    }

    private static int ProvenanceRank(string key)
    {
        if (string.Equals(key, StratEvidence.Unlabeled, StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        int index = DemoProvenanceLabel.All.ToList().IndexOf(key);
        return index < 0 ? DemoProvenanceLabel.All.Count : index;
    }

    private static int FailureRank(string failure)
    {
        int index = StratEvidence.Failures.ToList().IndexOf(failure);
        return index < 0 ? StratEvidence.Failures.Count : index;
    }
}
