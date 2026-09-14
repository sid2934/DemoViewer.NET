#region

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SkiaSharp;
using SteamDatabase.ValvePak;
using Svg.Skia;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

#endregion

namespace DemoViewer.NET.AssetBaker;

/// <summary>
///     Bakes CS2's weapon and HUD iconography out of <c>pak01_dir.vpk</c> into PNGs the app can ship.
///     <para>
///         <b>They are vector, not texture.</b> CS2 stores this artwork as compiled Panorama SVG
///         (<c>.vsvg_c</c>), and VRF hands back the original SVG bytes verbatim — there is no image
///         pipeline on the source side at all. The rasterising happens here, in the baker, for the same
///         reason the map bake lives here: <see cref="SKSvg" /> comes from Svg.Skia, which needs
///         SkiaSharp 3.x, and the app is pinned to 2.88.x by Avalonia's canvas-lease type identity. The
///         file boundary is what keeps those two apart.
///     </para>
///     <para>
///         <b>Two scales, not three.</b> Measured against a purpose-rendered target, a 96 px master
///         downscales to 48 px at a mean alpha error of 1.65/255 and to 32 px at 5.3, but to 16 px at
///         12.2 — soft enough to see on a kill-feed chip. So 96 covers everything from 48 up, 32 covers
///         the small chips crisply, and a third tier would buy nothing.
///     </para>
/// </summary>
public static class Icons
{
    /// <summary>
    ///     Bumped when the manifest shape changes, so a stale bake fails loudly instead of oddly.
    ///     <para>2 added <c>blank</c>: the keys CS2 ships deliberately empty artwork for.</para>
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>The baked pixel heights. Width follows the source aspect and is not constrained.</summary>
    public static readonly int[] Scales = [32, 96];

    /// <summary>
    ///     Bakes the curated set into <paramref name="outDir" /> and writes <c>icons.json</c> beside it.
    /// </summary>
    /// <param name="pakPath">Path to <c>game/csgo/pak01_dir.vpk</c>.</param>
    /// <param name="outDir">Output root; <c>equipment/</c>, <c>modifier/</c> and <c>ui/</c> are created under it.</param>
    /// <param name="bakerVersion">Recorded in the manifest so a re-bake is attributable.</param>
    /// <returns>A one-line-per-namespace summary for the console.</returns>
    public static string Bake(string pakPath, string outDir, string bakerVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);

        using Package pak = new();
        pak.Read(pakPath);

        Dictionary<string, PackageEntry> wanted = Select(pak);
        Directory.CreateDirectory(outDir);

        SortedDictionary<string, IconEntry> manifest = new(StringComparer.Ordinal);
        Crc32 crc = new();
        List<string> blank = [];
        Dictionary<string, string> seen = new(StringComparer.Ordinal);
        List<string> duplicates = [];

        foreach ((string key, PackageEntry entry) in wanted.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            byte[] svg = ReadSvg(pak, entry);

            // The version hash covers the SOURCE bytes, not our PNGs: a CS2 art update must show up as a
            // manifest diff even if the rasteriser happens to round a pixel the same way.
            crc.Append(svg);

            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(svg))[..16];
            if (seen.TryGetValue(hash, out string? twin))
            {
                duplicates.Add($"{key} == {twin}");
            }
            else
            {
                seen[hash] = key;
            }

            SKRect box = ViewBox(svg) ?? throw new InvalidOperationException($"{key}: no usable viewBox");

            using SKSvg picture = new();
            using (MemoryStream ms = new(svg))
            {
                picture.Load(ms);
            }

            if (picture.Picture is not { } pic)
            {
                blank.Add(key);
                continue;
            }

            List<byte[]> encoded = [];
            bool empty = true;
            bool tintable = true;
            foreach (int height in Scales)
            {
                using SKBitmap bmp = Render(pic, box, height);
                empty &= IsFullyTransparent(bmp);
                tintable &= IsAchromatic(bmp);
                using SKData png = bmp.Encode(SKEncodedImageFormat.Png, 100)
                                   ?? throw new InvalidOperationException($"{key}: PNG encode failed");
                encoded.Add(png.ToArray());
            }

