#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Sdk.Review;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The host's <see cref="ReviewQueue" /> as one extension sees it: each <see cref="ReviewEntry" />
///     projected to the published <see cref="ReviewClip" />, and every mutation keyed by id so nothing an
///     extension holds is translated back into a queue row.
///     <para>
///         <b>The projection is memoized against the entry instance</b>, not merely cached. The queue
///         replaces an entry wholesale with <c>with</c>, so an unchanged row is the same object it was, and
///         a list view decides whether to re-apply a row by comparing that reference. A fresh projection per
///         read would make the comparison always false and re-apply every realized row on every queue
///         change, which at a few hundred rows is over a thousand property notifications per keystroke.
///         Weak keys, so a removed entry's projection goes with it.
///     </para>
///     <para>
///         <b>UI thread only</b>, like the service. Refused rather than marshalled: even
///         <see cref="Entries" /> can trigger the queue's file read, and a posted mutation would act on a
///         list the caller never saw. The event accessors are the exception: they touch neither the list
///         nor the file, and a tab disposed off the dispatcher must not throw. The check is injected so a
///         test can drive the adapter off the dispatcher, the shape <see cref="ExtensionJobs" /> uses.
///     </para>
/// </summary>
internal sealed class ExtensionReviewQueue : IExtensionReviewQueue
{
    private readonly MulticastGuard<Action> _changed = new("review queue change handler");
    private readonly ExtensionGuard? _guard;
    private readonly Func<bool> _onUiThread;
    private readonly ConditionalWeakTable<ReviewEntry, ReviewClip> _projected = new();
    private readonly ReviewQueue _queue;
    private Action? _handlers;
    private bool _joined;

    /// <param name="queue">The shared queue.</param>
    /// <param name="guard">The extension's fault scope, which its change handlers run under; null runs them bare.</param>
    /// <param name="onUiThread">Whether the caller is on the UI thread; always true when null.</param>
    internal ExtensionReviewQueue(ReviewQueue queue, ExtensionGuard? guard = null, Func<bool>? onUiThread = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _guard = guard;
        _onUiThread = onUiThread ?? (static () => true);
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewClip> Entries
    {
        get
        {
            Require(nameof(Entries));
            return [.. _queue.Entries.Select(Project)];
        }
    }

    /// <inheritdoc />
    public int ClipCount
    {
        get
        {
            Require(nameof(ClipCount));
            return _queue.ClipCount;
        }
    }

    /// <inheritdoc />
    public int UnreviewedCount
    {
        get
        {
            Require(nameof(UnreviewedCount));
            return _queue.UnreviewedCount;
        }
    }

    /// <inheritdoc />
    public string? FileProblem
    {
        get
        {
            Require(nameof(FileProblem));
            return _queue.FileProblem;
        }
    }

    // Joined to the service once and raised through one multicast, so an extension's own -= finds the
    // handler it added. The service raises with a bare Changed?.Invoke(), so a throwing handler would
    // otherwise come out of whatever made the change, which is some other surface's send.
    /// <inheritdoc />
    public event Action? Changed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            _handlers += value;
            if (!_joined)
            {
                _joined = true;
                _queue.Changed += Raise;
            }
        }
        remove => _handlers -= value;
    }

    /// <inheritdoc />
    public int Add(IEnumerable<ReviewClip> clips, string? sectionTitle = null, string sectionSubtitle = "")
    {
        ArgumentNullException.ThrowIfNull(clips);
        Require(nameof(Add));

        // A clip read back from Entries carries the id of a row that is still queued, and the queue's own
        // dedup cannot catch every repeat: it ignores section cards, and a queued clip carrying a highlight
        // ref never matches one projected without it. The id is the key every mutation names, so a repeat
        // gets a fresh one rather than a second row nothing can address.
        HashSet<Guid> held = [.. _queue.Entries.Select(e => e.Id)];
        return _queue.Add(clips.Select(clip => ToEntry(clip, held)), sectionTitle, sectionSubtitle);
    }

    /// <inheritdoc />
    public void AddSection(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Require(nameof(AddSection));
        _queue.AddSection(title);
    }

    /// <inheritdoc />
    public int Remove(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Require(nameof(Remove));
        return _queue.Remove(ids);
    }

    /// <inheritdoc />
    public void MoveBy(Guid id, int delta)
    {
        Require(nameof(MoveBy));
        _queue.Move(id, delta);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Require(nameof(Clear));
        _queue.Clear();
    }

    /// <inheritdoc />
    public int SetReviewed(IEnumerable<Guid> ids, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Require(nameof(SetReviewed));
        return _queue.SetReviewed(ids, reviewed);
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewClip> ClipsUnder(Guid cardId)
    {
        Require(nameof(ClipsUnder));
        return [.. _queue.ClipsUnder(cardId).Select(Project)];
    }

    /// <inheritdoc />
    public bool SetTitle(Guid id, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Require(nameof(SetTitle));
        return _queue.SetTitle(id, title);
    }

    /// <inheritdoc />
    public bool SetNote(Guid id, string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        Require(nameof(SetNote));
        return _queue.SetNote(id, note);
    }

    /// <inheritdoc />
    public bool SetQuestion(Guid id, string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        Require(nameof(SetQuestion));
        return _queue.SetQuestion(id, question);
    }

    private ReviewClip Project(ReviewEntry entry) => _projected.GetValue(entry, static e => new ReviewClip
    {
        Id = e.Id,
        Kind = e.Kind == ReviewEntryKind.Section ? ReviewClipKind.Section : ReviewClipKind.Clip,
        Title = e.Title,
        DemoPath = e.DemoPath,
        Sha256 = e.Sha256,
        FromTick = e.FromTick,
        ToTick = e.ToTick,
        TickRate = e.TickRate,
        Note = e.Note,
        Question = e.Question,
        Source = e.Source,
        TeamId = e.TeamId,
        Reviewed = e.Reviewed
    });

    // The one translation that goes the other way, and only for a row being added: the persisted fields a
    // ReviewClip does not carry (the highlight ref, the lineup id, the file's extension data) have no
    // value to invent, and no caller can name a row this way afterwards.
    private static ReviewEntry ToEntry(ReviewClip clip, HashSet<Guid> held)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Guid id = clip.Id != Guid.Empty && held.Add(clip.Id) ? clip.Id : Guid.NewGuid();
        return clip.Kind == ReviewClipKind.Section
            ? ReviewEntry.Section(clip.Title, clip.Note) with { Id = id }
            : ReviewEntry.Clip(clip.DemoPath, clip.FromTick, clip.ToTick, clip.Note, clip.Source, clip.TickRate,
                clip.Sha256) with
            {
                Id = id,
                Question = clip.Question,
                TeamId = clip.TeamId,
                Reviewed = clip.Reviewed
            };
    }

    private void Raise()
    {
        if (_guard is { } guard)
        {
            _changed.Invoke(guard.Faults, _handlers, 0, static (handler, _) => handler());
            return;
        }

        _handlers?.Invoke();
    }

    private void Require(string member)
    {
        if (!_onUiThread())
        {
            throw new InvalidOperationException(
                $"IExtensionReviewQueue.{member} is UI-thread only; post the call to the UI thread.");
        }
    }
}
