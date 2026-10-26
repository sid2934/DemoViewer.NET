using System.Diagnostics.CodeAnalysis;

namespace DemoViewer.NET.Extensions.Sdk.Review;

/// <summary>
///     The host's review queue: one ordered list of clips from any demo, grouped by title cards into
///     sections. Every surface that finds moments sends them here and whatever walks them reads them back,
///     so two surfaces of two extensions share one list rather than each keeping its own.
///     <para>
///         <b>UI thread only</b>, like the service behind it. Every read and every mutation throws when it
///         is called from another thread, <see cref="Entries" /> included, whose first read waits for the
///         host to finish reading the queue off disk. Subscribing to <see cref="Changed" /> is the one
///         exception, since a view model does that while it is being built and torn down; the raise itself
///         is always on the UI thread.
///     </para>
///     <para>
///         <b>Reads are snapshots, rows are instances.</b> <see cref="Entries" /> answers a fresh list, and
///         a row that has not changed is the same <see cref="ReviewClip" /> instance it was last read as.
///         <see cref="ClipCount" /> is there because deriving it from <see cref="Entries" /> costs a copy.
///     </para>
///     <para>
///         <b>Every mutation is keyed by <see cref="ReviewClip.Id" /></b> and ignores an id the queue does
///         not hold, so a handler that acts on a stale list cannot throw or hit the wrong row.
///     </para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a queue a reviewer walks, the host's own name for the feature, not a Queue<T>.")]
public interface IExtensionReviewQueue
{
    /// <summary>The queue in order, title cards and clips together. A snapshot; the caller may hold it.</summary>
    IReadOnlyList<ReviewClip> Entries { get; }

    /// <summary>How many clips are queued, title cards not counted.</summary>
    int ClipCount { get; }

    /// <summary>How many queued clips nobody has marked reviewed.</summary>
    int UnreviewedCount { get; }

    /// <summary>Why the host could not read the stored queue, or null. While set, nothing is written back.</summary>
    string? FileProblem { get; }

    /// <summary>
    ///     Raised on the UI thread after every change, whoever made it. A handler that throws is reported
    ///     against the extension and does not reach whatever made the change.
    /// </summary>
    event Action? Changed;

    /// <summary>
    ///     Appends clips at the end, under a new title card when <paramref name="sectionTitle" /> is given. A
    ///     clip already queued (same demo, same range) is skipped, so sending a result set twice does not
    ///     double it, and a title card left with no clip is not added either.
    /// </summary>
    /// <param name="clips">The clips, in the order they should appear.</param>
    /// <param name="sectionTitle">A title card to open the batch with, or null to append bare.</param>
    /// <param name="sectionSubtitle">The line under that title.</param>
    /// <returns>
    ///     How many clips were added. 0 means every one of them was already queued, which is the answer a
    ///     surface reports rather than claiming it sent them.
    /// </returns>
    int Add(IEnumerable<ReviewClip> clips, string? sectionTitle = null, string sectionSubtitle = "");

    /// <summary>Puts a title card at the end of the queue.</summary>
    /// <param name="title">The card's title.</param>
    void AddSection(string title);

    /// <summary>Removes rows by id; an id the queue does not hold is ignored.</summary>
    /// <param name="ids">The rows to remove.</param>
    /// <returns>How many were removed.</returns>
    int Remove(IEnumerable<Guid> ids);

    /// <summary>
    ///     Moves one row by <paramref name="delta" /> places, clamped to the queue. Signed and relative: -1
    ///     is one place up, which is what an up arrow on a row means.
    /// </summary>
    /// <param name="id">The row.</param>
    /// <param name="delta">Signed places; negative is up.</param>
    void MoveBy(Guid id, int delta);

    /// <summary>Empties the queue, every surface's clips with it.</summary>
    void Clear();

    /// <summary>Marks clips reviewed or not, in one change; ids of title cards are ignored.</summary>
    /// <param name="ids">The clips.</param>
    /// <param name="reviewed">The mark.</param>
    /// <returns>How many clips changed.</returns>
    int SetReviewed(IEnumerable<Guid> ids, bool reviewed);

    /// <summary>The clips under a title card, up to the next card.</summary>
    /// <param name="cardId">The card; empty when the id is not a card's.</param>
    IReadOnlyList<ReviewClip> ClipsUnder(Guid cardId);

    /// <summary>Renames a title card. The title is trimmed to one line.</summary>
    /// <param name="id">The card.</param>
    /// <param name="title">The title.</param>
    /// <returns>False when the queue does not hold that id.</returns>
    bool SetTitle(Guid id, string title);

    /// <summary>Sets a clip's note, or a title card's subtitle. Trimmed to one line.</summary>
    /// <param name="id">The row.</param>
    /// <param name="note">The note.</param>
    /// <returns>False when the queue does not hold that id.</returns>
    bool SetNote(Guid id, string note);

    /// <summary>Sets a clip's one-line question. Trimmed to one line.</summary>
    /// <param name="id">The clip.</param>
    /// <param name="question">The question.</param>
    /// <returns>False when the queue does not hold that id.</returns>
    bool SetQuestion(Guid id, string question);
}
