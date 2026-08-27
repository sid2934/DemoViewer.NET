#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Vision;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     The one place a headless consumer builds a layer stack. <c>dv2d</c> never reads a feature gate
///     or an <c>AppSettings</c> value (design §7.7); it takes explicit ids, so the set of layers a
///     render can contain has to be enumerable from Pipeline alone.
///     <para>
///         <b>One table, one entry point.</b> <see cref="SceneStackIds" /> is the only table of layer
///         ids, and <see cref="CreateSceneStack" /> is the only place that builds a stack from it — a
///         second table would let a golden and a real render draw different stacks without anyone
///         asking for that. <c>playback2d.debuggrid</c> is not one of the registered ids: it stays a
///         smoke layer that Core's own test suites construct directly.
///     </para>
///     <para>The registered ids are the persisted keys from the design doc and are never renamed.</para>
/// </summary>
public static class SceneLayerCatalog
{
    /// <summary>The prefix every layer id carries; accepted but not required on the command line.</summary>
    public const string IdPrefix = "playback2d.";

    /// <summary>
    ///     Every layer id this build can register — an alias for
    ///     <see cref="SceneStackIds" /> and not a second table. The name survives the fold because it is
    ///     what <c>--layers</c>'s refusal text and <c>dv2d.md</c> call the set, and because
    ///     <see cref="UnknownIds" /> tests membership against "known" ids, not "the scene stack".
    /// </summary>
    public static IReadOnlyList<string> KnownLayerIds => SceneStackIds;

    /// <summary>The ids in <paramref name="ids" /> that no layer answers to. Empty when all are known.</summary>
    /// <param name="ids">Candidate ids, bare or prefixed.</param>
    public static IReadOnlyList<string> UnknownIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        List<string> unknown = [];
        foreach (string id in ids)
        {
            string normalized = Normalize(id);
            if (!KnownLayerIds.Contains(normalized, StringComparer.Ordinal))
            {
                unknown.Add(id);
            }
        }

