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
///         <b>Real-time ink</b> (plan D7) rides the second half of that split. An element carrying a
///         <see cref="StrokeTiming" /> draws only the prefix its run table says has been reached, and the
///         prefix is cut into a full-alpha body plus a fixed number of tail bands, each running the
///         element's own <see cref="TimeEnvelope" /> shifted by the offset its samples were drawn at. The
///         whole thing is a pure function of <c>SceneTime.Tick</c> — no accumulated <c>DeltaSeconds</c>
///         and no one-tick pulses — which is what keeps a 30 fps export (where
///         <c>ticksPerOutputFrame ≈ 2.13</c>, so ticks are skipped) identical to a 64 fps one.
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

    // How many alpha steps a real-time stroke's fading tail is drawn in (plan D7 §4).
    //
    // The visible window is always CONTIGUOUS — nothing past the head has been drawn yet, nothing behind
    // the tail is left — so the ramp lives only at the older end and the step count is a CONSTANT rather
    // than a function of the sample count. §4 costed a 400-sample stroke at 1080p at 117 µs (k=1),
    // 152 µs (k=8) and 316 µs (k=64), at 0 B/frame throughout;
    // RealTimeInkTests.OneRealTimeStroke_CostsAboutWhatSection4Costed measures the shipped thing at
    // ~116 µs whole against ~123 µs for body + 8 bands, because the bands span only the fade-out ramp
    // and are therefore short. 8 is where a 12.5 % alpha step stops being visible and the cost is still
    // single-digit microseconds against a 2.75 ms full-scene p99.
    private const int TailSteps = 8;

    // Generous world-space cull for the dry recordings — the same bound the compositor uses for its own
    // Static pictures. CS2 maps live well inside ±32768 world units.
    private static readonly SKRect WorldCull = new(-32768, -32768, 32768, 32768);

    // Keyed by the anchor's STORED level ZMin, not by a level id. The id an anchor belongs to is a
    // question only MapSpace can answer (MapSpace.IdForAnchor), and there is no space in Advance — so the
    // recording keys on the one value the element itself carries, and the pane resolves it at render.
    private readonly Dictionary<double, SKPicture> _dry = [];
    private readonly SKPaint _fill;
    private readonly List<SKPoint> _outline = new(1024);
    private readonly SKPath _path = new();
    private readonly List<Prepared> _prepared = [];

    // One flat run of sections for the whole frame; a Prepared points into it by (start, count). A list
    // per element would allocate per element per frame, and §6's budget is zero bytes.
    private readonly List<Section> _sections = new(64);
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

        RenderDry(canvas, in ctx, _dry);
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

            if (_dry.ContainsKey(world.LevelMinZ))
            {
                continue;
            }

            RecordLevel(document, world.LevelMinZ);
        }
    }

    private void RecordLevel(AnnotationDocument document, double levelMinZ)
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

            // Whole, always: a dry element is Static by definition, so there is nothing to reveal and no
            // section to fade.
            BuildPath(element.Points, element.Points.Count, element.Style.WidthWorld, _path);
            if (_path.IsEmpty)
            {
                continue;
            }

            _fill.Color = ColorOf(element.Style, 1.0);
            recording.DrawPath(_path, _fill);
        }

        _dry[levelMinZ] = recorder.EndRecording();
        DryRecordCount++;
    }

    private static void RenderDry(SKCanvas canvas, in SceneRenderContext ctx,
        Dictionary<double, SKPicture> dry)
    {
        if (dry.Count == 0)
        {
            return;
        }

        // A handful of entries at most (one per floor a stroke was drawn on), so the pane resolves each
        // anchor rather than the record keying on an id it cannot know. Struct enumerator, no closure:
        // this runs inside the §6 zero-allocation steady state.
        foreach (KeyValuePair<double, SKPicture> entry in dry)
        {
            if (ctx.IsSingleLevel || LevelIdFor(in ctx, entry.Key) == ctx.Pane.LevelId)
            {
                canvas.DrawPicture(entry.Value);
            }
        }
    }

    // The one place an anchor becomes a level id. Through the SPACE when there is one, because Mint's
    // collision bump makes level.Id != IdForZMin(level.ZMin) after a floor is lost and re-found; the
    // static minting rule is the fallback for a context built without a level set (B0's fixtures).
    private static MapLevelId LevelIdFor(in SceneRenderContext ctx, double levelMinZ) =>
        ctx.Levels is { } space ? space.IdForAnchor(levelMinZ) : MapSpace.IdForZMin(levelMinZ);

    private void ClearDry()
    {
        foreach (KeyValuePair<double, SKPicture> entry in _dry)
        {
            entry.Value.Dispose();
        }

        _dry.Clear();
    }

    // ── Dynamic ink: time-anchored and entity-anchored, resolved per frame. ──────────────────────────

    private void PrepareDynamic(AnnotationDocument document, in SceneTime time, Scene2DFrame frame)
    {
        _prepared.Clear();
        _sections.Clear();

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

            // The per-element cull gates on the strongest section the element still has, which for a
            // real-time stroke is its live HEAD rather than the element's own envelope: that envelope
            // opens at FromTick and shuts hold + fadeOut ticks later, while the head goes on being drawn
            // for DurationTicks. Gating on OpacityAt(tick) would erase a stroke in the middle of drawing
            // itself, the moment its FIRST section expired.
            double opacity = PeakOpacity(element, time.Tick) * element.Style.Opacity;
            if (opacity <= 0.001)
            {
                continue;
            }

            float offsetX = 0;
            float offsetY = 0;
            double worldZ = 0;
            double levelMinZ = 0;
            bool worldAnchored = false;

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
                    // The ANCHOR travels, not an id derived from it: Advance has no MapSpace, and the
                    // id an anchor belongs to is the space's answer, not Z's (see LevelIdFor).
                    levelMinZ = world.LevelMinZ;
                    worldAnchored = true;
                    break;

                default:
                    continue;
            }

            // Tick-dependent work happens HERE, once, and not in Render: Render runs once per pane, and
            // the section table has to be the same table on every one of them.
            int sectionStart = _sections.Count;
            int sectionCount = BuildSections(element, time.Tick, (float)opacity);
            if (sectionCount == 0)
            {
                continue;
            }

            _prepared.Add(new Prepared(element, offsetX, offsetY, worldAnchored, levelMinZ, worldZ,
                sectionStart, sectionCount));
        }
    }

    private void RenderPrepared(SKCanvas canvas, in SceneRenderContext ctx)
    {
        for (int i = 0; i < _prepared.Count; i++)
        {
            Prepared prepared = _prepared[i];

            bool belongs = prepared.WorldAnchored
                ? ctx.IsSingleLevel || ctx.Pane.LevelId == LevelIdFor(in ctx, prepared.LevelMinZ)
                : ctx.BelongsHere(prepared.WorldZ);
            if (!belongs)
            {
                continue;
            }

            int save = canvas.Save();
            canvas.Translate(prepared.OffsetX, prepared.OffsetY);

            // Oldest section first, so the brighter ink lands ON TOP of the shared boundary sample
            // rather than under it — see AddSection for why the sections overlap at all.
            for (int s = 0; s < prepared.SectionCount; s++)
            {
                Section section = _sections[prepared.SectionStart + s];
                BuildPath(prepared.Element.Points, section.Start, section.Count,
                    prepared.Element.Style.WidthWorld, _path);
                if (_path.IsEmpty)
                {
                    continue;
                }

                _fill.Color = ColorOf(prepared.Element.Style, section.Opacity);
                canvas.DrawPath(_path, _fill);
            }

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

    private void BuildPath(IReadOnlyList<InkPoint> points, int count, float widthWorld, SKPath into) =>
        BuildPath(points, 0, count, widthWorld, into);

    // The outliner takes a SPAN of the sample list, so a section costs one copy and one outline pass and
    // needs no geometry the whole-stroke path does not already have. The two ends get their own caps,
    // which is what lets neighbouring sections overlap cleanly instead of abutting.
    private void BuildPath(IReadOnlyList<InkPoint> points, int start, int count, float widthWorld,
        SKPath into)
    {
        into.Reset();
        if (count <= 0 || start < 0 || start + count > points.Count)
        {
            return;
        }

        if (_samples.Length < count)
        {
            _samples = new InkPoint[Math.Max(count, _samples.Length * 2)];
        }

        for (int i = 0; i < count; i++)
        {
            _samples[i] = points[start + i];
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

    // Partial-stroke reveal: how many of an element's samples have been drawn yet. ONE seam with two
    // sources, deliberately — design §5.4 called this out as nearly free because the outliner already
    // accepts a prefix of the point list, and that is still the reason both features share it. -1 means
    // "all of them".
    //
    //   • A captured cadence (plan D7) asks its own run table, so the head advances at the speed the
    //     stroke was authored at, pauses included.
    //   • Style.RevealOnFadeIn keeps its own, different meaning: a LINEAR sweep across the fade-in ramp,
    //     with no cadence recorded anywhere. It predates D7, it is in the published schema and the
    //     pinned sample, and dropping it would be a format break for no gain (D7 §9).
    private static int RevealCount(AnnotationElement element, int tick)
    {
        if (element.Kind != AnnotationKind.Freehand || element.Time.FromTick is not { } from)
        {
            return -1;
        }

        if (element.Timing is { } timing)
        {
            return timing.RevealedCount(ClampTick((long)tick - from), element.Points.Count);
        }

        if (!element.Style.RevealOnFadeIn || element.Time.FadeInTicks <= 0 || tick >= from)
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

    // ── Per-section fade: the element's own trapezoid, shifted by when each sample was drawn. ────────

    // The section decomposition (plan D7 §3 and §4), appended to _sections. Returns how many.
    //
    // Everything WITHOUT a captured cadence is one section over its revealed prefix at the element's own
    // opacity — byte for byte what this layer drew before D7, which is the point.
    //
    // A real-time element is a contiguous window walked back from the head, in ELAPSED space (ticks
    // since the stroke began), because RevealedCount is exactly the inverse map from an elapsed value to
    // a sample index:
    //     offset ≤ elapsed                   → drawn yet at all         (up to the head)
    //     offset ≥ elapsed − hold            → still inside the plateau (the full-alpha body)
    //     offset < elapsed − hold − fadeOut  → dissolved                (behind the tail)
    // so each boundary costs one O(runs) lookup. Evaluating an alpha per SAMPLE instead would be O(n)
    // every frame, on the one path in this layer that no cache covers.
    private int BuildSections(AnnotationElement element, int tick, float elementOpacity)
    {
        int count = element.Points.Count;
        if (count == 0)
        {
            return 0;
        }

        if (TimingOf(element) is not { } timing || element.Time.FromTick is not { } from)
        {
            int reveal = RevealCount(element, tick);
            if (reveal == 0)
            {
                return 0;
            }

            _sections.Add(new Section(0, reveal < 0 ? count : Math.Clamp(reveal, 1, count),
                elementOpacity));
            return 1;
        }

        long elapsed = (long)tick - from;
        int head = timing.RevealedCount(ClampTick(elapsed), count);
        if (head <= 0)
        {
            return 0; // not started, or scrubbed back behind FromTick
        }

        // No UntilTick is an envelope that never closes, so nothing behind the head has begun to fade
        // and the whole revealed prefix is body. Leaving bodyElapsed at `elapsed` also puts the body's
        // representative tick at FromTick, which is where that element's plateau starts.
        float styleOpacity = element.Style.Opacity;
        int fadeOut = 0;
        int steps = 0;
        int bodyStart = 0;
        long bodyElapsed = elapsed;
        long tailElapsed = elapsed;

        if (element.Time.UntilTick is { } until)
        {
            fadeOut = Math.Max(0, element.Time.FadeOutTicks);
            bodyElapsed = elapsed - ((long)until - from);
            tailElapsed = bodyElapsed - fadeOut;

            // Clamped against the head: an inverted envelope (UntilTick < FromTick) would otherwise put
            // the body's first sample past the head and draw ink the replay has not reached.
            bodyStart = Math.Min(head, timing.RevealedCount(ClampTick(bodyElapsed), count));

            // Never more bands than the ramp has ticks — subdividing below one tick buys two sections
            // that round to the same alpha.
            steps = Math.Min(TailSteps, fadeOut);
        }

        int emitted = 0;
        for (int j = 0; j < steps; j++)
        {
            long lo = tailElapsed + (long)fadeOut * j / steps;
            long hi = tailElapsed + (long)fadeOut * (j + 1) / steps;
            int start = timing.RevealedCount(ClampTick(lo), count);
            int end = Math.Min(bodyStart, timing.RevealedCount(ClampTick(hi), count));

            // §3 exactly: the band's representative sample is its midpoint, and its opacity is the
            // element's OWN trapezoid read at the tick that sample is living at. Not a second ramp —
            // TimeEnvelope.OpacityAt is already pure, scrub-safe and overflow-guarded, and HoldTicks
            // keeping its meaning PER SECTION is what makes one control produce both "the whole stroke
            // appears at once and dissolves from the start" and "the stroke chases its own tail".
            emitted += AddSection(start, end, head,
                (float)(element.Time.OpacityAt(ClampTick(tick - ((lo + hi) / 2))) * styleOpacity));
        }

        emitted += AddSection(bodyStart, head, head,
            (float)(element.Time.OpacityAt(ClampTick(tick - ((bodyElapsed + elapsed) / 2)))
                    * styleOpacity));
        return emitted;
    }

    // Sections OVERLAP by one sample rather than butt-joining, and RenderPrepared draws them oldest
    // first.
    //
    // Both joints are visible defects and the only question is which one to take. A butt joint leaves a
    // hairline where two ribbons' caps meet: that is a HOLE in the ink, it widens with zoom because the
    // gap is in world units and no amount of antialiasing closes it, and it crawls along the stroke as
    // the boundary sample steps from frame to frame. The overlap instead double-blends one sample's
    // width into a faint bead — bounded by the alpha STEP, so at k=8 it is 12.5 % of an already-dim
    // tail, and exactly zero at the body joint, where the newer section is opaque and covers the older
    // one outright. Ink a shade too dark in the tail is the same stroke; ink that is missing is a
    // different one.
    private int AddSection(int start, int end, int head, float opacity)
    {
        // The emptiness test is on the band ITSELF, before the overlap is applied: an empty band that
        // borrowed its neighbour's boundary sample would put a lone dot at the tail of every stroke
        // whose fade has not started yet.
        if (start < 0 || end <= start || opacity <= 0.002f)
        {
            return 0;
        }

        int drawEnd = Math.Min(end + 1, head);
        if (drawEnd <= start)
        {
            return 0;
        }

        _sections.Add(new Section(start, drawEnd - start, opacity));
        return 1;
    }

    // The strongest opacity any of this element's sections can carry at this tick — what the per-element
    // cull gates on, because a real-time element's own envelope closes while its head is still drawing.
    //
    // It is the NEWEST sample's: the run table's offsets are non-decreasing, so effective ticks fall as
    // the index rises, and the trapezoid is monotone everywhere right of its plateau. DurationTicks
    // rather than TickOffsetForSample(n−1) deliberately — RevealedCount clamps its answer up by one so
    // the head can sit a hair past its own offset, and the duration is what the capture recorded.
    private static double PeakOpacity(AnnotationElement element, int tick)
    {
        if (TimingOf(element) is not { } timing || element.Time.FromTick is not { } from)
        {
            return element.Time.OpacityAt(tick);
        }

        long age = (long)tick - from - timing.DurationTicks;
        return element.Time.OpacityAt(ClampTick(from + Math.Max(0, age)));
    }

    // A cadence only animates a freehand stroke: every other kind derives its geometry from the first
    // and last point, so "the first 40 % of it" is not a shape it has.
    private static StrokeTiming? TimingOf(AnnotationElement element) =>
        element.Kind == AnnotationKind.Freehand ? element.Timing : null;

    // Tick arithmetic in long, narrowed once. TimeEnvelope.OpacityAt guards its own input against
    // overflow; these are the DIFFERENCES that reach it, and a stroke anchored near int.MinValue would
    // otherwise wrap straight into the middle of its own envelope.
    private static int ClampTick(long tick) => (int)Math.Clamp(tick, int.MinValue, int.MaxValue);

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
        bool WorldAnchored,
        double LevelMinZ,
        double WorldZ,
        int SectionStart,
        int SectionCount);

    // One contiguous run of samples drawn at one alpha. Start/Count index into the element's own point
    // list, and Opacity already carries Style.Opacity — exactly what Prepared.Opacity used to hold, so a
    // non-real-time element's single section resolves to the same colour it always did.
    private readonly record struct Section(int Start, int Count, float Opacity);
}
