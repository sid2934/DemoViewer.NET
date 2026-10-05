namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Per-demo data kept in memory for the session: the browser build, where the host keeps none, and tests.
///     Keyed by the demo's path; a demo that leaves <paramref name="library" /> loses its data, as on disk.
///     Change notices run on the calling thread.
/// </summary>
/// <param name="library">The library whose removals the data follows; null follows nothing.</param>
public sealed class MemoryDemoData(IExtensionLibrary? library = null) : IExtensionDemoData, IDisposable
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IExtensionLibrary, MemoryDemoData> Shared = new();

    /// <summary>The one in-memory data of <paramref name="library" />, so every store over that library shares it.</summary>
    /// <param name="library">The library.</param>
    public static MemoryDemoData For(IExtensionLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return Shared.GetValue(library, static l => new MemoryDemoData(l));
    }

    private readonly Dictionary<(string Path, string Facet), Item> _items = new(Comparer.Instance);
    private readonly Lock _gate = new();
    private bool _subscribed;

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public event Action<string?>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (library is not null && _subscribed)
        {
            library.Changed -= OnLibraryChanged;
        }
    }

    /// <inheritdoc />
    public DemoDataStamp? Stamp(string demoPath, string facet)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault((demoPath, facet))?.Stamp;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DemoDataStamp> Stamps(string facet)
    {
        lock (_gate)
        {
            return [.. _items.Where(kv => string.Equals(kv.Key.Facet, facet, StringComparison.Ordinal)).Select(kv => kv.Value.Stamp)];
        }
    }

    /// <inheritdoc />
    public byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null) =>
        ReadAny(demoPath, facet, part) is { } record && record.Schema == schema
                                                     && string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? record.Content
            : null;

    /// <inheritdoc />
    public DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue((demoPath, facet), out Item? item) || item.Written is not { } written)
            {
                return null;
            }

            byte[]? content = part is null ? written.Payload : written.Parts.GetValueOrDefault(part);
            return content is null ? null : new DemoDataRecord(written.Schema, written.Fingerprint, null, written.WrittenAtTicks, content);
        }
    }

    /// <inheritdoc />
    public DemoDataStamp? Write(string demoPath, DemoDataWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        long now = DateTime.UtcNow.Ticks;
        Written written = new(write.Schema, write.Fingerprint, now, write.Payload.ToArray(),
            write.Parts.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal));
        DemoDataStamp stamp = new(demoPath, Sha256Of(demoPath), write.Facet, write.Schema, write.Fingerprint, DemoDataState.Written, now,
            write.Count);
        lock (_gate)
        {
            Follow();
            _items[(demoPath, write.Facet)] = new Item(stamp, written);
        }

        Changed?.Invoke(demoPath);
        return stamp;
    }

    /// <inheritdoc />
    public void MarkFailed(string demoPath, string facet) => Mutate(demoPath, facet, s => s with { State = DemoDataState.Failed }, true);

    /// <inheritdoc />
    public void ClearFailed(string demoPath, string facet) =>
        Mutate(demoPath, facet, s => s.State == DemoDataState.Failed ? s with { State = DemoDataState.Pending } : s, false);

    /// <inheritdoc />
    public void SetCount(string demoPath, string facet, int count) => Mutate(demoPath, facet, s => s with { Count = count }, true);

    /// <inheritdoc />
    public void Invalidate(string facet, string? demoPath = null)
    {
        lock (_gate)
        {
            foreach (((string Path, string Facet) key, Item item) in _items.ToList())
            {
                if (string.Equals(key.Facet, facet, StringComparison.Ordinal)
                    && (demoPath is null || string.Equals(key.Path, demoPath, StringComparison.OrdinalIgnoreCase)))
                {
                    _items[key] = item with
                    {
                        Stamp = item.Stamp with
                        {
                            Fingerprint = null,
                            State = item.Stamp.State == DemoDataState.Failed ? DemoDataState.Pending : item.Stamp.State
                        }
                    };
                }
            }
        }

        Changed?.Invoke(demoPath);
    }

    /// <inheritdoc />
    public void Delete(string demoPath, string facet)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove((demoPath, facet));
        }

        if (removed)
        {
            Changed?.Invoke(demoPath);
        }
    }

    /// <summary>Puts a stamp and its content as they are, write time included: a test's fixture.</summary>
    /// <param name="stamp">The stamp; its path and facet are the key.</param>
    /// <param name="payload">The main content, or null for a stamp with nothing behind it.</param>
    /// <param name="parts">Further named contents.</param>
    internal void Put(DemoDataStamp stamp, byte[]? payload = null, IReadOnlyDictionary<string, byte[]>? parts = null)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        Written? written = payload is null
            ? null
            : new Written(stamp.Schema, stamp.Fingerprint, stamp.WrittenAtTicks, payload,
                parts?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? new Dictionary<string, byte[]>(StringComparer.Ordinal));
        lock (_gate)
        {
            Follow();
            _items[(stamp.DemoPath, stamp.Facet)] = new Item(stamp, written);
        }

        Changed?.Invoke(stamp.DemoPath);
    }

    private void Mutate(string demoPath, string facet, Func<DemoDataStamp, DemoDataStamp> change, bool create)
    {
        lock (_gate)
        {
            Follow();
            if (!_items.TryGetValue((demoPath, facet), out Item? item))
            {
                if (!create)
                {
                    return;
                }

                item = new Item(new DemoDataStamp(demoPath, Sha256Of(demoPath), facet, 0, null, DemoDataState.Pending, 0, 0), null);
            }

            _items[(demoPath, facet)] = item with { Stamp = change(item.Stamp) };
        }

        Changed?.Invoke(demoPath);
    }

    private string? Sha256Of(string demoPath) => library?.Find(demoPath)?.Sha256;

    // Called under the lock.
    private void Follow()
    {
        if (library is not null && !_subscribed)
        {
            _subscribed = true;
            library.Changed += OnLibraryChanged;
        }
    }

    private void OnLibraryChanged(LibraryChange change)
    {
        if (change.Path is not { } path || library?.Find(path) is not null)
        {
            return;
        }

        bool removed = false;
        lock (_gate)
        {
            foreach ((string Path, string Facet) key in _items.Keys.ToList())
            {
                if (string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    removed |= _items.Remove(key);
                }
            }
        }

        if (removed)
        {
            Changed?.Invoke(path);
        }
    }

    private sealed record Written(int Schema, string? Fingerprint, long WrittenAtTicks, byte[] Payload, Dictionary<string, byte[]> Parts);

    private sealed record Item(DemoDataStamp Stamp, Written? Written);

    private sealed class Comparer : IEqualityComparer<(string Path, string Facet)>
    {
        public static Comparer Instance { get; } = new();

        public bool Equals((string Path, string Facet) x, (string Path, string Facet) y) =>
            string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Facet, y.Facet, StringComparison.Ordinal);

        public int GetHashCode((string Path, string Facet) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), StringComparer.Ordinal.GetHashCode(obj.Facet));
    }
}
