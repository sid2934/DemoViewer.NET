namespace DemoViewer.NET.Extensions.Sdk.Review;

/// <summary>What a row of the review queue is: a clip to watch, or the title card that opens a section.</summary>
public enum ReviewClipKind
{
    /// <summary>A clip: one demo and one tick range.</summary>
    Clip,

    /// <summary>A section's title card. Every clip after it, up to the next card, belongs to that section.</summary>
    Section
}

/// <summary>
///     Where a clip came from. Plain strings rather than an enum, so a surface that lands later adds a value
///     without a schema bump and a queue the host wrote on a newer build still reads.
/// </summary>
public static class ReviewSources
{
    /// <summary>Staged from the host's highlight tray, Match Overview or its add-clips picker.</summary>
    public const string Highlight = "highlight";

    /// <summary>Sent from a situation search's results.</summary>
    public const string Situation = "situation";

    /// <summary>One instance of a tag.</summary>
    public const string Tag = "tag";

    /// <summary>Picked by hand at the playhead.</summary>
    public const string Manual = "manual";

    /// <summary>A lineup clip an earlier build queued.</summary>
    public const string Lineup = "lineup";

    /// <summary>A round some dossier section stacks, opened from that section.</summary>
    public const string Dossier = "dossier";
}

/// <summary>
///     One row of the host's review queue: a clip, <c>(demo, from, to, note)</c> plus the one-line question
///     the reviewer is asked, or a section's title card. One shape for both in one ordered list, because a
///     section is simply where its card sits, so moving a card moves the boundary and nothing else.
///     <para>
///         <b>Read-only, and the instance is the identity.</b> The host hands back the same instance for a
///         row that has not changed, on every read of <see cref="IExtensionReviewQueue.Entries" />. A list
///         view may therefore keep a realized row in place while
///         <see cref="object.ReferenceEquals(object, object)" /> holds and re-apply only the rows that moved.
///     </para>
///     <para>
///         Every edit goes through <see cref="IExtensionReviewQueue" /> keyed by <see cref="Id" />; nothing
///         here is settable, and <see cref="Clip" /> is how a surface makes one to send.
///     </para>
/// </summary>
public sealed class ReviewClip
{
    internal ReviewClip()
    {
    }

    /// <summary>Stable across edits and moves; the key every mutation names.</summary>
    public Guid Id { get; internal init; }

    /// <summary>Whether this row is a clip or a section's title card.</summary>
    public ReviewClipKind Kind { get; internal init; }

    /// <summary>A section's title; empty on a clip.</summary>
    public string Title { get; internal init; } = "";

    /// <summary>The demo's path when the clip was queued.</summary>
    public string DemoPath { get; internal init; } = "";

    /// <summary>The demo's content hash when the surface knew it: the join that survives a moved file.</summary>
    public string? Sha256 { get; internal init; }

    /// <summary>First tick of the clip, on the host's frame clock.</summary>
    public int FromTick { get; internal init; }

    /// <summary>Last tick of the clip, frame clock, inclusive.</summary>
    public int ToTick { get; internal init; }

    /// <summary>The demo's tick rate, for the clip's length; 0 when the surface did not know it.</summary>
    public int TickRate { get; internal init; }

    /// <summary>What the clip is, as the surface described it; on a title card, the line under the title.</summary>
    public string Note { get; internal init; } = "";

    /// <summary>The one-line question the reviewer answers while watching; empty until someone writes one.</summary>
    public string Question { get; internal init; } = "";

    /// <summary>One of <see cref="ReviewSources" />; empty on a section.</summary>
    public string Source { get; internal init; } = "";

    /// <summary>The team the sending surface was about, or null when it had none.</summary>
    public Guid? TeamId { get; internal init; }

    /// <summary>A reviewer has watched the clip.</summary>
    public bool Reviewed { get; internal init; }

    /// <summary>The clip's length in seconds, or null without a tick rate.</summary>
    public double? Seconds => TickRate > 0 ? (ToTick - FromTick) / (double)TickRate : null;

    /// <summary>A clip to send. A reversed range is put the right way round.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="fromTick">First tick, frame clock.</param>
    /// <param name="toTick">Last tick, frame clock.</param>
    /// <param name="note">What the clip is.</param>
    /// <param name="source">One of <see cref="ReviewSources" />.</param>
    /// <param name="tickRate">The demo's tick rate, or 0 when the caller does not know it.</param>
    /// <param name="sha256">The demo's content hash, or null.</param>
    /// <param name="teamId">The team the sending surface was about, or null.</param>
    public static ReviewClip Clip(string demoPath, int fromTick, int toTick, string note, string source,
        int tickRate = 0, string? sha256 = null, Guid? teamId = null)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        return new ReviewClip
        {
            Id = Guid.NewGuid(),
            Kind = ReviewClipKind.Clip,
            DemoPath = demoPath,
            Sha256 = sha256,
            FromTick = Math.Max(0, Math.Min(fromTick, toTick)),
            ToTick = Math.Max(0, Math.Max(fromTick, toTick)),
            TickRate = Math.Max(0, tickRate),
            Note = note ?? "",
            Source = source ?? "",
            TeamId = teamId
        };
    }
}