        return unknown;
    }

    /// <summary>
    ///     Canonicalises a command-line id: a bare word gets the <see cref="IdPrefix" />. Both spellings
    ///     are accepted because the design's JSON samples show bare names while the persisted keys are
    ///     prefixed; only the prefixed form is ever written back out.
    /// </summary>
    /// <param name="id">A bare or prefixed layer id.</param>
    public static string Normalize(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        // Any id that already carries a namespace is left alone. The HUD ids are "hud.clock" /
        // "hud.killfeed", deliberately not playback2d-prefixed because they are HUD layers rather than
        // 2D-playback overlays, and blindly prepending would invent "playback2d.hud.clock".
        return id.Contains('.', StringComparison.Ordinal) ? id : IdPrefix + id;
    }

    /// <summary>
    ///     <b>The table.</b> The ids <see cref="CreateSceneStack" /> can register: the seven scene
    ///     layers, the ink, and the three HUD layers. The last four are
    ///     <see cref="SceneLayerIds.OptIn" />. Every other layer list in the repository is asserted
    ///     against this one by <c>SceneLayerListParityTests</c> rather than hand-maintained beside it.
    ///     <para>
    ///         <b>Registration order is not draw order.</b> The compositor sorts every layer on
    ///         <c>(Slot, Order, Id)</c>, so <c>playback2d.annotations</c> (Overlay/100) draws before
    ///         <c>playback2d.floorlabel</c> (Hud/60) even though it is registered after it below — ink is
    ///         world content the floor caption must stay legible over.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> SceneStackIds { get; } =
    [
        SceneLayerIds.Radar,
        SceneLayerIds.Trails,
        SceneLayerIds.AreaEffects,
        SceneLayerIds.Vision,
        SceneLayerIds.Markers,
        SceneLayerIds.Bomb,
        SceneLayerIds.FloorLabel,
        SceneLayerIds.Annotations,
        SceneLayerIds.HudRoster,
        SceneLayerIds.HudClock,
        SceneLayerIds.HudKillFeed
    ];

    /// <summary>
    ///     Builds the <b>full v2 scene stack</b> — what the window draws, plus the export HUD.
    ///     <para>
    ///         <b>The only entry point.</b> <c>dv2d render</c>, <c>golden</c>, <c>bench</c> and
    ///         <c>export</c> all arrive here, so a pixel gate and a video are always drawn by the same
    ///         stack.
    ///     </para>
    ///     <para>
    ///         The <see cref="SceneLayerIds.OptIn" /> layers (the three HUD layers and the ink) are
    ///         registered only when named in <paramref name="include" /> AND only when the source that
    ///         feeds them was supplied, so an export never burns in a scoreboard or someone else's
    ///         telestration by accident; <c>SceneExportSession.OptInLayerIds</c> enforces the same rule
    ///         on the request.
    ///     </para>
    /// </summary>
    /// <param name="include">Ids to register; null registers the seven scene layers and nothing opt-in.</param>
    /// <param name="exclude">Ids to subtract.</param>
    /// <param name="vision">The line-of-sight solver, or null to draw no cones (the layer handles it).</param>
    /// <param name="hud">The tick → HUD state function; null leaves the HUD layers unregistered.</param>
    /// <param name="annotations">
    ///     The ink to burn in; null leaves the annotation layer unregistered. <b>Never the live document</b>
    ///     when the caller is an export — the layer re-records its cached pictures whenever the document's
    ///     Version moves, so a session the user is still drawing into would put strokes made DURING the
    ///     render into frames it had already passed.
    /// </param>
    /// <param name="smoother">Shared marker smoothing; a private one when null.</param>
    /// <exception cref="ArgumentException">An id is not in <see cref="SceneStackIds" />.</exception>
    public static SceneCompositor CreateSceneStack(IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null, IVisionSolver? vision = null, IHudDataSource? hud = null,
        AnnotationSession? annotations = null, MarkerSmoother? smoother = null)
    {
        HashSet<string>? wanted = include is null
            ? null
            : new HashSet<string>(include.Select(Normalize), StringComparer.Ordinal);
        HashSet<string> unwanted = exclude is null
            ? []
            : new HashSet<string>(exclude.Select(Normalize), StringComparer.Ordinal);

        // Both directions, because a typo in --exclude-layers is exactly as wrong as one in --layers and
        // silently subtracting nothing is the failure mode that hides it. (Create() validated the
        // exclude side and CreateSceneStack did not; folding the two kept the stricter half.)
        string[] unknown = [.. UnknownIds(include), .. UnknownIds(exclude)];
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"unknown layer id(s): {string.Join(", ", unknown)}. " +
                $"Known: {string.Join(", ", KnownLayerIds)}",
                UnknownIds(include).Count > 0 ? nameof(include) : nameof(exclude));
        }

        MarkerSmoother shared = smoother ?? new MarkerSmoother();
        SceneCompositor compositor = new();

        // ONE blob cache for every text layer, exactly as Scene2DHost and the test stage wire it. Left to
        // their defaults each layer builds its own, which means five copies of the embedded Inter face and
        // five LRUs holding the same handful of strings. Built eagerly rather than on
        // first text layer because the compositor is what owns it — see SceneCompositor.AddOwned.
        TextBlobCache text = new();
        compositor.AddOwned(text);

        try
        {
            foreach (string id in SceneStackIds)
            {
                if (unwanted.Contains(id))
                {
                    continue;
                }

                // Null include = "the scene", not "everything": the HUD and the ink are opt-in by name.
                bool optIn = SceneLayerIds.OptIn.Contains(id);
                if (wanted is null ? optIn : !wanted.Contains(id))
                {
                    continue;
                }

                if (optIn && Starved(id, hud, annotations))
                {
                    continue; // asked for, but nothing to feed it — draw nothing rather than an empty box.
                }

                compositor.Add(BuildLayer(id, vision, hud, annotations, shared, text));
            }
        }
        catch
        {
            compositor.Dispose();
            throw;
        }

        return compositor;
    }

    // Which source an opt-in id starves without. Everything opt-in EXCEPT the ink feeds from the HUD
    // source, so hud.roster needs no line here — only a genuinely new kind of source would. The check
    // is what lets BuildLayer keep its `hud!` / `annotations!`: an unfed layer never reaches it.
    private static bool Starved(string id, IHudDataSource? hud, AnnotationSession? annotations) =>
        string.Equals(id, SceneLayerIds.Annotations, StringComparison.Ordinal)
            ? annotations is null
            : hud is null;

    private static ISceneLayer BuildLayer(string id, IVisionSolver? vision, IHudDataSource? hud,
        AnnotationSession? annotations, MarkerSmoother smoother, TextBlobCache text) => id switch
    {
        SceneLayerIds.Radar => new RadarLayer(),
        SceneLayerIds.Trails => new TrailLayer(),
        SceneLayerIds.AreaEffects => new AreaEffectLayer(),
        SceneLayerIds.Vision => new VisionLayer(vision, smoother),
        SceneLayerIds.Markers => new MarkerLayer(smoother, text),
        SceneLayerIds.Bomb => new BombLayer(),
        SceneLayerIds.FloorLabel => new FloorLabelLayer(text),
        SceneLayerIds.Annotations => new AnnotationLayer(annotations!),
        SceneLayerIds.HudRoster => new RosterLayer(hud!, text: text),
        SceneLayerIds.HudClock => new ClockLayer(hud!, text: text),
        _ => new KillFeedLayer(hud!, text: text)
    };
}
