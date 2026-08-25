#region

using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     The in-flight ("wet") stroke: raw samples the user is still drawing, not yet committed to the
///     document. Rendered by <c>AnnotationLayer</c> every frame while active and thrown away on commit —
///     the committed element is what the document and the picture cache see.
///     <para>
///         The sample buffer is pre-sized once and reused for the life of the session: a
///         <see cref="List{T}" /> that doubles as it grows would allocate mid-gesture, and the §6 budget
///         is zero bytes.
///     </para>
/// </summary>
public sealed class WetStroke
{
    private const int InitialCapacity = 4096;

    private readonly List<InkPoint> _points = new(InitialCapacity);

    /// <summary>True between <see cref="Begin" /> and <see cref="Clear" />.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The paint the committed element will carry.</summary>
    public AnnotationStyle Style { get; private set; } = AnnotationStyle.Default;

    /// <summary>The anchor the committed element will carry.</summary>
    public SpaceRef Space { get; private set; } = new SpaceRef.World(0);

    /// <summary>
    ///     The level whose pane the gesture began on. The wet stroke is drawn ONLY there, so a drag that
    ///     wanders into the next band does not ghost onto a floor it was never drawn on.
    /// </summary>
    public MapLevelId? PaneLevelId { get; private set; }

    /// <summary>The raw samples so far, oldest first.</summary>
    public IReadOnlyList<InkPoint> Points => _points;

    /// <summary>Bumped on every change, so a consumer can skip re-deriving an unchanged outline.</summary>
    public int Version { get; private set; }

    /// <summary>Starts a stroke.</summary>
    /// <param name="style">The paint.</param>
    /// <param name="space">The anchor.</param>
    /// <param name="paneLevelId">The level whose pane the gesture began on.</param>
    /// <param name="first">The first sample.</param>
    public void Begin(in AnnotationStyle style, SpaceRef space, MapLevelId? paneLevelId, InkPoint first)
    {
        ArgumentNullException.ThrowIfNull(space);

        _points.Clear();
        _points.Add(first);
        Style = style;
        Space = space;
        PaneLevelId = paneLevelId;
        IsActive = true;
        Version++;
    }

    /// <summary>Appends a sample unconditionally.</summary>
    /// <param name="point">The sample.</param>
    public void Append(InkPoint point)
    {
        if (!IsActive)
        {
            return;
        }

        _points.Add(point);
        Version++;
    }

    /// <summary>
    ///     Appends a sample only when it is at least <paramref name="minDistanceWorld" /> from the last
    ///     one. Decimating on the way IN is what keeps a slow drag from storing a thousand coincident
    ///     samples that the outliner would then have to streamline away.
    /// </summary>
    /// <param name="point">The candidate sample.</param>
    /// <param name="minDistanceWorld">Minimum world-space separation.</param>
    /// <returns>True when the sample was kept.</returns>
    public bool TryAppend(InkPoint point, float minDistanceWorld)
    {
        if (!IsActive)
        {
            return false;
        }

        if (_points.Count > 0 && minDistanceWorld > 0)
        {
            InkPoint last = _points[^1];
            float dx = point.X - last.X;
            float dy = point.Y - last.Y;
            if (dx * dx + dy * dy < minDistanceWorld * minDistanceWorld)
            {
                return false;
            }
        }

        _points.Add(point);
        Version++;
        return true;
    }

    /// <summary>Ends the stroke and drops its samples. The capacity is kept.</summary>
    public void Clear()
    {
        if (!IsActive && _points.Count == 0)
        {
            return;
        }

        _points.Clear();
        IsActive = false;
        PaneLevelId = null;
        Version++;
    }

    /// <summary>
    ///     Rebases a world anchor across a level-set rebuild (plan risk S5). A rebuild that lands
    ///     mid-gesture would otherwise leave the wet stroke pointing at a level that no longer exists.
    /// </summary>
    /// <param name="zMinMap">Old quantized level ZMin → new quantized level ZMin.</param>
    public void RemapWorldLevel(IReadOnlyDictionary<double, double> zMinMap)
    {
        ArgumentNullException.ThrowIfNull(zMinMap);

        if (Space is not SpaceRef.World world || !zMinMap.TryGetValue(world.LevelMinZ, out double target))
        {
            return;
        }

        Space = new SpaceRef.World(target);
        PaneLevelId = MapSpace.IdForZMin(target);
        Version++;
    }
}

