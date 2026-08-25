#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     Owns the layer stack and its draw order. Layers are kept sorted by <c>(Slot, Order, Id)</c> —
///     <c>Id</c> is the final tiebreaker rather than insertion order so the sequence is a pure function
///     of the registered set, and a golden image cannot silently depend on registration timing.
/// </summary>
public sealed class SceneCompositor : IDisposable
{
    private readonly List<ISceneLayer> _layers = [];
    private bool _disposed;

    /// <summary>The registered layers in draw order.</summary>
    public IReadOnlyList<ISceneLayer> Layers => _layers;

    /// <summary>Disposes every registered layer and clears the stack. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ISceneLayer layer in _layers)
        {
            layer.Dispose();
        }

        _layers.Clear();
    }

    /// <summary>Registers a layer and re-sorts the stack.</summary>
    /// <param name="layer">The layer to add.</param>
    /// <exception cref="ArgumentException">A layer with the same <see cref="ISceneLayer.Id" /> is registered.</exception>
    public void Add(ISceneLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

        if (Find(layer.Id) is not null)
        {
            throw new ArgumentException($"A layer with id '{layer.Id}' is already registered.", nameof(layer));
        }

        _layers.Add(layer);
        _layers.Sort(CompareLayers);
    }

    /// <summary>Removes and disposes the layer with this id. Returns false when it was not registered.</summary>
    /// <param name="layerId">The layer's stable id.</param>
    public bool Remove(string layerId)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            if (!string.Equals(_layers[i].Id, layerId, StringComparison.Ordinal))
            {
                continue;
            }

            ISceneLayer removed = _layers[i];
            _layers.RemoveAt(i);
            removed.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>The layer with this id, or null.</summary>
    /// <param name="layerId">The layer's stable id.</param>
    public ISceneLayer? Find(string layerId)
    {
        foreach (ISceneLayer layer in _layers)
        {
            if (string.Equals(layer.Id, layerId, StringComparison.Ordinal))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>Enables or disables one layer. A no-op when the id is not registered.</summary>
    /// <param name="layerId">The layer's stable id.</param>
    /// <param name="enabled">The new enabled state.</param>
    public void SetEnabled(string layerId, bool enabled)
    {
        if (Find(layerId) is { } layer)
        {
            layer.IsEnabled = enabled;
        }
    }

    /// <summary>
    ///     Advances every enabled layer. Returns the OR of their results — true means at least one layer
    ///     is still animating, so the caller keeps the self-terminating render loop armed. Every layer is
    ///     advanced even once one has returned true, because Advance is where they mutate.
    /// </summary>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="frame">The frame being advanced to.</param>
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        bool keepArmed = false;
        foreach (ISceneLayer layer in _layers)
        {
            if (layer.IsEnabled)
            {
                keepArmed |= layer.Advance(in time, frame);
            }
        }

        return keepArmed;
    }

    /// <summary>
    ///     Draws every enabled layer into one pane, in order. B1 adds a
    ///     <c>Render(SKCanvas, in SceneSubmission)</c> overload for multi-pane layouts rather than
    ///     changing this signature (decision D9).
    /// </summary>
    /// <param name="canvas">The pane's canvas.</param>
    /// <param name="ctx">The pane's render context.</param>
    public void Render(SKCanvas canvas, in SceneRenderContext ctx)
    {
        foreach (ISceneLayer layer in _layers)
        {
            if (layer.IsEnabled)
            {
                layer.Render(canvas, ctx);
            }
        }
    }

    private static int CompareLayers(ISceneLayer a, ISceneLayer b)
    {
        int bySlot = ((int)a.Slot).CompareTo((int)b.Slot);
        if (bySlot != 0)
        {
            return bySlot;
        }

        int byOrder = a.Order.CompareTo(b.Order);
        return byOrder != 0 ? byOrder : string.CompareOrdinal(a.Id, b.Id);
    }
}
