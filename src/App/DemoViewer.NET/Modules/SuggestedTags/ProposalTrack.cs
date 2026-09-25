#region

using System.Globalization;
using System.Text;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>How sure a proposal is, in the three steps the track tints by (suggested-tags.md §3.6).</summary>
public enum ConfidenceStep
{
    /// <summary>Below 0.5.</summary>
    Low,

    /// <summary>0.5 to 0.75.</summary>
    Medium,

    /// <summary>Above 0.75.</summary>
    High
}

/// <summary>
///     The Proposal Track (suggested-tags.md §3.6): the open demo's pending proposals as bands on the tag
///     lane, labelled <c>code@site</c> and tinted by confidence. Accepted proposals leave it and show on the
///     Tag Track as instances; rejected ones disappear. The queue hands it the pending set with
///     <see cref="SetProposals" />, which raises <see cref="MarkersChanged" />.
///     <para>
///         <b>Merged like the Tag Track.</b> Bands must not overlap within a track and an execute overlaps
///         the opener of the same round, so overlapping windows merge into runs: a run of one is the
///         proposal's own band, a run of several is labelled with the count and its tooltip lists them.
///         <see cref="ProposalsInRun" /> is how a band press picks a proposal.
///     </para>
/// </summary>
public sealed class ProposalTrack : ITimelineTrack
{
    /// <summary>The track's stable id. Never renamed.</summary>
    public const string TrackId = "suggested";

    private const int MaxRunTooltipLines = 5;

    // Low-alpha washes for the three steps, the RoundTrack idiom: a track hands back ARGB rather than a
    // brush, and the host replaces these through StepColour with its theme tokens.
    private const uint TintLow = 0x40A0A8B0;
    private const uint TintMedium = 0x60D8C040;
    private const uint TintHigh = 0x805AB05A;

    private IReadOnlyList<TagProposal> _proposals = [];

    /// <summary>A step's colour as ARGB, or null for the track's own wash. The host maps steps to theme tokens here.</summary>
    public Func<ConfidenceStep, uint?>? StepColour { get; set; }

    /// <inheritdoc />
    public string Id => TrackId;

    /// <inheritdoc />
    public string DisplayName => "Suggested";

    /// <inheritdoc />
    public event Action? MarkersChanged;

    /// <summary>The step a confidence falls in.</summary>
    /// <param name="confidence">In <c>[0, 1]</c>.</param>
    public static ConfidenceStep StepOf(double confidence) =>
        confidence < 0.5 ? ConfidenceStep.Low
        : confidence <= 0.75 ? ConfidenceStep.Medium
        : ConfidenceStep.High;

    /// <summary><c>code@site</c>, or the code alone when the proposal names no site.</summary>
    /// <param name="proposal">The proposal.</param>
    public static string LabelOf(TagProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.Labels.TryGetValue("site", out string? site) && site.Length > 0
            ? $"{proposal.Code}@{site}"
            : proposal.Code;
    }

    /// <summary>One line per proposal: label, side, round, confidence.</summary>
    /// <param name="proposal">The proposal.</param>
    public static string Tooltip(TagProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return string.Create(CultureInfo.InvariantCulture,
            $"{LabelOf(proposal)} · {ProposalIds.SideName(proposal.Side)} · r{proposal.Round} · {proposal.Confidence:0.00}");
    }

    /// <summary>Replaces what the track shows and asks the timeline to re-query.</summary>
    /// <param name="pending">The pending proposals; the track keeps its own copy of the list.</param>
    public void SetProposals(IEnumerable<TagProposal> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        _proposals = [.. pending];
        MarkersChanged?.Invoke();
    }

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data) =>
        data is not null && _proposals.Any(p => data.FrameIndexAtTick(p.FromTick) >= 0);

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => Array.Empty<TimelineMarker>();

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data)
    {
        List<TimelineBand> bands = [];
        foreach ((List<Span> run, int end) in Runs(data))
        {
            Span head = run[0];
            if (run.Count == 1)
            {
                bands.Add(new TimelineBand(TrackId, head.Start, end, LabelOf(head.Proposal), Tooltip(head.Proposal),
                    ColourOf(StepOf(head.Proposal.Confidence))));
                continue;
            }

            StringBuilder sb = new();
            sb.Append(run.Count.ToString(CultureInfo.InvariantCulture)).Append(" suggestions");
            foreach (Span span in run.Take(MaxRunTooltipLines))
            {
                sb.Append('\n').Append(Tooltip(span.Proposal));
            }

            if (run.Count > MaxRunTooltipLines)
            {
                sb.Append("\n…");
            }

            // A run is as sure as its surest member: that is the one a press picks first.
            double best = run.Max(s => s.Proposal.Confidence);
            bands.Add(new TimelineBand(TrackId, head.Start, end, run.Count.ToString(CultureInfo.InvariantCulture),
                sb.ToString(), ColourOf(StepOf(best))));
        }

        return bands;
    }

    /// <summary>
    ///     The proposals merged into the band that starts at <paramref name="startFrame" />, in the track's
    ///     order (by start, then by end), or none when no band starts there.
    /// </summary>
    /// <param name="data">The timeline data the bands were built from.</param>
    /// <param name="startFrame">The band's first frame.</param>
    public IReadOnlyList<string> ProposalsInRun(ITimelineData data, int startFrame)
    {
        foreach ((List<Span> run, int _) in Runs(data))
        {
            if (run[0].Start == startFrame)
            {
                return run.ConvertAll(s => s.Proposal.Id);
            }
        }

        return Array.Empty<string>();
    }

    private uint ColourOf(ConfidenceStep step) =>
        StepColour?.Invoke(step) ?? step switch
        {
            ConfidenceStep.High => TintHigh,
            ConfidenceStep.Medium => TintMedium,
            _ => TintLow
        };

    // Every proposal on the frame axis, merged into runs of overlapping spans. A start past the end of
    // this parse is dropped and an end past it clamped, the Tag Track's rule.
    private List<(List<Span> Run, int End)> Runs(ITimelineData data)
    {
        List<(List<Span>, int)> runs = [];
        if (data is null || data.TotalFrames <= 0)
        {
            return runs;
        }

        int last = data.TotalFrames - 1;
        List<Span> spans = [];
        foreach (TagProposal proposal in _proposals)
        {
            int start = data.FrameIndexAtTick(proposal.FromTick);
            if (start < 0)
            {
                continue;
            }

            int end = data.FrameIndexAtTick(proposal.ToTick);
            end = end < 0 ? last : Math.Min(end, last);
            spans.Add(new Span(proposal, start, Math.Max(start, end)));
        }

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        List<Span>? current = null;
        int runEnd = 0;
        foreach (Span span in spans)
        {
            if (current is not null && span.Start <= runEnd)
            {
                current.Add(span);
                runEnd = Math.Max(runEnd, span.End);
                continue;
            }

            if (current is not null)
            {
                runs.Add((current, runEnd));
            }

            current = [span];
            runEnd = span.End;
        }

        if (current is not null)
        {
            runs.Add((current, runEnd));
        }

        return runs;
    }

    private readonly record struct Span(TagProposal Proposal, int Start, int End);
}
