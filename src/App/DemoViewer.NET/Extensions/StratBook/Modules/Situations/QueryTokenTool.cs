#region

using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Query;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The Query Canvas pointer tool: places the armed rail slot, moves a placed token, lifts one.
///     <para>
///         <b>Press decides the gesture.</b> A left press with a rail slot armed starts placing that
///         slot; a left press on a placed token with nothing armed grabs it; a left press with neither
///         is refused, so the host can hand the drag to pan. A right press on a token lifts it. Moves
///         carry the token unresolved (the disc goes hollow), and the release is the one moment the
///         drop resolves to a place, so the resolver runs once per gesture rather than once per sample.
///     </para>
///     <para>
///         Cancel puts the token back where the press found it, or removes a placement that never
///         finished; a half-placed token must not survive Esc.
///     </para>
/// </summary>
public sealed class QueryTokenTool : IPointerTool
{
    /// <summary>How close to a token's centre a press must land, in screen pixels.</summary>
    public const float HitRadiusPx = QueryTokenLayer.Radius + 4f;

    private readonly QueryCanvasDocument _document;
    private readonly IQueryPlaceResolver _resolver;

    private QueryToken? _before;
    private (QuerySide Side, int Slot)? _dragging;

    /// <param name="document">The slots the tool mutates. Shared with the layer.</param>
    /// <param name="resolver">Turns a drop into a place.</param>
    public QueryTokenTool(QueryCanvasDocument document, IQueryPlaceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resolver);
        _document = document;
        _resolver = resolver;
    }

    /// <summary>The rail slot the next empty-map press places, or null. Set by the rail; cleared by the placement.</summary>
    public (QuerySide Side, int Slot)? Armed { get; set; }

    /// <summary>The token a gesture is carrying, or null between gestures.</summary>
    public (QuerySide Side, int Slot)? Dragging => _dragging;

    /// <summary>The place the last release resolved to, or null when it resolved nothing. For the rail's status line.</summary>
    public QueryPlaceHit? LastHit { get; private set; }

    /// <summary>
    ///     Raised after a release has placed its token, with what the drop resolved to. The rail reads
    ///     this rather than the document's change, because the document also changes on every carried
    ///     move, when there is no answer yet.
    /// </summary>
    public event Action<QueryPlaceHit?>? Dropped;

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.QueryToken;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (e.Pane is not { } pane)
        {
            return false;
        }

        QueryToken? under = TokenAt(pane, e.Screen, s);

        if (e.Button == ToolPointerButton.Right)
        {
            // A lift is a whole gesture on its own: the release that follows has nothing to do.
            if (under is not { } lifted)
            {
                return false;
            }

            _document.Lift(lifted.Side, lifted.Slot);
            _before = null;
            _dragging = null;
            s.RequestRender();
            return true;
        }

        if (e.Button != ToolPointerButton.Left)
        {
            return false;
        }

        // An armed slot places, whatever is under the pointer: the rail click said what the next press
        // means, and a placement that turned into a grab because a token happened to sit there would
        // be a surprise. Grabbing is what a press means when nothing is armed.
        if (Armed is { } armed)
        {
            _before = _document.Get(armed.Side, armed.Slot);
            _dragging = armed;
            Armed = null;
            Carry(pane, e.World);
            s.RequestRender();
            return true;
        }

        if (under is not { } grabbed)
        {
            return false;
        }

        _before = grabbed;
        _dragging = (grabbed.Side, grabbed.Slot);
        Carry(pane, e.World);
        s.RequestRender();
        return true;
    }

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_dragging is null || e.Pane is not { } pane)
        {
            return;
        }

        Carry(pane, e.World);
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_dragging is not { } dragging)
        {
            return;
        }

        _dragging = null;
        _before = null;

        // Released over no band: the token goes back to the rail. On a stacked map PaneAt clamps to
        // the nearest band, so this is the pointer captured past the control, not a fumbled drop.
        if (e.Pane is not { } pane)
        {
            _document.Lift(dragging.Side, dragging.Slot);
            LastHit = null;
            s.RequestRender();
            return;
        }

        MapLevel level = pane.Level;
        QueryPlaceHit? hit = _resolver.Resolve(_document.MapName, e.World.X, e.World.Y, level.ZMin, level.ZMax);
        LastHit = hit;
        _document.Place(new QueryToken(dragging.Side, dragging.Slot, e.World.X, e.World.Y,
            MapSpace.QuantizeZ(level.ZMin), hit?.Place));
        Dropped?.Invoke(hit);
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_dragging is not { } dragging)
        {
            return;
        }

        if (_before is { } before)
        {
            _document.Place(before);
        }
        else
        {
            _document.Lift(dragging.Side, dragging.Slot);
        }

        _dragging = null;
        _before = null;
        s.RequestRender();
    }

    // The token under a host point on this pane: nearest centre within the hit radius, tested on the
    // level the pane shows so a token on the other storey of a stacked map cannot be grabbed through it.
    private QueryToken? TokenAt(LevelPane pane, SKPoint screen, IToolServices s)
    {
        QueryToken? best = null;
        float bestDistance = HitRadiusPx * HitRadiusPx;
        MapLevelId paneLevel = pane.LevelId;
        bool stacked = pane.Space is { Levels.Count: > 1 };

        foreach (QueryToken token in _document.Placed)
        {
            if (stacked && pane.Space!.IdForAnchor(token.LevelMinZ) != paneLevel)
            {
                continue;
            }

            SKPoint at = s.WorldToScreen(pane, new SKPoint(token.WorldX, token.WorldY));
            float dx = at.X - screen.X;
            float dy = at.Y - screen.Y;
            float distance = dx * dx + dy * dy;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = token;
            }
        }

        return best;
    }

    // The carried token is written unresolved: the disc follows the pointer hollow, and the place is
    // decided once, at the release.
    private void Carry(LevelPane pane, SKPoint world)
    {
        if (_dragging is not { } dragging)
        {
            return;
        }

        _document.Place(new QueryToken(dragging.Side, dragging.Slot, world.X, world.Y,
            MapSpace.QuantizeZ(pane.Level.ZMin), null));
    }
}
