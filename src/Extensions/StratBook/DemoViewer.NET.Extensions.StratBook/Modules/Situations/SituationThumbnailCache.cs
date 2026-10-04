namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     What a thumbnail is a picture of: one demo's one round at one matched tick, under the
///     fingerprint the rows were built with. The fingerprint is in the key so a rebuilt index can
///     never be shown through a picture of the old rows; the thumbnail size, camera and palette are
///     not, since a change to any of them ships with a golden and a new build.
/// </summary>
/// <param name="StableKey">The demo's derived-store join key.</param>
/// <param name="Round">The Round Facts round number.</param>
/// <param name="Tick">The matched tick, frame clock.</param>
/// <param name="Fingerprint">The index fingerprint in force for the demo's map.</param>
public readonly record struct SituationThumbnailKey(string StableKey, int Round, int Tick, string Fingerprint);

/// <summary>
///     The per-session thumbnail cache: PNG bytes keyed by <see cref="SituationThumbnailKey" />, in
///     memory only. Forty renders at half a millisecond are cheaper than scheduling them, so nothing is
///     written to disk; the cache exists so walking back over a result set, or re-running a query,
///     never re-renders a card. Bounded and least-recently-used, so a long session over many queries
///     holds a few hundred small PNGs at most. Thread-safe: the worker fills it and the UI reads it.
/// </summary>
public sealed class SituationThumbnailCache
{
    /// <summary>The default bound: a few result sets' worth, about ten megabytes of PNG at most.</summary>
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly Dictionary<SituationThumbnailKey, LinkedListNode<(SituationThumbnailKey Key, byte[] Png)>> _map = [];
    private readonly LinkedList<(SituationThumbnailKey Key, byte[] Png)> _order = [];
    private readonly object _gate = new();

    /// <param name="capacity">How many thumbnails to hold before the least recently used goes.</param>
    public SituationThumbnailCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>How many thumbnails are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>The PNG for a key, or null when it has not been rendered or was evicted.</summary>
    /// <param name="key">The thumbnail's identity.</param>
    public byte[]? TryGet(SituationThumbnailKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out LinkedListNode<(SituationThumbnailKey Key, byte[] Png)>? node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Png;
        }
    }

    /// <summary>Stores a rendered PNG, evicting the least recently used entry past the bound.</summary>
    /// <param name="key">The thumbnail's identity.</param>
    /// <param name="png">The rendered bytes.</param>
    public void Put(SituationThumbnailKey key, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<(SituationThumbnailKey Key, byte[] Png)>? existing))
            {
                _order.Remove(existing);
            }

            LinkedListNode<(SituationThumbnailKey Key, byte[] Png)> node = new((key, png));
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>Forgets everything: a rebuild has made every picture a picture of old rows.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
