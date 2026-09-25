#region

using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>One proposal with the verdict it already has, if any.</summary>
/// <param name="Proposal">The proposal as the detectors made it.</param>
/// <param name="VerdictKey">The key the verdict is stored under: the proposal's own id, or a re-matched one.</param>
/// <param name="Verdict">The verdict; null while the proposal is pending.</param>
public sealed record ProposalEntry(TagProposal Proposal, string? VerdictKey, SuggestionVerdict? Verdict)
{
    /// <summary>The round's freeze end, frame clock; 0 when the file did not say.</summary>
    public int RoundStartTick { get; init; }

    /// <summary>No verdict yet: what the track and the queue show.</summary>
    public bool IsPending => Verdict is null;
}

/// <summary>
///     A demo's proposals merged with its verdicts: what <see cref="SuggestedTagsService.Load" /> hands
///     the track and the queue.
/// </summary>
/// <param name="DemoPath">The demo.</param>
/// <param name="Sha256">The content hash the verdicts are keyed by; null when the demo has none yet.</param>
/// <param name="Entries">Every proposal in round order, then trigger order.</param>
/// <param name="IsPersistent">False on the browser host: the queue says "session only".</param>
/// <param name="VerdictsUnreadable">The verdicts file exists and cannot be read, so nothing is offered.</param>
public sealed record ProposalSet(
    string DemoPath,
    string? Sha256,
    IReadOnlyList<ProposalEntry> Entries,
    bool IsPersistent,
    bool VerdictsUnreadable)
{
    /// <summary>The set for a demo with no proposals.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="isPersistent">Whether anything here outlives the process.</param>
    public static ProposalSet Empty(string demoPath, bool isPersistent) => new(demoPath, null, [], isPersistent, false);

    /// <summary>The proposals still waiting for a verdict.</summary>
    public IReadOnlyList<ProposalEntry> Pending => [.. Entries.Where(e => e.IsPending)];
}

/// <summary>
///     Joins proposals to verdicts (suggested-tags.md §3.4). The identity key is stable across a
///     re-detection with changed parameters, so an exact key match is the rule. It is not stable across a
///     re-parse that renumbers rounds, which is why each verdict records the frame count of the parse it
///     was given on: a verdict from another parse is matched by the nearest trigger tick instead, same
///     detector and side, within <see cref="WindowSeconds" />, before anything is offered again.
/// </summary>
public static class ProposalVerdictMatch
{
    /// <summary>How far a re-parsed trigger may move and still be the same proposal.</summary>
    public const int WindowSeconds = ProposalIds.Quantum;

    /// <summary>The entries, in the proposals' order.</summary>
    /// <param name="proposals">The proposals.</param>
    /// <param name="verdicts">The demo's verdicts by key.</param>
    /// <param name="frameCount">The frame count of the parse the proposals were made on; 0 when unknown.</param>
    /// <param name="tickRate">Ticks per second, for the window.</param>
    public static IReadOnlyList<ProposalEntry> Resolve(
        IReadOnlyList<TagProposal> proposals,
        IReadOnlyDictionary<string, SuggestionVerdict> verdicts,
        int frameCount,
        int tickRate)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(verdicts);
        ProposalEntry[] entries = new ProposalEntry[proposals.Count];
        HashSet<string> used = new(StringComparer.Ordinal);

        // Same parse, same key: the ordinary case. A verdict with no frame count predates nothing and is
        // taken at its word.
        for (int i = 0; i < proposals.Count; i++)
        {
            TagProposal p = proposals[i];
            if (verdicts.TryGetValue(p.Id, out SuggestionVerdict? v) && SameParse(v, frameCount))
            {
                entries[i] = new ProposalEntry(p, p.Id, v);
                used.Add(p.Id);
            }
        }

        // Verdicts from another parse: nearest trigger first, so two proposals close together each find
        // their own verdict rather than both reaching for the first.
        int window = WindowSeconds * (tickRate > 0 ? tickRate : 64);
        List<(int Proposal, string Key, int Distance)> candidates = [];
        for (int i = 0; i < proposals.Count; i++)
        {
            if (entries[i] is not null)
            {
                continue;
            }

            TagProposal p = proposals[i];
            foreach ((string key, SuggestionVerdict v) in verdicts)
            {
                if (used.Contains(key) || SameParse(v, frameCount)
                                       || !string.Equals(v.Detector, p.Detector, StringComparison.Ordinal)
                                       || v.Side != p.Side)
                {
                    continue;
                }

                int distance = Math.Abs(v.TriggerTick - p.TriggerTick);
                if (distance <= window)
                {
                    candidates.Add((i, key, distance));
                }
            }
        }

        foreach ((int index, string key, int _) in candidates
                     .OrderBy(c => c.Distance).ThenBy(c => c.Proposal).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (entries[index] is not null || !used.Add(key))
            {
                continue;
            }

            entries[index] = new ProposalEntry(proposals[index], key, verdicts[key]);
        }

        for (int i = 0; i < proposals.Count; i++)
        {
            entries[i] ??= new ProposalEntry(proposals[i], null, null);
        }

        return entries;
    }

    private static bool SameParse(SuggestionVerdict verdict, int frameCount) =>
        verdict.FrameCount == 0 || frameCount == 0 || verdict.FrameCount == frameCount;
}