            // world / worldent / trigger_hurt are deliberately empty 103-byte SVGs in CS2 — an
            // environment death shows no icon. Emitting nothing lets lookup return null and say the same
            // thing, instead of shipping three blank PNGs for a consumer to special-case.
            if (empty)
            {
                blank.Add(key);
                continue;
            }

            for (int i = 0; i < Scales.Length; i++)
            {
                string dest = Path.Combine(outDir, PathFor(key, Scales[i]));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, encoded[i]);
            }

            // rank/ is declared rather than measured: the badges are a coherent set of pictures, and a
            // greyscale one (an unranked plate) would otherwise be judged tintable on its pixels alone.
            bool isPicture = key.StartsWith("rank/", StringComparison.Ordinal)
                             || key.StartsWith("map/", StringComparison.Ordinal);
            manifest[key] = new IconEntry(Round(box.Width), Round(box.Height), tintable && !isPicture);
        }

        int premierCount = BakePremier(pak, outDir, manifest, crc);

        string version = Convert.ToHexString(crc.GetCurrentHash()).ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(outDir, "icons.json"),
            JsonSerializer.Serialize(
                new IconManifest(SchemaVersion, bakerVersion, version, Scales, manifest,
                    [.. IconSet.PremierTiers.Select(t => new PremierTierEntry(t.Name, t.Hex, t.MinRating))],
                    IconSet.RankNames,
                    [.. blank]),
                ManifestJson));

        StringBuilder log = new();
        foreach (string ns in new[] { "equipment", "modifier", "ui", "rank", "map" })
        {
            int n = manifest.Keys.Count(k => k.StartsWith(ns + "/", StringComparison.Ordinal));
            log.Append(CultureInfo.InvariantCulture, $"  {ns,-10} {n,3} icons\n");
        }

        log.Append(CultureInfo.InvariantCulture,
            $"  {"premier",-10} {premierCount,3} icons (emblem x 7 tiers + unranked)");
        log.Append('\n');
        log.Append(CultureInfo.InvariantCulture, $"  {"blank",-10} {blank.Count,3} skipped");
        if (blank.Count > 0)
        {
            log.Append(" (").Append(string.Join(", ", blank)).Append(')');
        }

        log.Append('\n');
        if (duplicates.Count > 0)
        {
            // Reported, not collapsed: aliasing them would make one manifest key resolve to another
            // key's file, and the ~10 KB saved is not worth that indirection in every consumer.
            log.Append(CultureInfo.InvariantCulture, $"  {"duplicate",-10} {duplicates.Count,3} identical sources: ")
               .Append(string.Join("; ", duplicates)).Append('\n');
        }

        log.Append(CultureInfo.InvariantCulture, $"  version    {version}");
        return log.ToString();
    }

    /// <summary>
    ///     Bakes the Premier CS Rating emblem once per tier.
    ///     <para>
    ///         <b>Multiply, not SrcIn.</b> Every other icon in the set is flat white and is therefore an
    ///         alpha mask a consumer can tint however it likes. This one is not: it is a shaded plate with
    ///         bright chevrons (#E6E6E6 down to #393737 across six gradients), and replacing its colour
    ///         wholesale would flatten it into a silhouette. CS2 draws it with Panorama's
    ///         <c>wash-color</c>, which multiplies — so the bake multiplies too (see
    ///         <see cref="Modulate" /> for which Skia mode actually does that), and the shading survives
    ///         into every tier.
    ///     </para>
    ///     <para>
    ///         Baked as seven finished variants rather than tinted at runtime because the consumers cannot
    ///         all do it: an Avalonia opacity mask has no multiply, and the alternative would be a blend
    ///         path in each consumer for one icon. Seven copies of a 3 KB emblem is the cheaper answer.
    ///     </para>
    /// </summary>
    private static int BakePremier(Package pak, string outDir,
        SortedDictionary<string, IconEntry> manifest, Crc32 crc)
    {
        int made = 0;
        made += BakeEmblem(IconSet.PremierEmblem, tinted: true);
        made += BakeEmblem(IconSet.PremierEmblemUnranked, tinted: false);
        return made;

        int BakeEmblem(string source, bool tinted)
        {
            PackageEntry entry = pak.FindEntry(source + ".vsvg_c")
                                 ?? throw new InvalidOperationException(
                                     $"{source} is not in this CS2 build — the Premier emblem moved.");

            byte[] svg = ReadSvg(pak, entry);
            crc.Append(svg);
            SKRect box = ViewBox(svg) ?? throw new InvalidOperationException($"{source}: no viewBox");

            using SKSvg picture = new();
            using (MemoryStream ms = new(svg))
            {
                picture.Load(ms);
            }

            if (picture.Picture is not { } pic)
            {
                throw new InvalidOperationException($"{source}: no drawable content");
            }

            (string Key, SKColor? Wash)[] variants = tinted
                ? [.. IconSet.PremierTiers.Select(t => ("premier/" + t.Name, (SKColor?)Parse(t.Hex)))]
                : [("premier/unranked", (SKColor?)null)];

            foreach ((string key, SKColor? wash) in variants)
            {
                foreach (int height in Scales)
                {
                    using SKBitmap plain = Render(pic, box, height);
                    using SKBitmap outBmp = wash is { } tint ? Modulate(plain, tint) : plain.Copy();
                    using SKData png = outBmp.Encode(SKEncodedImageFormat.Png, 100)
                                       ?? throw new InvalidOperationException($"{key}: PNG encode failed");

                    string dest = Path.Combine(outDir, PathFor(key, height));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, png.ToArray());
                }

                manifest[key] = new IconEntry(Round(box.Width), Round(box.Height), Tintable: false);
            }

            return variants.Length;
        }
    }

    /// <summary>
    ///     src * tint on every channel, <b>alpha included</b>, so the emblem's transparent surround stays
    ///     transparent and its antialiased edges stay soft.
    ///     <para>
    ///         <b>Modulate, not Multiply — this is a trap.</b> <see cref="SKBlendMode.Multiply" /> is a
    ///         separable Porter-Duff mode: it composites as well as blends, so against a transparent
    ///         destination it still resolves to <c>ar = as + ad - as*ad = 1</c> and paints the tint at full
    ///         opacity. Every clear pixel in the emblem's bounding box would come out solid — the slanted
    ///         corners filled in, the badge rendered as a rectangle.
    ///         <see cref="SKBlendMode.Modulate" /> is the plain componentwise product on premultiplied
    ///         values, which leaves <c>ar = ad</c> and is what Panorama's <c>wash-color</c> actually does.
    ///     </para>
    /// </summary>
    private static SKBitmap Modulate(SKBitmap src, SKColor tint)
    {
        SKBitmap outBmp = new(src.Width, src.Height);
        using SKCanvas canvas = new(outBmp);
        canvas.Clear(SKColors.Transparent);
        using SKPaint paint = new()
        {
            ColorFilter = SKColorFilter.CreateBlendMode(tint, SKBlendMode.Modulate)
        };
        canvas.DrawBitmap(src, 0, 0, paint);
        return outBmp;
    }

    private static SKColor Parse(string hex) =>
        SKColor.TryParse(hex, out SKColor c) ? c : throw new ArgumentException($"bad colour {hex}", nameof(hex));

    /// <summary>
    ///     Lists the icons this CS2 build ships, marking the ones the curation already takes.
    ///     <para>
    ///         The point is <b>discovery</b>. Adding an icon means writing its source path into
    ///         <see cref="IconSet" />, and finding that path otherwise means dumping the archive by hand.
    ///         This prints exactly the string to paste, alongside whether it is already taken and whether
    ///         it is a flat mask (tintable) or a picture.
    ///     </para>
    /// </summary>
    /// <param name="pakPath">Path to <c>game/csgo/pak01_dir.vpk</c>.</param>
    /// <param name="filter">Case-insensitive substring; empty lists everything.</param>
    /// <param name="takenOnly">Show only what the curation already bakes.</param>
    /// <param name="freeOnly">Show only what it does not.</param>
    /// <returns>A console listing.</returns>
    public static string List(string pakPath, string filter, bool takenOnly, bool freeOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);

        using Package pak = new();
        pak.Read(pakPath);

        Dictionary<string, PackageEntry> wanted = Select(pak);
        Dictionary<string, string> takenBySource = new(StringComparer.Ordinal);
        foreach ((string key, PackageEntry entry) in wanted)
        {
            takenBySource[entry.GetFullPath()] = key;
        }

        if (pak.Entries is not { } all || !all.TryGetValue("vsvg_c", out List<PackageEntry>? entries))
        {
            throw new InvalidOperationException("pak01 contains no vsvg_c entries — wrong archive?");
        }

        StringBuilder log = new();
        int shown = 0, taken = 0;
        foreach (PackageEntry e in entries.OrderBy(x => x.GetFullPath(), StringComparer.Ordinal))
        {
            string full = e.GetFullPath();
            if (filter.Length > 0 && !full.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isTaken = takenBySource.TryGetValue(full, out string? key);
            if ((takenOnly && !isTaken) || (freeOnly && isTaken))
            {
                continue;
            }

            shown++;
            if (isTaken)
            {
                taken++;
            }

            // The source path without its extension is what IconSet entries are keyed on.
            string source = full[..^".vsvg_c".Length];
            log.Append(CultureInfo.InvariantCulture, $"{(isTaken ? "[x]" : "[ ]")} {source}");
            if (isTaken)
            {
                log.Append(CultureInfo.InvariantCulture, $"  ->  {key}");
            }

            log.AppendLine();
        }

        log.AppendLine();
        log.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{shown} shown, {taken} already baked, {shown - taken} available"));

        if (shown - taken > 0)
        {
            log.AppendLine();
            log.AppendLine("To add one, put its path in IconSet (Ui, Modifiers or Ranks) as");
            log.AppendLine("    [source-path] = our-name     (both quoted C# strings)");
            log.AppendLine("then re-run with the icons flag and commit assets/icons/.");
        }

        return log.ToString();
    }

    /// <summary>The manifest-relative path of one baked icon, e.g. <c>equipment/ak47@32.png</c>.</summary>
    /// <param name="key">The icon key, namespace included.</param>
    /// <param name="scale">One of <see cref="Scales" />.</param>
    public static string PathFor(string key, int scale) =>
        FormattableString.Invariant($"{key}@{scale}.png");

    // Which vpk entries the curation asks for, keyed by our name. Built by walking the archive's own
    // vsvg_c list rather than by probing each wanted path, so a name Valve has renamed shows up as a
    // missing key at the end instead of as a silent hole.
    private static Dictionary<string, PackageEntry> Select(Package pak)
    {
        Dictionary<string, PackageEntry> wanted = new(StringComparer.Ordinal);
        Dictionary<string, string> claimedBy = new(StringComparer.Ordinal);
        if (pak.Entries is not { } all || !all.TryGetValue("vsvg_c", out List<PackageEntry>? entries))
        {
            throw new InvalidOperationException("pak01 contains no vsvg_c entries — wrong archive?");
        }

        // Two sources claiming one key would silently overwrite, and the loser would just never appear.
        // That is the mistake a growing curation actually makes, so it stops the bake by name.
        void Claim(string key, PackageEntry entry)
        {
            string source = entry.GetFullPath();
            if (claimedBy.TryGetValue(key, out string? first))
            {
                throw new InvalidOperationException(
                    $"two icons both want the key '{key}': {first} and {source}. Rename one in IconSet.");
            }

            claimedBy[key] = source;
            wanted[key] = entry;
        }

        foreach (PackageEntry e in entries)
        {
            string full = e.GetFullPath();
            string bare = full[..^".vsvg_c".Length];

            if (full.StartsWith(IconSet.EquipmentDir, StringComparison.Ordinal))
            {
                Claim("equipment/" + Path.GetFileNameWithoutExtension(full), e);
            }
            else if (IconSet.Modifiers.TryGetValue(bare, out string? mod))
            {
                Claim("modifier/" + mod, e);
            }
            else if (IconSet.Ui.TryGetValue(bare, out string? ui))
            {
                Claim("ui/" + ui, e);
            }
            else if (IconSet.Ranks.TryGetValue(bare, out string? rank))
            {
                Claim("rank/" + rank, e);
            }
            else if (full.StartsWith(IconSet.MapIconDir, StringComparison.Ordinal)
                     && Path.GetFileNameWithoutExtension(full) is { } mapFile
                     && mapFile.StartsWith(IconSet.MapIconPrefix, StringComparison.Ordinal)
                     && !string.Equals(mapFile, IconSet.MapIconSkip, StringComparison.Ordinal))
            {
                Claim("map/" + mapFile[IconSet.MapIconPrefix.Length..], e);
            }
        }

        List<string> missing =
        [
            .. IconSet.Modifiers.Values.Where(v => !wanted.ContainsKey("modifier/" + v)).Select(v => "modifier/" + v),
            .. IconSet.Ui.Values.Where(v => !wanted.ContainsKey("ui/" + v)).Select(v => "ui/" + v),
            .. IconSet.Ranks.Values.Where(v => !wanted.ContainsKey("rank/" + v)).Select(v => "rank/" + v)
        ];

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "IconSet names not present in this CS2 build (Valve renamed or removed them): "
                + string.Join(", ", missing));
        }

        return wanted;
    }

    private static byte[] ReadSvg(Package pak, PackageEntry entry)
    {
        pak.ReadEntry(entry, out byte[] raw);
        using Resource res = new();
        res.Read(new MemoryStream(raw));
        return res.DataBlock is Panorama p
            ? p.Data
            : throw new InvalidOperationException($"{entry.GetFullPath()} is not a Panorama resource");
    }

    private static SKBitmap Render(SKPicture pic, SKRect box, int height)
    {
        float scale = height / box.Height;
        int width = Math.Max(1, (int)MathF.Ceiling(box.Width * scale));
        SKBitmap bmp = new(width, height);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-box.Left, -box.Top);
        canvas.DrawPicture(pic);
        return bmp;
    }

    /// <summary>
    ///     Whether an icon carries no hue, and may therefore be recoloured.
    ///     <para>
    ///         <b>The test is hue, not whiteness</b>, and the difference matters in both directions. Some
    ///         curated icons are genuinely coloured &#8212; the green defuser, the multi-hue MVP star
    ///         &#8212; and recolouring those destroys information, so they are excluded. But others are
    ///         merely <i>shaded</i> greys (the airborne wing, the armour plate): a whiteness test would
    ///         exclude those too, and an icon that is never tinted renders white-on-white in the light
    ///         theme. Flattening a grey icon's shading is a far smaller loss than making it invisible.
    ///     </para>
    ///     <para>
    ///         Judged over the whole image rather than per pixel, so one stray antialiased pixel cannot
    ///         flip an entire icon out of the tintable set.
    ///     </para>
    /// </summary>
    private static bool IsAchromatic(SKBitmap bmp)
    {
        const int HueSpread = 24; // max-min channel spread before a pixel counts as coloured
        long visible = 0, coloured = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor px = bmp.GetPixel(x, y);
                if (px.Alpha < 24)
                {
                    continue;
                }

                visible++;
                int hi = Math.Max(px.Red, Math.Max(px.Green, px.Blue));
                int lo = Math.Min(px.Red, Math.Min(px.Green, px.Blue));
                if (hi - lo > HueSpread)
                {
                    coloured++;
                }
            }
        }

        return visible == 0 || coloured * 200 < visible; // under 0.5% of the visible artwork
    }

    private static bool IsFullyTransparent(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).Alpha != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     The declared <c>viewBox</c>, which is the authority on an icon's size — <b>not</b>
    ///     <c>SKPicture.CullRect</c>. Most icons agree, but some do not: <c>icon_knife_tactical_big</c>
    ///     declares 104.375×32 and culls at 418×128, so cull-rect sizing would bake it four times too big.
    /// </summary>
    private static SKRect? ViewBox(byte[] svg)
    {
        XElement? root;
        try
        {
            using MemoryStream ms = new(svg);
            root = XDocument.Load(ms).Root;
        }
        catch (System.Xml.XmlException) { return null; }

        string? vb = (string?)root?.Attribute("viewBox");
        if (vb is null)
        {
            return null;
        }

        string[] parts = vb.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        float[] v = new float[4];
        for (int i = 0; i < 4; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
            {
                return null;
            }
        }

        return v[2] > 0 && v[3] > 0 ? new SKRect(v[0], v[1], v[0] + v[2], v[1] + v[3]) : null;
    }

    private static double Round(float v) => Math.Round(v, 3);

    // Matches Bundle.cs so the two baked manifests read the same way.
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The baked manifest: what exists, at what aspect, from which CS2 build.</summary>
    /// <param name="SchemaVersion">Manifest shape version.</param>
    /// <param name="BakerVersion">Baker build that produced it.</param>
    /// <param name="SourceHash">CRC32 over every source SVG, in key order.</param>
    /// <param name="Scales">The baked pixel heights.</param>
    /// <param name="Icons">Key → intrinsic size.</param>
    /// <param name="PremierTiers">The CS Rating tiers.</param>
    /// <param name="RankNames">Skill Group names by rank number.</param>
    /// <param name="Blank">
    ///     Keys CS2 ships deliberately EMPTY artwork for. Recorded rather than merely skipped so a
    ///     consumer can tell "absent because the game means nothing here" from "absent because it is
    ///     missing" — the first must draw nothing and stay silent, the second is worth reporting.
    /// </param>
    private sealed record IconManifest(
        int SchemaVersion,
        string BakerVersion,
        string SourceHash,
        IReadOnlyList<int> Scales,
        IReadOnlyDictionary<string, IconEntry> Icons,
        IReadOnlyList<PremierTierEntry> PremierTiers,
        IReadOnlyList<string> RankNames,
        IReadOnlyList<string> Blank);

    /// <summary>One CS Rating tier in the manifest: key suffix, wash colour, and lower bound.</summary>
    /// <param name="Name">Key suffix, e.g. <c>tier3</c>.</param>
    /// <param name="Hex">CS2's wash colour.</param>
    /// <param name="MinRating">Lowest CS Rating in the tier.</param>
    private sealed record PremierTierEntry(string Name, string Hex, int MinRating);

    /// <summary>
    ///     One icon's intrinsic size in source units. Carried because <b>102 of the curated 167 icons are
    ///     not square</b> — they share a 32-unit height and vary in width from 0.33× to 4.78× — so a
    ///     consumer that assumes a square box squashes every rifle.
    /// </summary>
    /// <param name="W">Intrinsic width in viewBox units.</param>
    /// <param name="H">Intrinsic height in viewBox units.</param>
    /// <param name="Tintable">
    ///     True when the artwork is a white alpha mask and may be recoloured; false when it is a picture
    ///     (rank badges, the Premier emblem, a handful of multi-colour UI icons) and must be drawn as-is.
    /// </param>
    private sealed record IconEntry(double W, double H, bool Tintable);
}
