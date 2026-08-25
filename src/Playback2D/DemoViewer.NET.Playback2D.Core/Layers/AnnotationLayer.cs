#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Ink;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     Draws the annotation document: cached "dry" ink, per-frame animated ink, and the "wet" stroke
///     under the pointer.
///     <para>
///         <b>The split is by what can change</b> (plan decision D7). An element that is both
///         <see cref="TimeEnvelope.Static" /> and <see cref="SpaceRef.World" /> can never move or fade, so
///         it is recorded once per level into a WORLD-space <see cref="SKPicture" /> and replayed under
///         the pane's camera — re-recorded only when the document's <c>Version</c> changes, which is what
///         stops a drag-erase across thirty strokes from re-recording thirty times. Everything else —
///         time-anchored fades, entity-anchored telestration — is prepared in <see cref="Advance" /> and
///         drawn per frame, because its geometry and its opacity are functions of the clock.
///     </para>
///     <para>
///         The layer's own <see cref="Cache" /> is <see cref="LayerCacheHint.Dynamic" />: the compositor
///         must not try to record the whole layer, because the wet stroke changes every frame.
///     </para>
/// </summary>
public sealed class AnnotationLayer : ISceneLayer
{
    /// <summary>The stable, persisted layer id. B4's export toggles annotations by this string.</summary>
    public const string LayerId = SceneLayerIds.Annotations;

    // Generous world-space cull for the dry recordings — the same bound the compositor uses for its own
    // Static pictures. CS2 maps live well inside ±32768 world units.
    private static readonly SKRect WorldCull = new(-32768, -32768, 32768, 32768);

    private readonly Dictionary<MapLevelId, SKPicture> _dry = [];
    private readonly SKPaint _fill;
    private readonly List<SKPoint> _outline = new(1024);
    private readonly SKPath _path = new();
    private readonly List<Prepared> _prepared = [];
    private readonly AnnotationSession _session;
    private readonly List<StrokePoint> _strokePoints = new(512);
    private readonly SKPath _wetPath = new();

    private int _dryVersion = -1;
    private bool _disposed;
    private InkPoint[] _samples = new InkPoint[4096];
    private int _wetVersion = -1;

    /// <summary>Creates the layer over a session.</summary>
    /// <param name="session">The session whose document and wet stroke are drawn.</param>
    public AnnotationLayer(AnnotationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        _fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => _session.Document.Version;

    /// <summary>Test hook: how many dry pictures are currently recorded.</summary>
    public int DryPictureCount => _dry.Count;

    /// <summary>Test hook: how many dry recordings have been made since construction.</summary>
    public int DryRecordCount { get; private set; }

    /// <summary>Test hook: how many elements the last <see cref="Advance" /> prepared for per-frame drawing.</summary>
    public int PreparedCount => _prepared.Count;

    /// <summary>
    ///     Drops the cached dry pictures. Called after a <c>MapSpace</c> rebuild, because a level id may
    ///     now describe a different Z band.
    /// </summary>
    public void InvalidateLevels()
    {
        ClearDry();
        _dryVersion = -1;
    }

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            return false;
        }

        AnnotationDocument document = _session.Document;
        if (document.Version != _dryVersion)
        {
            RecordDry(document);
            _dryVersion = document.Version;
        }

        PrepareDynamic(document, in time, frame);

