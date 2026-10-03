#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     What <see cref="StratCanvasViewModel" /> needs from outside that it used to reach through
///     <c>App.Services</c>: the live feature gate (token-routing reactivity), the zone place resolver, and
///     the settings read for keybind overrides. One bundle so callers two hops away
///     (<c>StratBookTabViewModel</c>, <c>DetectedStratsViewModel</c>) pass one parameter instead of three.
///     A null member means the same as no host did before: routing off, the baked-in place resolver, no
///     keybind overrides.
/// </summary>
public sealed record StratCanvasServices(IFeatureGate? Gate, IZonePlaceResolverSource? Places, SettingsService? Settings);
