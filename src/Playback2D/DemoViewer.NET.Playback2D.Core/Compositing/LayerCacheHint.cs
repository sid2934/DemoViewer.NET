namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     How cacheable a layer's drawing is (design §5.2). Declared rather than inferred so caching is
///     auditable: a layer that lies about this is a rendering bug with a name.
/// </summary>
public enum LayerCacheHint
{
    /// <summary>Output changes only when <c>ContentVersion</c> does, recordable into one SKPicture.</summary>
    Static,

    /// <summary>Output changes with the camera as well as the content.</summary>
    PerCamera,

    /// <summary>Output changes every frame; never recorded, and <c>ContentVersion</c> is ignored.</summary>
    Dynamic
}
