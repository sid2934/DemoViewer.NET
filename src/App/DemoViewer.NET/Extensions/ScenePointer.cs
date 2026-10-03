#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A primary press handed to a pointer pre-handler (<see cref="IPlaybackSurface.AddPointerPreHandler" />),
///     ahead of the pointer tools. <see cref="Zones" /> is lazy: the map's zones file is read only when a
///     handler actually calls it, so a press no handler wants never forces a zone load.
/// </summary>
/// <param name="Level">The floor the clicked pane shows.</param>
/// <param name="WorldX">World X of the click.</param>
/// <param name="WorldY">World Y of the click.</param>
/// <param name="Screen">Host-relative screen position.</param>
/// <param name="Modifiers">Modifiers at the time of the press.</param>
/// <param name="Frame">The frame on screen at the time of the press.</param>
/// <param name="Zones">The open map's zones, or null without any. Read on demand.</param>
public sealed record ScenePointer(MapLevel Level, double WorldX, double WorldY, SKPoint Screen,
    ToolModifiers Modifiers, Scene2DFrame Frame, Func<PlaceResolver?> Zones);
