#region

using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>
///     Keeps every tag instance's <see cref="TagInstance.Facts" /> in step with its round's Round Facts
///     (tag-store.md §3.3): the parser namespace, rewritten wholesale, never reading or writing
///     <see cref="TagInstance.Labels" />.
///     <para>
///         <b>When.</b> On <see cref="IRoundFactsSource.Updated" />, which the evaluator raises after it
///         (re)writes a demo's rows, so a re-parse or a Round Facts schema bump reaches every document of
///         that demo. The design names <c>DemoCacheStore.Changed</c> and a comparison against
///         <c>factsStamp.computedUtc</c>; the rows carry no write time, and <c>Updated</c> fires for
///         exactly the writes that matter, so that is the trigger. A fresh instance does not wait for a
///         re-parse: <see cref="TagSession" /> stamps it through <see cref="RefreshInstance" /> as it is made.
///     </para>
///     <para>
///         <b>Where.</b> Through <see cref="TagStore.Update" />, which hands the change to the session when
///         the demo is open (applied outside its undo history) and does a locked read-modify-write of the
///         sidecar otherwise. A demo with no tag document is left alone: there is nothing to refresh, and
///         opening a demo must not leave a file behind.
///     </para>
///     <para>
///         <b>What.</b> Every label <see cref="IRoundFactsSource.FactsFor" /> reports for the round holding
///         the instance's <c>fromTick</c>, at that tick, under its plain name (overview correction 10: the
///         array is the namespace, so no <c>parser.</c> prefix). Values are absolute per side; no
///         <c>side</c>, <c>buy.us</c> or <c>buy.them</c> is written, because an instance has no side unless a
///         person or Team Identity says so.
///     </para>
/// </summary>
public sealed class TagFactsRefresher : IDisposable
{
    private readonly Action<Action> _background;
    private readonly IRoundFactsSource _facts;
    private readonly Func<string, string?> _sha256For;
    private readonly TagStore _tags;
    private readonly Func<DateTime> _utcNow;
    private int _disposed;

    /// <param name="tags">The tag store the documents live in.</param>
    /// <param name="facts">The Round Facts read API whose <c>Updated</c> drives the refresh.</param>
    /// <param name="sha256For">
    ///     Demo path to its content hash, the cache index's join; null when the demo has not been hashed,
    ///     in which case the rows' own <see cref="RoundFactsRows.DemoSha256" /> is tried.
    /// </param>
    /// <param name="background">
    ///     Runs a refresh off the raising thread: <c>Updated</c> is posted to the UI thread and a refresh
    ///     reads a cache sidecar and may rewrite a tag sidecar. Defaults to the thread pool; tests pass a
    ///     synchronous one.
    /// </param>
    /// <param name="utcNow">The stamp's clock.</param>
    public TagFactsRefresher(TagStore tags, IRoundFactsSource facts, Func<string, string?> sha256For,
        Action<Action>? background = null, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(sha256For);
        _tags = tags;
        _facts = facts;
        _sha256For = sha256For;
        _background = background ?? (action => _ = Task.Run(action));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _facts.Updated += OnUpdated;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _facts.Updated -= OnUpdated;
        }
    }

    /// <summary>
    ///     Refreshes the tag document of one demo from its current rows. False when there is nothing to
    ///     refresh from: no rows, or no hash to find the document by. A demo with no document is left to
    ///     <see cref="TagStore.Update" />, which does nothing for it, rather than checked here: a session's
    ///     document that has not reached its first autosave exists only in that session.
    /// </summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    public bool RefreshDemo(string demoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);

        RoundFactsRows? rows = _facts.TryGet(demoPath);
        if (rows is null)
        {
            return false;
        }

        string? sha = _sha256For(demoPath);
        if (string.IsNullOrEmpty(sha))
        {
            sha = rows.DemoSha256;
        }

        if (string.IsNullOrEmpty(sha))
        {
            return false;
        }

        // Captured here, not read inside the mutation: a routed update runs later on the UI thread, and
        // the stamp must say what the facts were computed from.
        int schema = _facts.Schema;
        DateTime now = _utcNow();
        _tags.Update(sha, document => Refresh(document, rows, schema, now));
        return true;
    }

    /// <summary>Refreshes every instance of a document against one demo's rows.</summary>
    /// <param name="document">The document; only derived fields change.</param>
    /// <param name="rows">The demo's rows.</param>
    /// <param name="schema">The Round Facts schema the rows were read under, for the stamp.</param>
    /// <param name="utcNow">The stamp's time.</param>
    public static void Refresh(TagDocument document, RoundFactsRows rows, int schema, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rows);

        foreach (TagInstance instance in document.Instances)
        {
            RefreshInstance(instance, rows.Rounds, schema, utcNow);
        }
    }

    /// <summary>
    ///     Sets one instance's <c>round</c>, <c>facts</c> and <c>factsStamp</c> from the round holding its
    ///     <c>fromTick</c>. A start that falls in no round keeps its old facts and round, stamped stale, so
    ///     a re-parse that lost a round does not erase what the Matrix was pivoting on.
    /// </summary>
    /// <param name="instance">The instance; its labels are not read.</param>
    /// <param name="rounds">The demo's rounds, in number order.</param>
    /// <param name="schema">The Round Facts schema, for the stamp.</param>
    /// <param name="utcNow">The stamp's time.</param>
    public static void RefreshInstance(TagInstance instance, IReadOnlyList<RoundFacts.RoundFacts> rounds, int schema,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(rounds);

        TagFactsStamp? old = instance.FactsStamp;
        if (RoundFactsSource.FindRound(rounds, instance.FromTick) is not { } round)
        {
            instance.FactsStamp = new TagFactsStamp
            {
                Schema = old?.Schema ?? schema,
                ComputedUtc = old?.ComputedUtc,
                Stale = true,
                Extra = old?.Extra
            };
            return;
        }

        instance.Round = round.Number;
        instance.Facts = FactsOf(round, instance.FromTick);
        instance.FactsStamp = new TagFactsStamp
        {
            Schema = schema,
            ComputedUtc = utcNow,
            Stale = false,
            Extra = old?.Extra
        };
    }

    /// <summary>
    ///     The adapter from Round Facts' label list to the document's: <see cref="FactLabel.Key" /> is the
    ///     group, and the list's own grouping (<c>buy</c>, <c>score</c>, ...) is display-only and dropped. The
    ///     same projection <see cref="IRoundFactsSource.FactsFor" /> makes, over rows already in hand, so a
    ///     document costs one sidecar read rather than one per instance.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="fromTick">The instance's start, for the tick-anchored facts.</param>
    public static List<TagLabel> FactsOf(RoundFacts.RoundFacts round, int fromTick) =>
        [.. RoundFactsSource.Labels(round, fromTick).Select(f => new TagLabel(f.Key, f.Value))];

    private void OnUpdated(string demoPath)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(demoPath))
        {
            return;
        }

        _background(() => RefreshDemo(demoPath));
    }
}
