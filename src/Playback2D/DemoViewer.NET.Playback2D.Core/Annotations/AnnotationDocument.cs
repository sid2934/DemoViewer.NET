#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     The annotation model for one demo: an ordered element list plus a delta-stack undo history.
///     <para>
///         <b>One gesture is one undo entry.</b> <see cref="BeginGesture" /> opens a mark; every
///         <see cref="Apply" /> until the handle is disposed lands in the same batch, and disposing pushes
///         that batch as a SINGLE history entry — so a 400-sample stroke and a drag-erase across thirty
///         strokes each cost the user exactly one Ctrl+Z. A gesture that produced no deltas pushes
///         nothing (plan decision D4).
///     </para>
///     <para>
///         <b>Inverses are computed at apply time</b>, not stored in the delta: a <c>Remove</c>'s inverse
///         needs the element that was removed, and a <c>Replace</c>'s needs the value it displaced. That
///         is what keeps <see cref="DocDelta" /> minimal and serializable.
///     </para>
///     <para>
///         <b>The history holds annotations and nothing else</b> (design risk 13). There is no reference
///         to a camera, a playhead or a selection anywhere on this type, so "undo after a seek" can only
///         ever undo the stroke.
///     </para>
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gesture handle is owned by the CALLER that opened it (the pointer tool); the " +
                    "document only tracks which one is currently open, and disposing it from here " +
                    "would commit an undo entry the caller never asked to commit.")]
public sealed class AnnotationDocument
{
    /// <summary>
    ///     Hard cap on undo entries. A stroke is ~1 KB of samples, so 200 gestures is generous and still
    ///     bounded — the repo's "no unbounded buffer" invariant. The oldest entry is dropped.
    /// </summary>
    public const int MaxHistoryEntries = 200;

    private readonly List<AnnotationElement> _elements = [];
    private readonly Dictionary<Guid, int> _index = [];
    private readonly List<GestureStep> _open = [];
    private readonly List<List<GestureStep>> _redo = [];
    private readonly List<List<GestureStep>> _undo = [];

    private GestureHandle? _gesture;

    /// <summary>The live elements, oldest first. Draw order is list order; the LAST element is topmost.</summary>
    public IReadOnlyList<AnnotationElement> Elements => _elements;

    /// <summary>
    ///     Bumped on every mutation, including migrations and level remaps. The ink layer re-records its
    ///     cached pictures when this changes, so it must never go backwards or repeat.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>How many undo entries are available.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>How many redo entries are available.</summary>
    public int RedoDepth => _redo.Count;

    /// <summary>True while a gesture handle is open.</summary>
    public bool IsGestureOpen => _gesture is not null;

    /// <summary>The open gesture's name, or null. Diagnostics only.</summary>
    public string? OpenGestureName => _gesture?.Name;

    /// <summary>
    ///     Raised after any mutation, once per mutation, <b>and once more when a gesture closes</b> — the
    ///     moment its deltas become a single undo entry and <see cref="UndoDepth" /> finally moves.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    ///     Opens an undo mark. Every <see cref="Apply" /> until the returned handle is disposed squashes
    ///     into one history entry; disposing a gesture that produced no deltas pushes nothing.
    ///     <para>
    ///         <b>Non-reentrant.</b> The router guarantees exactly one active tool, so a second open
    ///         gesture is a bug — and a silently nested one would merge two users' worth of intent into a
    ///         single undo entry.
    ///     </para>
    /// </summary>
    /// <param name="name">Diagnostic name, e.g. <c>"draw"</c> or <c>"erase"</c>.</param>
    /// <exception cref="InvalidOperationException">A gesture is already open.</exception>
    public IDisposable BeginGesture(string name)
    {
        if (_gesture is not null)
        {
            throw new InvalidOperationException(
                $"A gesture ('{_gesture.Name}') is already open; annotation gestures do not nest.");
        }

        _open.Clear();
        _gesture = new GestureHandle(this, name);
        return _gesture;
    }

