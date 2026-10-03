#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     An <see cref="ISceneFrameHost" /> with tokens to drag. Split out of the frame host contract
///     (strat-book-plugin.md §3.3) so a host with no tokens (the 2D Playback tab, the query canvas)
///     implements nothing extra for them.
/// </summary>
internal interface ITokenEditingHost
{
    /// <summary>The editor the token tool drags through; null while the host refuses edits (read-only).</summary>
    ITokenEditor? TokenEditor { get; }
}

/// <summary>An <see cref="ISceneFrameHost" /> with editing guides to draw over the scene.</summary>
internal interface IGuidesHost
{
    /// <summary>The guides for the current frame. Never null; <see cref="SceneGuides.None" /> draws nothing.</summary>
    SceneGuides Guides { get; }
}
