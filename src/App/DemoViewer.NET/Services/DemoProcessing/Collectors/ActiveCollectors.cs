namespace DemoViewer.NET.Services.DemoProcessing.Collectors;

/// <summary>
///     The collectors of every retained visit in flight, by path. Two visits of one path can run passes on one
///     parse at once (an open joins a background parse, then both run their passes), and they share that parse's
///     cached run, so a path answers the union of its live visits and each visit retracts only its own.
/// </summary>
internal sealed class ActiveCollectors
{
    private readonly Dictionary<string, List<Registration>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Adds <paramref name="collectors" /> to <paramref name="path" /> until the result is disposed.</summary>
    public IDisposable Begin(string path, CollectorSet collectors)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(collectors);
        Registration registration = new(this, path, collectors);
        lock (_gate)
        {
            if (!_byPath.TryGetValue(path, out List<Registration>? live))
            {
                _byPath[path] = live = [];
            }

            live.Add(registration);
        }

        return registration;
    }

    /// <summary>The union of the collectors of every visit of <paramref name="path" /> in flight.</summary>
    public CollectorSet For(string path)
    {
        lock (_gate)
        {
            if (!_byPath.TryGetValue(path, out List<Registration>? live))
            {
                return CollectorSet.Empty;
            }

            CollectorSet union = CollectorSet.Empty;
            foreach (Registration r in live)
            {
                union = union.Union(r.Collectors);
            }

            return union;
        }
    }

    private void End(Registration registration)
    {
        lock (_gate)
        {
            if (_byPath.TryGetValue(registration.Path, out List<Registration>? live)
                && live.Remove(registration) && live.Count == 0)
            {
                _byPath.Remove(registration.Path);
            }
        }
    }

    private sealed class Registration(ActiveCollectors owner, string path, CollectorSet collectors) : IDisposable
    {
        private int _ended;

        public string Path => path;

        public CollectorSet Collectors => collectors;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                owner.End(this);
            }
        }
    }
}