    /// <summary>
    ///     Esc: rolls the open gesture back to its mark and closes it, adding NO undo entry. Returns
    ///     false when no gesture is open or it had produced nothing.
    /// </summary>
    public bool BailToMark()
    {
        if (_gesture is null)
        {
            return false;
        }

        _gesture.Bailed = true;
        bool rolled = RollBackOpen();
        _gesture = null;
        _open.Clear();

        if (rolled)
        {
            Bump();
        }

        return rolled;
    }

    /// <summary>
    ///     Applies a delta as a USER action: it is invertible, it lands on the undo stack (directly, or
    ///     into the open gesture) and it clears the redo stack.
    /// </summary>
    /// <param name="delta">The mutation.</param>
    public void Apply(DocDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (!TryApply(delta, out DocDelta? inverse))
        {
            return;
        }

        _redo.Clear();

        GestureStep step = new(delta, inverse!);
        if (_gesture is not null)
        {
            _open.Add(step);
        }
        else
        {
            PushUndo([step]);
        }

        Bump();
    }

    /// <summary>
    ///     Applies a delta as a MIGRATION: it bumps <see cref="Version" /> and raises
    ///     <see cref="Changed" />, but pushes nothing onto either stack and clears neither.
    ///     <para>
    ///         For level-set rebases and schema migrations — the user did not act, so Ctrl+Z must not
    ///         restore a state that describes a level set which no longer exists (plan correction 9).
    ///     </para>
    /// </summary>
    /// <param name="delta">The mutation.</param>
    public void ApplyMigration(DocDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (TryApply(delta, out DocDelta? _))
        {
            Bump();
        }
    }

    /// <summary>Undoes the newest history entry. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        List<GestureStep> entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        for (int i = entry.Count - 1; i >= 0; i--)
        {
            TryApply(entry[i].Inverse, out DocDelta? _);
        }

