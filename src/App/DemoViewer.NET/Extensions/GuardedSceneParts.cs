#region

using System.Text.RegularExpressions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Tools;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>Where an extension's scene layers are filed, so they can never take a host layer's id.</summary>
public static partial class ExtensionLayerIds
{
    /// <summary>The root every extension layer id starts with. No host layer id does.</summary>
    public const string Root = "ext.";

    /// <summary>The id an extension's layer is filed under: <c>ext.&lt;extension id&gt;.&lt;id&gt;</c>.</summary>
    /// <param name="extensionId">The extension's id.</param>
    /// <param name="id">The layer's id within the extension.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> is empty or has a character outside letters, digits, '.', '-' and '_'.</exception>
    public static string Compose(string extensionId, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!LocalId().IsMatch(id))
        {
            throw new ArgumentException($"'{id}' is not a layer id: use letters, digits, '.', '-' and '_'.", nameof(id));
        }

        return Root + extensionId + "." + id;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex LocalId();
}

/// <summary>
///     An extension's scene layer as the compositor sees it: filed under its composed id, every call into it
///     run as the extension's, and the canvas restored after each render. A layer that throws stops drawing.
///     <see cref="ISceneLayer.Render" /> runs on the render thread; the fault counter is thread-safe.
/// </summary>
internal sealed class GuardedSceneLayer : ISceneLayer
{
    private readonly ExtensionGuard _guard;
    private readonly ISceneLayer _inner;
    private volatile bool _faulted;

    public GuardedSceneLayer(string id, ISceneLayer inner, ExtensionGuard guard)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        Id = id;

        // Read once: the compositor sorts and caches by them on every frame.
        Slot = guard.Run("scene layer", () => inner.Slot, LayerSlot.Overlay);
        Order = guard.Run("scene layer", () => inner.Order, 0);
        Cache = guard.Run("scene layer", () => inner.Cache, LayerCacheHint.Dynamic);
    }

    /// <summary>True once the layer threw; it draws nothing more.</summary>
    public bool IsFaulted => _faulted;

    public string Id { get; }

    public LayerSlot Slot { get; }

    public int Order { get; }

    public LayerCacheHint Cache { get; }

    // The inner layer's own flag counts: its owner may switch it off on the instance it holds.
    public bool IsEnabled
    {
        get
        {
            if (_faulted)
            {
                return false;
            }

            try
            {
                return _inner.IsEnabled;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fault("scene layer", ex);
                return false;
            }
        }
        set => _guard.Run("scene layer", () => _inner.IsEnabled = value);
    }

    public int ContentVersion => _faulted ? -1 : _guard.Run("scene layer", () => _inner.ContentVersion, 0, FaultKind.Recurring);

    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        if (_faulted)
        {
            return false;
        }

        try
        {
            return _inner.Advance(in time, frame);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fault("scene layer advance", ex);
            return false;
        }
    }

    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (_faulted)
        {
            return;
        }

        int saved = canvas.Save();
        try
        {
            _inner.Render(canvas, ctx);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fault("scene layer render", ex);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    public void Dispose() => _guard.Run("scene layer dispose", _inner.Dispose);

    private void Fault(string site, Exception ex)
    {
        _faulted = true;
        _guard.Report(site, ex, FaultKind.Recurring);
    }
}

/// <summary>An extension's map tool with every call run as the extension's. A throwing press is a refused one.</summary>
internal sealed class GuardedMapTool(IMapTool inner, ExtensionGuard guard) : IMapTool
{
    public IMapTool Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public bool OnPressed(in MapToolEvent e, IMapToolContext context)
    {
        MapToolEvent sample = e;
        return guard.Run("map tool", () => Inner.OnPressed(in sample, context), false, FaultKind.Recurring);
    }

    public void OnMoved(in MapToolEvent e, IMapToolContext context)
    {
        MapToolEvent sample = e;
        guard.Run("map tool", () => Inner.OnMoved(in sample, context), FaultKind.Recurring);
    }

    public void OnReleased(in MapToolEvent e, IMapToolContext context)
    {
        MapToolEvent sample = e;
        guard.Run("map tool", () => Inner.OnReleased(in sample, context), FaultKind.Recurring);
    }

    public void OnCancelled(IMapToolContext context) => guard.Run("map tool", () => Inner.OnCancelled(context), FaultKind.Recurring);
}

/// <summary>A layer that draws nothing: what a layer factory that threw builds instead.</summary>
internal sealed class EmptySceneLayer : ISceneLayer
{
    public static readonly EmptySceneLayer Instance = new();

    public string Id => "";

    public LayerSlot Slot => LayerSlot.Overlay;

    public int Order => 0;

    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    public bool IsEnabled { get; set; }

    public int ContentVersion => 0;

    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
///     An extension's <see cref="IPointerTool" /> on a scene view, every call run as the extension's. A throw
///     is reported and the sample dropped; the event is a ref struct, so the calls cannot go through a lambda.
/// </summary>
internal sealed class GuardedPointerTool(IPointerTool inner, ExtensionGuard guard) : IPointerTool
{
    public IPointerTool Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public ToolKind Kind => Inner.Kind;

    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        try
        {
            return Inner.OnPressed(in e, s);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            guard.Report("pointer tool", ex, FaultKind.Recurring);
            return false;
        }
    }

    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        try
        {
            Inner.OnMoved(in e, s);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            guard.Report("pointer tool", ex, FaultKind.Recurring);
        }
    }

    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        try
        {
            Inner.OnReleased(in e, s);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            guard.Report("pointer tool", ex, FaultKind.Recurring);
        }
    }

    public void OnCancelled(IToolServices s) => guard.Run("pointer tool", () => Inner.OnCancelled(s), FaultKind.Recurring);
}
