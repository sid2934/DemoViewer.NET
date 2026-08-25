namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     The four constants that decide when a level switch is real. All four live here so retuning is a
///     one-line change with no API break (plan risk R4).
/// </summary>
/// <param name="MinBand">
///     Half a histogram bucket. A boundary is the integer midpoint between two peak buckets
///     (<c>FloorSplitter</c>), so a one-bucket shift of either peak moves it by up to 64u and integer
///     division halves that in practice — below 32u, boundary drift alone re-triggers the switch.
/// </param>
/// <param name="MaxBand">
///     Two buckets. CS2 jump velocity 301 u/s under <c>sv_gravity 800</c> gives an apex of
///     301²/(2·800) ≈ 56.6u, step-up height is 18u and the crouch delta ≈ 18u — at 128u it is
///     geometrically impossible for a jump, a step or a crouch to change a level.
/// </param>
/// <param name="BandFractionOfSpan">
///     Fraction of the <i>thinner</i> adjacent band, so the dead zone stays inside the middle half of
///     both. Peak-to-peak separation on Nuke goes as low as ~90u, so a fixed band would be unsafe on a
///     degenerate thin one — hence relative, with a cap.
/// </param>
/// <param name="DwellSeconds">
///     Scene-time the candidate must hold before the <i>view</i> follows it. Matches the camera's own
///     settle (<c>LerpResponse 7.0</c> ⇒ ≈0.35 s to 92 %), so the level switch and the camera re-fit
///     read as one motion. Shorter lets stairs dither through; longer makes a genuine traversal feel
///     unresponsive.
/// </param>
/// <remarks>
///     A <b>record class</b>, not a record struct. A record struct whose primary-constructor parameters
///     all have defaults still zero-initializes under <c>new()</c> — the compiler takes the implicit
///     parameterless struct constructor, not the primary one — which would silently hand every caller
///     <c>MinBand 0, DwellSeconds 0</c>: no hysteresis at all, and no error anywhere. <see cref="Default" />
///     is a cached single instance, so the per-frame <see cref="LevelHysteresis.SpatialBand" /> call
///     still allocates nothing.
/// </remarks>
public sealed record LevelHysteresisOptions(
    double MinBand = 32.0,
    double MaxBand = 128.0,
    double BandFractionOfSpan = 0.25,
    double DwellSeconds = 0.35)
{
    /// <summary>The tuned defaults, justified in the B3 plan's "Hysteresis sizing" section.</summary>
    public static LevelHysteresisOptions Default { get; } = new();
}

/// <summary>
///     Stateful level chooser: a spatial sticky band plus a temporal dwell.
///     <para>
///         <b>Time comes only from <c>SceneTime.DeltaSeconds</c></b> — no wall clock (design §5.1) — so
///         a 30 fps export and a 144 fps interactive session switch levels at the same moment of the
///         demo. On <c>SceneTime.IsDiscontinuity</c> the dwell is bypassed entirely: after a seek there
///         is no continuity to protect, and holding the old level for 0.35 s would show the wrong floor
///         on every scrub.
///     </para>
/// </summary>
public sealed class LevelHysteresis
{
    private readonly LevelHysteresisOptions _options;

    /// <summary>Creates a chooser.</summary>
    /// <param name="options">Tuning; <see cref="LevelHysteresisOptions.Default" /> when null.</param>
    public LevelHysteresis(LevelHysteresisOptions? options = null) =>
        _options = options ?? LevelHysteresisOptions.Default;

    /// <summary>The settled level. <see cref="MapLevelId.None" /> until the first update.</summary>
    public MapLevelId Current { get; private set; } = MapLevelId.None;

    /// <summary>The candidate awaiting its dwell, or <see cref="MapLevelId.None" /> when settled.</summary>
    public MapLevelId Pending { get; private set; } = MapLevelId.None;

    /// <summary>Scene-seconds accumulated toward <see cref="Pending" />.</summary>
    public double PendingSeconds { get; private set; }

    /// <summary>The tuning in force.</summary>
    public LevelHysteresisOptions Options => _options;

    /// <summary>
    ///     The spatial half-band between two adjacent levels:
    ///     <c>clamp(BandFractionOfSpan × min(spans), MinBand, MaxBand)</c>. Pure, and unit-tested
    ///     directly — it is the number every per-entity level assignment leans on.
    /// </summary>
    /// <param name="a">One level.</param>
    /// <param name="b">The other.</param>
    /// <param name="options">Tuning.</param>
    public static double SpatialBand(MapLevel a, MapLevel b, LevelHysteresisOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(options);

        double thinner = Math.Min(a.Span, b.Span);
        return Math.Clamp(options.BandFractionOfSpan * thinner, options.MinBand, options.MaxBand);
    }

    /// <summary>
    ///     Advances the chooser one scene frame and returns the level to display.
    /// </summary>
    /// <param name="time">The frame's injected clock.</param>
    /// <param name="worldZ">The tracked entity's world Z.</param>
    /// <param name="space">The level set.</param>
    public MapLevelId Update(in SceneTime time, double worldZ, MapSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);

        if (space.Levels.Count == 0)
        {
            return Current;
        }

        MapLevel? resolved = space.LevelFor(worldZ, Current.IsNone ? null : Current);
        if (resolved is null)
        {
            return Current;
        }

        if (Current.IsNone || time.IsDiscontinuity)
        {
            // Nothing to protect: a first observation, or a seek. Adopt and clear the dwell, or a scrub
            // into another floor would show 0.35 s of the wrong level every single time.
            Current = resolved.Id;
            Pending = MapLevelId.None;
            PendingSeconds = 0;
            return Current;
        }

        if (resolved.Id == Current)
        {
            Pending = MapLevelId.None;
            PendingSeconds = 0;
            return Current;
        }

        if (resolved.Id != Pending)
        {
            Pending = resolved.Id;
            PendingSeconds = 0;
        }

        PendingSeconds += Math.Max(0, time.DeltaSeconds);
        if (PendingSeconds < _options.DwellSeconds)
        {
            return Current;
        }

        Current = Pending;
        Pending = MapLevelId.None;
        PendingSeconds = 0;
        return Current;
    }

    /// <summary>Forgets everything. For a demo change or a <see cref="MapSpace" /> rebuild.</summary>
    public void Reset()
    {
        Current = MapLevelId.None;
        Pending = MapLevelId.None;
        PendingSeconds = 0;
    }

    /// <summary>Adopts a level immediately and clears the dwell. A manual pick, or an AUTO re-arm.</summary>
    /// <param name="id">The level to hold.</param>
    public void ForceTo(MapLevelId id)
    {
        Current = id;
        Pending = MapLevelId.None;
        PendingSeconds = 0;
    }
}
