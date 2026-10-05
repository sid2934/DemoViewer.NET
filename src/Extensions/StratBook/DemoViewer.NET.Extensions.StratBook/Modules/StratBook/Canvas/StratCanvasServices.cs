#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

/// <summary>
///     What <see cref="StratCanvasViewModel" /> needs from outside: the extension's feature switches (token
///     routing reacts to them), the zone place resolver, and the user's keybind overrides. One bundle so
///     callers two hops away (<c>StratBookTabViewModel</c>, <c>DetectedStratsViewModel</c>) pass one parameter
///     instead of three. A null member means: routing off, the baked-in place resolver, no keybind overrides,
///     place reads on the pool.
/// </summary>
public sealed record StratCanvasServices(IExtensionFeatures? Gate, IZonePlaceResolverSource? Places,
    Func<IEnumerable<string>>? KeybindOverrides,
    IExtensionJobs? Jobs = null);
