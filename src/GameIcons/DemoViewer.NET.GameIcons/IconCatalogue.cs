#region

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.GameIcons;

/// <summary>
///     The baked CS2 iconography: weapons, kill-feed modifiers, and a curated UI set.
///     <para>
///         <b>Weapon lookup needs no mapping table.</b> Valve's own icon file names match the
///         <c>player_death.weapon</c> strings exactly — <c>ak47</c>, <c>usp_silencer</c>, <c>knife_t</c>,
///         <c>inferno</c>, <c>taser</c> — so <see cref="Weapon" /> passes the demo's string straight
///         through. A weapon with no icon (CS2 ships deliberately empty art for <c>world</c>,
///         <c>worldent</c> and <c>trigger_hurt</c>, the environment deaths) returns null, which is the
///         same answer: draw nothing.
///     </para>
///     <para>
///         <b>Every icon is white on transparent</b>, so it is an alpha mask and the consumer picks the
///         colour: <c>SKColorFilter.CreateBlendMode(tint, SrcIn)</c> under Skia, an
///         <c>OpacityMask</c> under Avalonia. There are no per-colour variants to ship or keep in sync.
///     </para>
/// </summary>
public static class IconCatalogue
{
    private const string Prefix = "icons/";

    private static readonly Assembly Owner = typeof(IconCatalogue).Assembly;
    private static readonly FrozenDictionary<string, IconRef> Catalogue;
    private static readonly FrozenDictionary<string, string> ResourceNames;
    private static readonly FrozenSet<string> BlankKeys;
    private static readonly ConcurrentDictionary<string, byte> Misses = new(StringComparer.Ordinal);

    /// <summary>The CS2 build fingerprint the icons were baked from (CRC32 over the source artwork).</summary>
    public static string SourceHash { get; }

    /// <summary>The baked pixel heights, ascending. Widths follow each icon's own aspect.</summary>
    public static IReadOnlyList<int> Scales { get; }

    /// <summary>
    ///     The seven Premier CS Rating tiers, ascending, with the colours CS2 itself washes the emblem
    ///     with. Read from the bake rather than restated here, so a Valve retune arrives with a re-bake.
    /// </summary>
    public static IReadOnlyList<PremierTier> PremierTiers { get; }

    /// <summary>
    ///     Skill Group names by rank number, 0&#8211;18, from CS2's own localization &#8212; index 0 is
    ///     "No Skill Group". <b>Wingman uses this same list</b>: the game has no separate per-rank Wingman
    ///     strings, only its own wording for the states without a rank.
    /// </summary>
    public static IReadOnlyList<string> RankNames { get; }

    /// <summary>The highest Skill Group number, 18 ("The Global Elite").</summary>
    public const int MaxRank = 18;

    /// <summary>Every icon key, namespace included, in ordinal order.</summary>
    public static IReadOnlyCollection<string> Keys => Catalogue.Keys;

    /// <summary>
    ///     Every key a drawing surface asked for and did not get. A snapshot; empty is the healthy state.
    /// </summary>
    public static IReadOnlyCollection<string> MissingKeys => (IReadOnlyCollection<string>)Misses.Keys;

    /// <summary>
    ///     Called once per distinct key, the first time <see cref="Demand" /> cannot satisfy it. Set by
    ///     the host to route misses into its own logging; a plain settable hook rather than an event, so
    ///     this assembly stays dependency-free.
    /// </summary>
    public static Action<string>? MissingKeyObserver { get; set; }

    static IconCatalogue()
    {
        // Resource names are fixed at build time; normalise separators so a Windows-built and a
        // Linux-built assembly resolve the same key.
        ResourceNames = Owner.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToFrozenDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);

        using Stream manifest = OpenResource(Prefix + "icons.json")
                                ?? throw new InvalidOperationException(
                                    "icons.json is not embedded. Run the icon bake: "
                                    + "dotnet run --project tools/DemoViewer.NET.AssetBaker -- --icons");

        using JsonDocument doc = JsonDocument.Parse(manifest);
        JsonElement root = doc.RootElement;

        SourceHash = root.GetProperty("sourceHash").GetString() ?? "";
        Scales = [.. root.GetProperty("scales").EnumerateArray().Select(e => e.GetInt32()).Order()];

        Dictionary<string, IconRef> catalogue = new(StringComparer.Ordinal);
        foreach (JsonProperty icon in root.GetProperty("icons").EnumerateObject())
        {
            double w = icon.Value.GetProperty("w").GetDouble();
            double h = icon.Value.GetProperty("h").GetDouble();
            bool tintable = !icon.Value.TryGetProperty("tintable", out JsonElement t) || t.GetBoolean();
            catalogue[icon.Name] = new IconRef(icon.Name, w, h, tintable);
        }

        Catalogue = catalogue.ToFrozenDictionary(StringComparer.Ordinal);

