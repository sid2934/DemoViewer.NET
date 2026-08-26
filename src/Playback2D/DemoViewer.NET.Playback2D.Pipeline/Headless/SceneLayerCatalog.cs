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
///     or an <c>AppSettings</c> value (design §7.7) — it takes explicit ids — so the set of layers a
///     render can contain has to be enumerable from Pipeline alone.
///     <para>
///         <b>B1 extends this, and only this.</b> Today the stack is B0's single smoke layer; when the
///         seven real layers land, they are registered here and <c>--layers radar,markers</c> starts
///         working with no CLI change. The registered ids are the persisted keys from
///         <c>plans/00-overview.md</c> §3.3 and are never renamed.
///     </para>
/// </summary>
public static class SceneLayerCatalog
{
    /// <summary>The prefix every layer id carries; accepted but not required on the command line.</summary>
    public const string IdPrefix = "playback2d.";

    private static readonly IReadOnlyList<Registration> _registrations =
    [
        // B0's smoke layer. It draws no text, so it needs no font and rasterises identically on a CI
        // container with no fontconfig — which is what makes a byte-exact CPU golden lane possible at
        // all before B1's embedded-typeface work lands.
        new("playback2d.debuggrid", static () => new DebugGridLayer())
    ];

    /// <summary>Every layer id this build can register, in registration order.</summary>
    public static IReadOnlyList<string> KnownLayerIds { get; } =
        _registrations.Select(static r => r.Id).ToArray();

    /// <summary>
    ///     Builds a compositor holding the requested layers. The caller owns and disposes it (disposing
    ///     a compositor disposes its layers).
    /// </summary>
    /// <param name="include">Ids to register, or null for every known layer.</param>
    /// <param name="exclude">Ids to subtract from <paramref name="include" />.</param>
    /// <exception cref="ArgumentException">An id is not in <see cref="KnownLayerIds" />.</exception>
    public static SceneCompositor Create(IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null)
    {
        string[] unknown = [.. UnknownIds(include), .. UnknownIds(exclude)];
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"unknown layer id(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", KnownLayerIds)}",
                include is not null && UnknownIds(include).Count > 0 ? nameof(include) : nameof(exclude));
        }

        HashSet<string>? wanted = include is null
            ? null
            : new HashSet<string>(include.Select(Normalize), StringComparer.Ordinal);
        HashSet<string> unwanted = exclude is null
            ? []
            : new HashSet<string>(exclude.Select(Normalize), StringComparer.Ordinal);

        SceneCompositor compositor = new();
        try
        {
            foreach (Registration registration in _registrations)
            {
                if (wanted is not null && !wanted.Contains(registration.Id))
                {
                    continue;
                }

                if (unwanted.Contains(registration.Id))
                {
                    continue;
                }

                compositor.Add(registration.Create());
            }
        }
        catch
        {
            compositor.Dispose();
            throw;
        }

        return compositor;
    }

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

        // Any id that already carries a namespace is left alone. B4's HUD ids are "hud.clock" /
        // "hud.killfeed" — deliberately not playback2d-prefixed, because they are HUD layers rather
        // than 2D-playback overlays — and blindly prepending would invent "playback2d.hud.clock".
        return id.Contains('.', StringComparison.Ordinal) ? id : IdPrefix + id;
    }

    /// <summary>
    ///     The ids <see cref="CreateSceneStack" /> can register: B1's seven scene layers, B2's ink, and the
    ///     three HUD layers, in draw order. The last four are <see cref="SceneLayerIds.OptIn" />.
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
    ///         <b>Why this is a second entry point and not a bigger <see cref="Create" />.</b> Adding
    ///         these to <see cref="_registrations" /> would change what <c>Create()</c> returns with no
    ///         arguments, and that is what <c>dv2d render</c> and every committed CPU golden are built on:
    ///         every golden in the corpus would move in a commit that is about video export. B1 folds the
    ///         two tables together in the PR that re-baselines the corpus deliberately; until then this is
    ///         the stack an export and <c>dv2d export</c> ask for by name.
    ///     </para>
    ///     <para>
    ///         The <see cref="SceneLayerIds.OptIn" /> layers — the three HUD layers and the ink — are
    ///         registered only when named in <paramref name="include" /> AND only when the source that
    ///         feeds them was supplied. An export never burns in a scoreboard, or someone else's
    ///         telestration, by accident (<c>SceneExportSession.OptInLayerIds</c> enforces the same rule on
    ///         the request).
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

        if (wanted is not null)
        {
            string[] unknown = [.. wanted.Where(id => !SceneStackIds.Contains(id, StringComparer.Ordinal))];
            if (unknown.Length > 0)
            {
                throw new ArgumentException(
                    $"unknown layer id(s): {string.Join(", ", unknown)}. " +
                    $"Known: {string.Join(", ", SceneStackIds)}", nameof(include));
            }
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
    // source, so D3b's hud.roster needs no line here — only a genuinely new kind of source would. The
    // check is what lets BuildLayer keep its `hud!` / `annotations!`: an unfed layer never reaches it.
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

    private sealed record Registration(string Id, Func<ISceneLayer> Create);
}