        // The RAF stays armed only while a stroke is in flight. A fade needs no loop of its own: a tick
        // change already repaints, and an idle tab that keeps asking for frames burns a core in the
        // background for nothing.
        return _session.Wet.IsActive;
    }

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (_disposed)
        {
            return;
        }

        int save = canvas.Save();
        SKMatrix matrix = ViewportMatrix.From(ctx.Transform);

        // Ink is authored in WORLD units and is meant to zoom with the map, so the camera matrix goes on
        // the canvas — unlike the marker layers, which transform their own points precisely because
        // their radii and stroke widths are in SCREEN units and must not scale.
        canvas.Concat(ref matrix);

        RenderDry(canvas, in ctx);
        RenderPrepared(canvas, in ctx);
        RenderWet(canvas, in ctx);

        canvas.RestoreToCount(save);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearDry();
        _fill.Dispose();
        _path.Dispose();
        _wetPath.Dispose();
    }

    // ── Dry ink: Static ∧ World, one world-space picture per level. ──────────────────────────────────

    private void RecordDry(AnnotationDocument document)
    {
        ClearDry();

        IReadOnlyList<AnnotationElement> elements = document.Elements;
        for (int i = 0; i < elements.Count; i++)
        {
            AnnotationElement element = elements[i];
            if (element.Space is not SpaceRef.World world || element.Time.IsAnchored)
            {
                continue;
            }

            MapLevelId id = MapSpace.IdForZMin(world.LevelMinZ);
            if (_dry.ContainsKey(id))
            {
                continue;
            }

            RecordLevel(document, id, world.LevelMinZ);
        }
    }

    private void RecordLevel(AnnotationDocument document, MapLevelId id, double levelMinZ)
    {
        using SKPictureRecorder recorder = new();
        SKCanvas recording = recorder.BeginRecording(WorldCull);

        IReadOnlyList<AnnotationElement> elements = document.Elements;
        for (int i = 0; i < elements.Count; i++)
        {
            AnnotationElement element = elements[i];
            if (element.Space is not SpaceRef.World world
                || element.Time.IsAnchored
                || !world.LevelMinZ.Equals(levelMinZ))
            {
                continue;
            }

            BuildPath(element, -1, _path);
            if (_path.IsEmpty)
            {
                continue;
            }

            _fill.Color = ColorOf(element.Style, 1.0);
            recording.DrawPath(_path, _fill);
        }

        _dry[id] = recorder.EndRecording();
        DryRecordCount++;
    }

    private void RenderDry(SKCanvas canvas, in SceneRenderContext ctx)
    {
        if (_dry.Count == 0)
        {
            return;
        }

        if (ctx.IsSingleLevel)
        {
            foreach (KeyValuePair<MapLevelId, SKPicture> entry in _dry)
            {
                canvas.DrawPicture(entry.Value);
            }

            return;
        }

        if (_dry.TryGetValue(ctx.Pane.LevelId, out SKPicture? picture))
        {
            canvas.DrawPicture(picture);
        }
    }

    private void ClearDry()
    {
        foreach (KeyValuePair<MapLevelId, SKPicture> entry in _dry)
        {
            entry.Value.Dispose();
        }

        _dry.Clear();
    }

    // ── Dynamic ink: time-anchored and entity-anchored, resolved per frame. ──────────────────────────

    private void PrepareDynamic(AnnotationDocument document, in SceneTime time, Scene2DFrame frame)
    {
        _prepared.Clear();

        IReadOnlyList<AnnotationElement> elements = document.Elements;
        for (int i = 0; i < elements.Count; i++)
        {
            AnnotationElement element = elements[i];
            bool anchoredInTime = element.Time.IsAnchored;
            bool anchoredToEntity = element.Space is SpaceRef.Entity;
            if (!anchoredInTime && !anchoredToEntity)
            {
                continue; // it is in the dry picture
            }

            double opacity = element.Time.OpacityAt(time.Tick) * element.Style.Opacity;
            if (opacity <= 0.001)
            {
                continue;
            }

            float offsetX = 0;
            float offsetY = 0;
            double worldZ = 0;
            MapLevelId levelId = MapLevelId.None;

            switch (element.Space)
            {
                case SpaceRef.Entity entity:
                {
                    if (!TryResolveMarker(frame, entity.SteamId, out PlayerMarker marker))
                    {
                        continue; // unresolvable or dead — §5.4 says hide, never guess
                    }

                    InkPoint origin = element.Points.Count > 0 ? element.Points[0] : default;
                    offsetX = marker.WorldX + entity.Dx - origin.X;
                    offsetY = marker.WorldY + entity.Dy - origin.Y;
                    worldZ = marker.WorldZ;
                    break;
                }

                case SpaceRef.World world:
                    levelId = MapSpace.IdForZMin(world.LevelMinZ);
                    break;

                default:
                    continue;
            }

            int reveal = RevealCount(element, time.Tick);
            if (reveal == 0)
            {
                continue;
            }

            _prepared.Add(new Prepared(element, offsetX, offsetY, (float)opacity, levelId, worldZ, reveal));
        }
    }

    private void RenderPrepared(SKCanvas canvas, in SceneRenderContext ctx)
    {
        for (int i = 0; i < _prepared.Count; i++)
        {
            Prepared prepared = _prepared[i];

            bool belongs = prepared.LevelId.IsNone
                ? ctx.BelongsHere(prepared.WorldZ)
                : ctx.IsSingleLevel || ctx.Pane.LevelId == prepared.LevelId;
            if (!belongs)
            {
                continue;
            }

            BuildPath(prepared.Element, prepared.Reveal, _path);
            if (_path.IsEmpty)
            {
                continue;
            }

            _fill.Color = ColorOf(prepared.Element.Style, prepared.Opacity);

            int save = canvas.Save();
            canvas.Translate(prepared.OffsetX, prepared.OffsetY);
            canvas.DrawPath(_path, _fill);
            canvas.RestoreToCount(save);
        }
    }

    // ── Wet ink: the stroke under the pointer, redrawn every frame it is active. ─────────────────────

    private void RenderWet(SKCanvas canvas, in SceneRenderContext ctx)
    {
        WetStroke wet = _session.Wet;
        if (!wet.IsActive || wet.Points.Count == 0)
        {
            return;
        }

        // Only in the pane the gesture began on: a drag that wanders across a band boundary must not
        // ghost the stroke onto a floor it was never drawn on.
        if (!ctx.IsSingleLevel && wet.PaneLevelId is { } origin && ctx.Pane.LevelId != origin)
        {
            return;
        }

        if (wet.Version != _wetVersion)
        {
            BuildPath(wet.Points, wet.Points.Count, wet.Style.WidthWorld, _wetPath);
            _wetVersion = wet.Version;
        }

        if (_wetPath.IsEmpty)
        {
            return;
        }

        _fill.Color = ColorOf(wet.Style, wet.Style.Opacity);
        canvas.DrawPath(_wetPath, _fill);
    }

    // ── Geometry. ───────────────────────────────────────────────────────────────────────────────────

    private void BuildPath(AnnotationElement element, int reveal, SKPath into)
    {
        int count = reveal < 0 ? element.Points.Count : Math.Clamp(reveal, 1, element.Points.Count);
        BuildPath(element.Points, count, element.Style.WidthWorld, into);
    }

    private void BuildPath(IReadOnlyList<InkPoint> points, int count, float widthWorld, SKPath into)
    {
        into.Reset();
        if (count <= 0 || points.Count == 0)
        {
            return;
        }

        if (_samples.Length < count)
        {
            _samples = new InkPoint[Math.Max(count, _samples.Length * 2)];
        }

        for (int i = 0; i < count; i++)
        {
            _samples[i] = points[i];
        }

        FreehandOptions options = FreehandOptions.ForWidth(widthWorld);
        FreehandOutline.GetOutline(_samples.AsSpan(0, count), in options, _strokePoints, _outline);

        if (_outline.Count < 3)
        {
            return;
        }

        into.MoveTo(_outline[0]);
        for (int i = 1; i < _outline.Count; i++)
        {
            into.LineTo(_outline[i]);
        }

        into.Close();
    }

    // Partial-stroke reveal: while an element is inside its fade-in ramp, draw only the leading fraction
    // of its samples. Design §5.4 calls this out as nearly free, and it is: the outliner already accepts
    // a prefix of the point list. -1 means "all of them".
    private static int RevealCount(AnnotationElement element, int tick)
    {
        if (!element.Style.RevealOnFadeIn
            || element.Kind != AnnotationKind.Freehand
            || element.Time.FromTick is not { } from
            || element.Time.FadeInTicks <= 0
            || tick >= from)
        {
            return -1;
        }

        double lead = from - tick;
        if (lead > element.Time.FadeInTicks)
        {
            return 0;
        }

        double fraction = 1.0 - lead / element.Time.FadeInTicks;
        return Math.Clamp((int)Math.Ceiling(fraction * element.Points.Count), 1, element.Points.Count);
    }

    private static bool TryResolveMarker(Scene2DFrame frame, ulong steamId, out PlayerMarker marker)
    {
        marker = default;
        if (steamId == 0)
        {
            return false;
        }

        IReadOnlyList<PlayerMarker> markers = frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].SteamId != steamId)
            {
                continue;
            }

            if (!markers[i].IsAlive)
            {
                return false;
            }

            marker = markers[i];
            return true;
        }

        return false;
    }

    private static SKColor ColorOf(in AnnotationStyle style, double opacity)
    {
        SKColor colour = new(style.ColorArgb);
        double alpha = colour.Alpha * Math.Clamp(opacity, 0, 1);
        return colour.WithAlpha((byte)Math.Clamp(Math.Round(alpha), 0, 255));
    }

    private readonly record struct Prepared(
        AnnotationElement Element,
        float OffsetX,
        float OffsetY,
        float Opacity,
        MapLevelId LevelId,
        double WorldZ,
        int Reveal);
}