        List<PremierTier> tiers = [];
        if (root.TryGetProperty("premierTiers", out JsonElement premier))
        {
            int i = 0;
            foreach (JsonElement t in premier.EnumerateArray())
            {
                string name = t.GetProperty("name").GetString() ?? "tier" + i;
                tiers.Add(new PremierTier(
                    i++, "premier/" + name,
                    t.GetProperty("hex").GetString() ?? "#ffffff",
                    t.GetProperty("minRating").GetInt32()));
            }
        }

        PremierTiers = tiers;

        RankNames = root.TryGetProperty("rankNames", out JsonElement names)
            ? [.. names.EnumerateArray().Select(n => n.GetString() ?? "")]
            : [];

        // Schema 2 carries the blank list. Seeded for an older bake so the distinction still works
        // against a manifest predating it: these three are CS2's environment deaths and have been
        // deliberately empty for as long as the artwork has existed.
        BlankKeys = root.TryGetProperty("blank", out JsonElement blank)
            ? blank.EnumerateArray().Select(b => b.GetString() ?? "").ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet.ToFrozenSet(
                ["equipment/world", "equipment/worldent", "equipment/trigger_hurt"],
                StringComparer.Ordinal);
    }

    /// <summary>
    ///     Resolves a key a drawing surface intends to render, and says <b>why</b> it is absent when it is.
    ///     <para>
    ///         The distinction is the point. <see cref="IconAvailability.Blank" /> means CS2 itself ships
    ///         empty artwork — an environment death has no weapon — so drawing nothing is the correct
    ///         result and there is nothing to report. <see cref="IconAvailability.Missing" /> means the
    ///         key is a typo, was never curated, or Valve renamed it; that one earns a fallback and is
    ///         recorded once through <see cref="MissingKeyObserver" />.
    ///     </para>
    ///     <para>
    ///         Use this from anything that draws. Use <see cref="Get" /> to merely probe — a classifier
    ///         asking "is this a weapon?" must not leave a miss behind for every player pawn it sees.
    ///     </para>
    /// </summary>
    /// <param name="key">A namespace-qualified key, or null/empty for "nothing was asked for".</param>
    /// <param name="availability">Why the result is what it is.</param>
    public static IconRef? Demand(string? key, out IconAvailability availability)
    {
        if (string.IsNullOrEmpty(key))
        {
            availability = IconAvailability.None;
            return null;
        }

        if (Catalogue.TryGetValue(key, out IconRef? icon))
        {
            availability = IconAvailability.Available;
            return icon;
        }

        if (BlankKeys.Contains(key))
        {
            availability = IconAvailability.Blank;
            return null;
        }

        availability = IconAvailability.Missing;

        // One failed TryAdd per frame for a key already seen; the observer fires exactly once.
        if (Misses.TryAdd(key, 0))
        {
            MissingKeyObserver?.Invoke(key);
        }

        return null;
    }

    /// <summary>
    ///     True when CS2 ships deliberately empty artwork for this key, so drawing nothing is right and a
    ///     fallback would be wrong.
    /// </summary>
    /// <param name="key">A namespace-qualified key.</param>
    public static bool IsBlank(string? key) => key is not null && BlankKeys.Contains(key);

    /// <summary>
    ///     The Competitive Skill Group badge for a rank number, 0&#8211;18. Null when the number is
    ///     outside that range &#8212; a demo can carry a value this build has no badge for.
    /// </summary>
    /// <param name="rank">The Skill Group number as the demo reports it.</param>
    public static IconRef? CompetitiveRank(int rank) =>
        rank is >= 0 and <= MaxRank ? Get(FormattableString.Invariant($"rank/competitive/{rank}")) : null;

    /// <summary>The Wingman Skill Group badge for a rank number, 0&#8211;18.</summary>
    /// <param name="rank">The Wingman Skill Group number.</param>
    public static IconRef? WingmanRank(int rank) =>
        rank is >= 0 and <= MaxRank ? Get(FormattableString.Invariant($"rank/wingman/{rank}")) : null;

    /// <summary>
    ///     The badge for a player who has no rank to show. <paramref name="expired" /> distinguishes a
    ///     Skill Group that lapsed through inactivity from one that was never established, which CS2
    ///     draws differently; <paramref name="wingman" /> selects the Wingman set.
    /// </summary>
    /// <param name="expired">True for a lapsed rank, false for one never earned.</param>
    /// <param name="wingman">True for the Wingman badge set.</param>
    public static IconRef? UnrankedBadge(bool expired = false, bool wingman = false) =>
        Get($"rank/{(wingman ? "wingman" : "competitive")}/{(expired ? "expired" : "none")}");

    /// <summary>
    ///     The badge shown while a player still owes placement wins. Competitive only &#8212; CS2 ships no
    ///     Wingman equivalent, so a Wingman player in that state uses <see cref="UnrankedBadge" />.
    /// </summary>
    public static IconRef? NeedsWinsBadge() => Get("rank/competitive/needwins");

    /// <summary>The Skill Group's display name, or an empty string when the rank is out of range.</summary>
    /// <param name="rank">The Skill Group number, 0&#8211;18.</param>
    public static string RankName(int rank) =>
        rank >= 0 && rank < RankNames.Count ? RankNames[rank] : "";

    /// <summary>
    ///     The CS Rating tier a rating falls in. Resolved off the baked thresholds rather than by
    ///     recomputing CS2's <c>floor(rating / 5000)</c>, so the game stays the single source of the
    ///     numbers. A rating below the first threshold clamps to tier 0, as it does in CS2.
    /// </summary>
    /// <param name="csRating">A Premier CS Rating, e.g. 18340.</param>
    public static PremierTier TierForRating(int csRating)
    {
        if (PremierTiers.Count == 0)
        {
            throw new InvalidOperationException("the bake carried no Premier tiers");
        }

        PremierTier found = PremierTiers[0];
        foreach (PremierTier t in PremierTiers)
        {
            if (csRating >= t.MinRating)
            {
                found = t;
            }
        }

        return found;
    }

    /// <summary>
    ///     The Premier emblem already washed for a rating's tier. <b>Pre-tinted, unlike every other
    ///     icon</b>: the emblem is shaded rather than flat white, so it is baked per tier with a multiply
    ///     and must be drawn as-is — tinting it again would flatten it.
    /// </summary>
    /// <param name="csRating">A Premier CS Rating.</param>
    public static IconRef? PremierEmblem(int csRating) => Get(TierForRating(csRating).Key);

    /// <summary>The emblem drawn before a rating exists. Also pre-coloured; draw it untinted.</summary>
    public static IconRef? PremierUnranked() => Get("premier/unranked");

    /// <summary>Looks up a fully-qualified key such as <c>equipment/ak47</c>; null when absent.</summary>
    /// <param name="key">Namespace-qualified icon key.</param>
    public static IconRef? Get(string key) =>
        key is not null && Catalogue.TryGetValue(key, out IconRef? icon) ? icon : null;

    /// <summary>
    ///     The icon for a demo weapon string, taken verbatim from <c>player_death.weapon</c>. Null when
    ///     CS2 has no artwork for it, which includes every environment death.
    /// </summary>
    /// <param name="demoWeaponName">The weapon string as the demo spells it, e.g. <c>usp_silencer</c>.</param>
    public static IconRef? Weapon(string? demoWeaponName) =>
        string.IsNullOrEmpty(demoWeaponName) ? null : Get("equipment/" + demoWeaponName);

    /// <summary>The icon for one kill-feed modifier.</summary>
    /// <param name="modifier">The modifier to draw.</param>
    public static IconRef? Modifier(KillModifier modifier) =>
        modifier is KillModifier.None ? null : Get("modifier/" + Name(modifier));

    /// <summary>A curated UI icon by short name, e.g. <c>bomb</c>, <c>clock</c>, <c>health</c>.</summary>
    /// <param name="name">The short name, without the namespace.</param>
    public static IconRef? Ui(string name) => Get("ui/" + name);

    /// <summary>
    ///     CS2's icon for a map, keyed by the map name a demo already carries (<c>de_dust2</c>). Null for
    ///     a community map or anything newer than the last bake, which is the common case rather than an
    ///     error - callers should have something to fall back to.
    /// </summary>
    /// <param name="mapName">The map name, e.g. <c>de_mirage</c>.</param>
    public static IconRef? Map(string? mapName) =>
        string.IsNullOrEmpty(mapName) ? null : Get("map/" + mapName);

    /// <summary>
    ///     The baked scale to decode for a target pixel height: the smallest one that is at least as tall,
    ///     else the largest available. Downscaling a 96 px master is near-lossless to 48 px and good to
    ///     32 px; upscaling is what actually looks wrong, so this never returns a scale below the target
    ///     unless there is nothing bigger.
    /// </summary>
    /// <param name="pixelHeight">The height the icon will be drawn at.</param>
    public static int ScaleFor(double pixelHeight)
    {
        // Indexed, not foreach: Scales is exposed as IReadOnlyList<int>, and iterating that through the
        // interface boxes an enumerator on every call. This sits on a per-frame render path.
        for (int i = 0; i < Scales.Count; i++)
        {
            if (Scales[i] >= pixelHeight)
            {
                return Scales[i];
            }
        }

        return Scales[Scales.Count - 1];
    }

    private static string Name(KillModifier m) => m switch
    {
        KillModifier.Headshot => "headshot",
        KillModifier.NoScope => "noscope",
        KillModifier.Penetrate => "penetrate",
        KillModifier.Smoke => "smoke",
        KillModifier.Blind => "blind",
        KillModifier.InAir => "inair",
        KillModifier.Suicide => "suicide",
        KillModifier.Domination => "domination",
        KillModifier.Revenge => "revenge",
        _ => throw new ArgumentOutOfRangeException(nameof(m), m, "not a single modifier")
    };

    internal static Stream? OpenResource(string logicalName) =>
        ResourceNames.TryGetValue(logicalName, out string? actual)
            ? Owner.GetManifestResourceStream(actual)
            : null;

    internal static string PathFor(string key, int scale) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{key}@{scale}.png");
}
