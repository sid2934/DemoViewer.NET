#region

using Avalonia;
using Avalonia.Input;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     What a <see cref="SceneView" /> draws: the frame to show and the state the view cannot read off it. The
///     view re-reads every member on <see cref="FrameUpdated" /> and never writes back; the two calls hand a
///     decision to the source. Implement it on the view model the view binds.
///     <para>
///         <see cref="Frame" /> is a stable reference until the next <see cref="FrameUpdated" />: the render
///         thread replays the frame it was handed, so publish a new frame rather than changing one in place.
///     </para>
/// </summary>
public interface ISceneSource
{
    /// <summary>The frame to show.</summary>
    Scene2DFrame Frame { get; }

    /// <summary>The map's bundle, from <see cref="MapAssets.TryLoad" />, for its floors and radar art. Null draws the grid.</summary>
    IMapAsset? MapAsset { get; }

    /// <summary>The ink drawn over the scene and edited by the drawing tools. Null mounts no ink and no drawing tools.</summary>
    AnnotationSession? Ink { get; }

    /// <summary>A view cone on each marker, turned by dragging it through <see cref="TokenEditor" />.</summary>
    bool ShowViewCones => false;

    /// <summary>What a <see cref="ToolKind.Token" /> tool drags through. Null refuses token edits.</summary>
    ITokenEditor? TokenEditor => null;

    /// <summary>Raised on the UI thread after <see cref="Frame" /> or any other member changed.</summary>
    event Action? FrameUpdated;

    /// <summary>
    ///     The map's floors moved after a frame changed the level set. Rebase world-anchored ink here; the
    ///     view does it for nothing else.
    /// </summary>
    /// <param name="zMinMap">Old quantized floor ZMin to new quantized floor ZMin.</param>
    void OnLevelsMoved(IReadOnlyDictionary<double, double> zMinMap)
    {
    }

    /// <summary>
    ///     Sees a primary press before the tools do. Return true to take it. Not called for a press the view
    ///     turns into a pan (Space, Control and the middle button pan).
    /// </summary>
    /// <param name="press">The press, resolved to a floor and world coordinates.</param>
    bool OnPress(ScenePress press) => false;
}

/// <summary>A primary press on a <see cref="SceneView" />.</summary>
/// <param name="Level">The floor the pressed pane shows.</param>
/// <param name="WorldX">World X under the pointer.</param>
/// <param name="WorldY">World Y under the pointer.</param>
/// <param name="Screen">The pointer, in the view's coordinates.</param>
/// <param name="Modifiers">Keys held.</param>
public sealed record ScenePress(MapLevel Level, double WorldX, double WorldY, Point Screen, KeyModifiers Modifiers);
