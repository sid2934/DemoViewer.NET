namespace DemoViewer.NET.GameIcons;

/// <summary>
///     One baked icon: its key, its intrinsic size, and access to the PNG bytes at a given scale.
///     <para>
///         <b>The size is carried, not measured.</b> The catalogue is a mix: a large block of wide weapon
///         silhouettes (an AWP is 3.42× wider than tall) alongside a large block of square glyphs, all
///         sharing a 32-unit height. Laying icons out in a square box squashes every weapon, so
///         consumers size by <see cref="Height" /> and let <see cref="Width" /> follow via
///         <see cref="WidthAt" />.
///     </para>
/// </summary>
/// <param name="Key">Namespace-qualified key, e.g. <c>equipment/awp</c>.</param>
/// <param name="Width">Intrinsic width in source units.</param>
/// <param name="Height">Intrinsic height in source units.</param>
/// <param name="Tintable">
///     True when the artwork carries no hue and may be recoloured; false when it is a picture that must
///     be drawn as it was baked &#8212; every rank badge and Premier emblem, and the handful of genuinely
///     coloured UI icons. <b>Check this before tinting</b>: recolouring a picture flattens it to a
///     silhouette.
/// </param>
public sealed record IconRef(string Key, double Width, double Height, bool Tintable = true)
{
    /// <summary>Width divided by height — 3.42 for the AWP, 1.0 for a modifier glyph.</summary>
    public double Aspect => Width / Height;

    /// <summary>The drawn width for a given drawn height, preserving the source aspect.</summary>
    /// <param name="pixelHeight">The height the icon will be drawn at.</param>
    public double WidthAt(double pixelHeight) => pixelHeight * Aspect;

    /// <summary>
    ///     Opens the PNG for one baked scale. The caller owns the stream. Throws when
    ///     <paramref name="scale" /> is not a baked scale, which is a wiring mistake rather than a
    ///     runtime condition — use <see cref="IconCatalogue.ScaleFor" /> to choose one.
    /// </summary>
    /// <param name="scale">One of <see cref="IconCatalogue.Scales" />.</param>
    public Stream Open(int scale) =>
        IconCatalogue.OpenResource(IconCatalogue.PathFor(Key, scale))
        ?? throw new ArgumentOutOfRangeException(
            nameof(scale), scale, $"{Key} was not baked at this scale");

    /// <summary>Reads the PNG bytes for one baked scale.</summary>
    /// <param name="scale">One of <see cref="IconCatalogue.Scales" />.</param>
    public byte[] Bytes(int scale)
    {
        using Stream s = Open(scale);
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
///     The kill-feed modifiers CS2 ships artwork for. Mirrors the boolean flags on
///     <c>KillFeedRow</c> one-for-one, which is why the feed needs no lookup table to go from a parsed
///     kill to an icon.
/// </summary>
[Flags]
public enum KillModifier
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>The killing shot hit the head.</summary>
    Headshot = 1,

    /// <summary>A sniper kill taken without scoping.</summary>
    NoScope = 2,

    /// <summary>The shot passed through at least one surface.</summary>
    Penetrate = 4,

    /// <summary>The shot crossed a smoke cloud.</summary>
    Smoke = 8,

    /// <summary>The attacker was flashed at the moment of the kill.</summary>
    Blind = 16,

    /// <summary>The attacker was airborne.</summary>
    InAir = 32,

    /// <summary>The victim killed themselves.</summary>
    Suicide = 64,

    /// <summary>The attacker is dominating the victim.</summary>
    Domination = 128,

    /// <summary>The attacker took revenge on a dominator.</summary>
    Revenge = 256
}

/// <summary>
///     One Premier CS Rating tier: where it starts, what CS2 colours it, and which baked emblem shows it.
/// </summary>
/// <param name="Index">0–6, ascending.</param>
/// <param name="Key">The baked emblem's key, e.g. <c>premier/tier3</c>.</param>
/// <param name="Hex">CS2's own wash colour for the tier, <c>#rrggbb</c> — the colour to use for a
///     rating number or a chart series that has to agree with the badge beside it.</param>
/// <param name="MinRating">The lowest CS Rating in this tier.</param>
public sealed record PremierTier(int Index, string Key, string Hex, int MinRating);

/// <summary>
///     Why an icon lookup returned what it did. The three absent cases are deliberately distinct: only
///     <see cref="Missing" /> is a defect, and only <see cref="Missing" /> should show a fallback.
/// </summary>
public enum IconAvailability
{
    /// <summary>No key was asked for. A caller with nothing to draw, not a failure.</summary>
    None,

    /// <summary>Artwork exists and was returned.</summary>
    Available,

    /// <summary>
    ///     CS2 ships deliberately empty artwork for this key — the environment deaths. Drawing nothing is
    ///     the correct outcome, and a fallback glyph here would invent meaning the game does not have.
    /// </summary>
    Blank,

    /// <summary>
    ///     The key is not in the bake: a typo, never curated, or renamed by a CS2 update. Worth a
    ///     fallback and worth reporting.
    /// </summary>
    Missing
}