/// <summary>
///     The shared mutable seam between the pointer tools, the render layer and the UI — the "annotation
///     session" design §5.5 names in <c>IToolServices</c>. It owns the document, the wet stroke, the
///     current paint and the envelope template new elements are stamped with.
///     <para>
///         Deliberately NOT a view-model: Core has no Avalonia, and the tools have to be drivable from a
///         direct-execution test with no dispatcher.
///     </para>
/// </summary>
public sealed class AnnotationSession
{
    /// <summary>Default eraser radius in world units. About a player disc wide at fit zoom.</summary>
    public const float DefaultEraserWorldRadius = 48f;

    /// <summary>Default entity-anchor capture radius in world units.</summary>
    public const float DefaultAnchorWorldRadius = 96f;

    /// <summary>Creates a session over a document.</summary>
    /// <param name="document">The document this session edits.</param>
    public AnnotationSession(AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    /// <summary>The document being edited.</summary>
    public AnnotationDocument Document { get; }

    /// <summary>The stroke currently under the pointer.</summary>
    public WetStroke Wet { get; } = new();

    /// <summary>The paint new elements are stamped with.</summary>
    public AnnotationStyle Style { get; set; } = AnnotationStyle.Default;

    /// <summary>
    ///     The envelope template for new elements. Only consulted directly in
    ///     <see cref="EnvelopeMode.Custom" />; the other two modes derive one from the current tick.
    /// </summary>
    public TimeEnvelope NewElementEnvelope { get; set; } = TimeEnvelope.Static;

    /// <summary>How <see cref="EnvelopeForNewElement" /> builds the envelope.</summary>
    public EnvelopeMode DefaultVisibility { get; set; } = EnvelopeMode.Always;

    /// <summary>Lead-in length for <see cref="EnvelopeMode.Fade" />.</summary>
    public int FadeInTicks { get; set; } = 8;

    /// <summary>Lead-out length for <see cref="EnvelopeMode.Fade" />.</summary>
    public int FadeOutTicks { get; set; } = 16;

    /// <summary>Fully-opaque hold for <see cref="EnvelopeMode.Fade" />. 320 ≈ 5 s at 64 tick.</summary>
    public int HoldTicks { get; set; } = 320;

    /// <summary>The tool the router currently routes to. Mirrored here for the UI's benefit.</summary>
    public ToolKind ActiveTool { get; set; } = ToolKind.PanZoom;

    /// <summary>When true, a stroke started near a player anchors to that player's SteamId.</summary>
    public bool AnchorToEntities { get; set; }

    /// <summary>How close to a marker a press must be to capture an entity anchor, in world units.</summary>
    public float AnchorWorldRadius { get; set; } = DefaultAnchorWorldRadius;

    /// <summary>The eraser disc radius in world units.</summary>
    public float EraserWorldRadius { get; set; } = DefaultEraserWorldRadius;

    /// <summary>
    ///     Minimum world-space separation between kept samples, as a fraction of the stroke width. 0.35
    ///     keeps a stroke smooth while dropping the jitter a stationary pointer generates.
    /// </summary>
    public float SampleSpacingFactor { get; set; } = 0.35f;

    /// <summary>Raised whenever the wet stroke changed and the surface needs a repaint.</summary>
    public event Action? WetChanged;

    /// <summary>The world-space sample spacing filter for the current style.</summary>
    public float SampleSpacingWorld => Math.Max(0f, Style.WidthWorld) * SampleSpacingFactor;

    /// <summary>
    ///     The envelope a new element gets, resolved against the playhead.
    ///     <para>
    ///         <paramref name="currentTick" /> is a DV FRAME-CLOCK tick, never a CS2 server tick — the
    ///         LiveSync servo bends the playhead between 0.75× and 1.5×, so a CS2 anchor would drift
    ///         against what the user was looking at when they drew.
    ///     </para>
    /// </summary>
    /// <param name="currentTick">The playhead, in DV frame-clock ticks.</param>
    public TimeEnvelope EnvelopeForNewElement(int currentTick) => DefaultVisibility switch
    {
        EnvelopeMode.Fade => TimeEnvelope.Static.PinnedTo(currentTick, HoldTicks, FadeInTicks, FadeOutTicks),
        EnvelopeMode.Custom => NewElementEnvelope,
        _ => TimeEnvelope.Static
    };

    /// <summary>Raises <see cref="WetChanged" />. Called by the tools after they touch the wet stroke.</summary>
    public void NotifyWetChanged() => WetChanged?.Invoke();
}