        _redo.Add(entry);
        Bump();
        return true;
    }

    /// <summary>Redoes the newest undone entry. Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        List<GestureStep> entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        for (int i = 0; i < entry.Count; i++)
        {
            TryApply(entry[i].Applied, out DocDelta? _);
        }

        _undo.Add(entry);
        Bump();
        return true;
    }

    /// <summary>Looks an element up by id.</summary>
    /// <param name="id">The element's identity.</param>
    /// <param name="element">The element, when found.</param>
    public bool TryGet(Guid id, out AnnotationElement element)
    {
        if (_index.TryGetValue(id, out int at))
        {
            element = _elements[at];
            return true;
        }

        element = null!;
        return false;
    }

    /// <summary>The element's position in <see cref="Elements" />, or -1.</summary>
    /// <param name="id">The element's identity.</param>
    public int IndexOf(Guid id) => _index.TryGetValue(id, out int at) ? at : -1;

    /// <summary>
    ///     Rewrites every <see cref="SpaceRef.World" /> anchor whose <c>LevelMinZ</c> appears in
    ///     <paramref name="zMinMap" />, in the LIVE elements <b>and</b> in every element captured in the
    ///     undo/redo history.
    ///     <para>
    ///         <b>History-transparent</b> (plan decision D6): a level-set rebuild is a system event, not a
    ///         user gesture. It consumes no undo slot, and rewriting the history is what stops a later
    ///         Ctrl+Z from restoring an anchor pointing at a level that no longer exists.
    ///     </para>
    /// </summary>
    /// <param name="zMinMap">Old quantized level ZMin → new quantized level ZMin.</param>
    public void RemapWorldLevels(IReadOnlyDictionary<double, double> zMinMap)
    {
        ArgumentNullException.ThrowIfNull(zMinMap);

        if (zMinMap.Count == 0)
        {
            return;
        }

        bool changed = false;

        for (int i = 0; i < _elements.Count; i++)
        {
            AnnotationElement remapped = RemapElement(_elements[i], zMinMap);
            if (!ReferenceEquals(remapped, _elements[i]))
            {
                _elements[i] = remapped;
                changed = true;
            }
        }

        changed |= RemapHistory(_undo, zMinMap);
        changed |= RemapHistory(_redo, zMinMap);
        changed |= RemapSteps(_open, zMinMap);

        if (changed)
        {
            Bump();
        }
    }

    /// <summary>
    ///     Bulk load (persistence, tests, "clear all" with an empty sequence). Clears the history — a
    ///     load is not an action the user can undo into the previous demo's ink — and raises exactly one
    ///     <see cref="Changed" />.
    /// </summary>
    /// <param name="elements">The elements to hold, oldest first.</param>
    public void Reset(IEnumerable<AnnotationElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        _gesture = null;
        _open.Clear();
        _undo.Clear();
        _redo.Clear();
        _elements.Clear();
        _index.Clear();

        foreach (AnnotationElement element in elements)
        {
            if (element is null || _index.ContainsKey(element.Id))
            {
                continue;
            }

            _index[element.Id] = _elements.Count;
            _elements.Add(element);
        }

        Bump();
    }

    // ── Mutation core. Returns false when the delta changed nothing (an absent id), in which case no
    //    inverse exists and nothing is recorded — that is what makes a no-hit erase gesture cost zero
    //    undo entries.
    private bool TryApply(DocDelta delta, out DocDelta? inverse)
    {
        switch (delta)
        {
            case DocDelta.Add add:
                return ApplyAdd(add, out inverse);
            case DocDelta.Remove remove:
                return ApplyRemove(remove, out inverse);
            case DocDelta.Replace replace:
                return ApplyReplace(replace, out inverse);
            case DocDelta.Batch batch:
                return ApplyBatch(batch, out inverse);
            default:
                inverse = null;
                return false;
        }
    }

    private bool ApplyAdd(DocDelta.Add add, out DocDelta? inverse)
    {
        inverse = null;
        if (add.Element is null || _index.ContainsKey(add.Element.Id))
        {
            return false;
        }

        int at = Math.Clamp(add.Index, 0, _elements.Count);
        _elements.Insert(at, add.Element);
        Reindex(at);
        inverse = new DocDelta.Remove(add.Element.Id);
        return true;
    }

    private bool ApplyRemove(DocDelta.Remove remove, out DocDelta? inverse)
    {
        inverse = null;
        if (!_index.TryGetValue(remove.Id, out int at))
        {
            return false;
        }

        AnnotationElement removed = _elements[at];
        _elements.RemoveAt(at);
        _index.Remove(remove.Id);
        Reindex(at);
        inverse = new DocDelta.Add(removed, at);
        return true;
    }

    private bool ApplyReplace(DocDelta.Replace replace, out DocDelta? inverse)
    {
        inverse = null;
        if (replace.Element is null || !_index.TryGetValue(replace.Id, out int at))
        {
            return false;
        }

        AnnotationElement previous = _elements[at];
        _elements[at] = replace.Element;
        if (replace.Element.Id != replace.Id)
        {
            _index.Remove(replace.Id);
        }

        _index[replace.Element.Id] = at;

        // The inverse keys on the NEW id, because that is what sits at this position afterwards.
        inverse = new DocDelta.Replace(replace.Element.Id, previous);
        return true;
    }

    private bool ApplyBatch(DocDelta.Batch batch, out DocDelta? inverse)
    {
        inverse = null;
        if (batch.Items is null || batch.Items.Count == 0)
        {
            return false;
        }

        List<DocDelta> inverses = new(batch.Items.Count);
        for (int i = 0; i < batch.Items.Count; i++)
        {
            if (TryApply(batch.Items[i], out DocDelta? step) && step is not null)
            {
                inverses.Add(step);
            }
        }

        if (inverses.Count == 0)
        {
            return false;
        }

        inverses.Reverse();
        inverse = new DocDelta.Batch(inverses);
        return true;
    }

    private void Reindex(int from)
    {
        for (int i = Math.Max(0, from); i < _elements.Count; i++)
        {
            _index[_elements[i].Id] = i;
        }
    }

    private void PushUndo(List<GestureStep> entry)
    {
        _undo.Add(entry);
        if (_undo.Count > MaxHistoryEntries)
        {
            _undo.RemoveAt(0);
        }
    }

    private bool RollBackOpen()
    {
        bool rolled = false;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            rolled |= TryApply(_open[i].Inverse, out DocDelta? _);
        }

        return rolled;
    }

    private void CloseGesture(GestureHandle handle)
    {
        if (!ReferenceEquals(_gesture, handle))
        {
            return;
        }

        _gesture = null;

        if (handle.Bailed || _open.Count == 0)
        {
            _open.Clear();
            return;
        }

        PushUndo([.. _open]);
        _open.Clear();

        // Closing a gesture is when its deltas actually BECOME an undo entry: until now they sat in the
        // open batch and UndoDepth still read zero. Announce it, or every consumer tracking undo depth —
        // the toolbar's undo button first among them — stays stale until the next unrelated mutation.
        //
        // Version is deliberately NOT bumped: no content changed, and bumping it would make the ink
        // layer re-record every level's dry picture at the end of every single stroke.
        Changed?.Invoke();
    }

    private void Bump()
    {
        Version++;
        Changed?.Invoke();
    }

    private static bool RemapHistory(List<List<GestureStep>> stacks,
        IReadOnlyDictionary<double, double> zMinMap)
    {
        bool changed = false;
        for (int i = 0; i < stacks.Count; i++)
        {
            changed |= RemapSteps(stacks[i], zMinMap);
        }

        return changed;
    }

    private static bool RemapSteps(List<GestureStep> steps, IReadOnlyDictionary<double, double> zMinMap)
    {
        bool changed = false;
        for (int i = 0; i < steps.Count; i++)
        {
            DocDelta applied = RemapDelta(steps[i].Applied, zMinMap);
            DocDelta inverted = RemapDelta(steps[i].Inverse, zMinMap);
            if (ReferenceEquals(applied, steps[i].Applied) && ReferenceEquals(inverted, steps[i].Inverse))
            {
                continue;
            }

            steps[i] = new GestureStep(applied, inverted);
            changed = true;
        }

        return changed;
    }

    private static DocDelta RemapDelta(DocDelta delta, IReadOnlyDictionary<double, double> zMinMap)
    {
        switch (delta)
        {
            case DocDelta.Add add:
            {
                AnnotationElement remapped = RemapElement(add.Element, zMinMap);
                return ReferenceEquals(remapped, add.Element) ? add : new DocDelta.Add(remapped, add.Index);
            }

            case DocDelta.Replace replace:
            {
                AnnotationElement remapped = RemapElement(replace.Element, zMinMap);
                return ReferenceEquals(remapped, replace.Element)
                    ? replace
                    : new DocDelta.Replace(replace.Id, remapped);
            }

            case DocDelta.Batch batch:
            {
                List<DocDelta>? rebuilt = null;
                for (int i = 0; i < batch.Items.Count; i++)
                {
                    DocDelta item = RemapDelta(batch.Items[i], zMinMap);
                    if (!ReferenceEquals(item, batch.Items[i]) && rebuilt is null)
                    {
                        rebuilt = new List<DocDelta>(batch.Items);
                    }

                    if (rebuilt is not null)
                    {
                        rebuilt[i] = item;
                    }
                }

                return rebuilt is null ? batch : new DocDelta.Batch(rebuilt);
            }

            default:
                return delta;
        }
    }

    private static AnnotationElement RemapElement(AnnotationElement element,
        IReadOnlyDictionary<double, double> zMinMap)
    {
        if (element?.Space is not SpaceRef.World world
            || !zMinMap.TryGetValue(world.LevelMinZ, out double target)
            || target.Equals(world.LevelMinZ))
        {
            return element!;
        }

        return element with
        {
            Space = new SpaceRef.World(target)
        };
    }

    private readonly record struct GestureStep(DocDelta Applied, DocDelta Inverse);

    private sealed class GestureHandle(AnnotationDocument owner, string name) : IDisposable
    {
        private bool _disposed;

        public string Name { get; } = name;

        public bool Bailed { get; set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.CloseGesture(this);
        }
    }
}
