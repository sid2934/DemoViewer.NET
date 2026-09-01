#region

using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     The whole of the <see cref="LayerCacheHint" /> mechanism: recorded <see cref="SKPicture" />s,
///     keyed so a stale one can never be replayed.
///     <para>
///         <b>Why the key has four parts.</b> <c>LevelId</c> because two panes draw different content;
///         <c>LayerId</c> because two layers do; <c>ContentVersion</c> because the layer says when its
///         content changed; and <c>CameraEpoch</c> because a <c>PerCamera</c> recording is in pane-local
///         screen space and is wrong the moment the camera moves. A <c>Static</c> recording is in world
///         space and replays under the camera matrix, so its key pins <c>CameraEpoch</c> to 0: that
///         difference is the entire reason both hints exist (plan decision D-6).
///     </para>
///     <para>
///         Eviction is least-recently-used with a hard cap, and an evicted picture is disposed
///         immediately: an <see cref="SKPicture" /> holds unmanaged draw commands the GC has no pressure
///         signal for.
///     </para>
/// </summary>
internal sealed class LayerPictureCache : IDisposable
{
    private readonly Dictionary<Key, SKPicture> _entries;
    private readonly List<Key> _order;
    private bool _disposed;

    public LayerPictureCache(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _entries = new Dictionary<Key, SKPicture>(Math.Min(Capacity, 32));
        _order = new List<Key>(Math.Min(Capacity, 32));
    }

    /// <summary>Maximum live pictures before the least recently used is evicted.</summary>
    public int Capacity { get; }

    /// <summary>Live picture count.</summary>
    public int Count => _entries.Count;

    /// <summary>Cumulative recordings, for <see cref="SceneCompositorStats" />.</summary>
    public int Recorded { get; private set; }

    /// <summary>Cumulative replays, for <see cref="SceneCompositorStats" />.</summary>
    public int Replayed { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    /// <summary>The cached picture for this key, or null. A hit refreshes its LRU position.</summary>
    public SKPicture? Get(in Key key)
    {
        if (!_entries.TryGetValue(key, out SKPicture? picture))
        {
            return null;
        }

        Touch(key);
        Replayed++;
        return picture;
    }

    /// <summary>Stores a freshly recorded picture, evicting as needed. The cache takes ownership.</summary>
    public void Put(in Key key, SKPicture picture)
    {
        if (_entries.TryGetValue(key, out SKPicture? existing))
        {
            existing.Dispose();
            _entries[key] = picture;
            Touch(key);
            Recorded++;
            return;
        }

        while (_entries.Count >= Capacity && _order.Count > 0)
        {
            Key oldest = _order[0];
            _order.RemoveAt(0);
            if (_entries.Remove(oldest, out SKPicture? evicted))
            {
                evicted.Dispose();
            }
        }

        _entries[key] = picture;
        _order.Add(key);
        Recorded++;
    }

    /// <summary>
    ///     Drops every picture recorded for one pane. Called when a level vanishes from the space:
    ///     otherwise its pictures would outlive the pane and hold an <see cref="SKImage" /> alive.
    /// </summary>
    public void InvalidatePane(MapLevelId levelId)
    {
        List<Key>? doomed = null;
        foreach (Key key in _entries.Keys)
        {
            if (key.LevelId == levelId)
            {
                (doomed ??= []).Add(key);
            }
        }

        if (doomed is null)
        {
            return;
        }

        foreach (Key key in doomed)
        {
            _order.Remove(key);
            if (_entries.Remove(key, out SKPicture? picture))
            {
                picture.Dispose();
            }
        }
    }

    /// <summary>Drops and disposes every picture.</summary>
    public void Clear()
    {
        foreach (SKPicture picture in _entries.Values)
        {
            picture.Dispose();
        }

        _entries.Clear();
        _order.Clear();
    }

    private void Touch(Key key)
    {
        int index = _order.IndexOf(key);
        if (index < 0 || index == _order.Count - 1)
        {
            return;
        }

        _order.RemoveAt(index);
        _order.Add(key);
    }

    /// <summary>A cache key. See the type doc for why each component is there.</summary>
    internal readonly record struct Key(MapLevelId LevelId, string LayerId, int ContentVersion, int CameraEpoch);
}
