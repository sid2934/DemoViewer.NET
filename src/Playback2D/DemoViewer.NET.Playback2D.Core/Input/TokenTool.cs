#region

using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Drags the strat canvas's tokens (step-authoring.md §3.7). Press hits a token through
///     <see cref="IToolServices.Tokens" />, moves write it, release closes the drag as one edit, and Esc
///     rolls it back. The tool never sees a strat; <see cref="ITokenEditor" /> is the whole contract.
///     <para>
///         <b>One tool, two grips.</b> A press on the disc moves the token; a press on its heading stub
///         turns it toward the pointer instead. Shift held on release snaps a move's yaw to the drag
///         direction; a plain move keeps the keyframe's yaw.
///     </para>
///     <para>
///         <b>A miss is not a gesture.</b> No editor (the 2D Playback tab has none), no pane or no token
///         under the press returns false, so the press falls through unhandled and nothing opens.
///     </para>
///     <para>
///         <b>The level comes from the pane under the pointer</b>, sample by sample, which is how a token is
///         dragged onto another floor of a stacked map. A sample over no pane is skipped rather than
///         guessed.
///     </para>
/// </summary>
public sealed class TokenTool : IPointerTool
{
    private ITokenEditor? _editor;
    private TokenGrip _grip;
    private SKPoint _pressWorld;
    private string? _slot;

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.Token;

    /// <summary>Whether a drag is open.</summary>
    public bool IsDragging => _slot is not null;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (s.Tokens is not { } editor || e.Pane is not { } pane)
        {
            return false;
        }

        float radius = TokenHitTest.WorldRadius(s.WorldUnitsPerPixel(pane));
        if (!editor.TryHitToken(pane, e.World, radius, out string slot, out TokenGrip grip))
        {
            return false;
        }

        // The editor is captured at press: a host that swaps it mid-drag must not receive half a gesture.
        _editor = editor;
        _slot = slot;
        _grip = grip;
        _pressWorld = e.World;
        editor.BeginDrag(slot, grip);
        s.RequestRender();
        return true;
    }

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_editor is not { } editor || _slot is not { } slot || e.Pane is not { } pane)
        {
            return;
        }

        editor.MoveTo(slot, e.World, MapSpace.QuantizeZ(pane.Level.ZMin));
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_editor is not { } editor || _slot is not { } slot)
        {
            Clear();
            return;
        }

        // The release is a sample like any other: a drag whose last move event was dropped still lands
        // where the pointer let go.
        if (e.Pane is { } pane)
        {
            editor.MoveTo(slot, e.World, MapSpace.QuantizeZ(pane.Level.ZMin));
        }

        float? yaw = null;
        if (_grip == TokenGrip.Body && (e.Modifiers & ToolModifiers.Shift) != 0)
        {
            float dx = e.World.X - _pressWorld.X;
            float dy = e.World.Y - _pressWorld.Y;
            if (dx != 0 || dy != 0)
            {
                yaw = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
            }
        }

        Clear();
        editor.EndDrag(yaw);
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_editor is not { } editor)
        {
            return;
        }

        Clear();
        editor.CancelDrag();
        s.RequestRender();
    }

    private void Clear()
    {
        _editor = null;
        _slot = null;
    }
}
