#region

using System.Text;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The export HUD's kill feed: up to six rows in the top-right corner, carrying the same modifiers the
///     XAML feed shows: headshot, wallbang, no-scope, through-smoke, blind, airborne, flash assist.
///     <para>
///         <b>One source, and its glyphs are not always the XAML feed's.</b> Rows come from the same
///         <c>KillFeedTimeline.Window</c> the view-model calls, through an <see cref="IHudDataSource" />,
///         so the feed and the export can't disagree about which kills to show. This layer only decides
///         how a row looks. It draws through the embedded Latin-only Inter (<see cref="TextBlobCache" />)
///         rather than the platform UI font; two of the panel's symbols have no glyph in Inter, so where
///         they differ the export uses a token that exists: the modifier is the contract, the character
///         is not.
///     </para>
///     <para>
///         <b>Three runs per row, not one.</b> Attacker and victim are drawn in their own side's colour,
///         the weapon and modifiers between them in the secondary text colour, so a wall of white reads as
///         "our side is trading". Each run is memoised through <see cref="TextBlobCache" />, so three runs
///         cost no more than one. A row whose side the demo could not resolve
///         (<c>KillFeedRow.AttackerTeam == 0</c>) keeps the neutral colour.
///         <b>No kill loses its row over a missing team.</b> Like <see cref="ClockLayer" />, it is opt-in
///         and draws in the topmost band only.
///     </para>
/// </summary>
public sealed class KillFeedLayer : ISceneLayer
{
    /// <summary>Rows drawn at most, matching the view-model's own window (<c>KillFeedTimeline</c>).</summary>
    public const int MaxRows = 6;

    /// <summary>Row pitch: the vertical advance from one row's top to the next's.</summary>
    private const float LineHeightFactor = 1.75f;

    private readonly StringBuilder _builder = new(96);
    private readonly IHudDataSource _data;
    private readonly IIconSource? _icons;
    private readonly bool _ownsText;
    private readonly SKPaint _paint;
    private readonly Dictionary<KillFeedRow, RowVisual> _rendered = new(256);
    private readonly SKPaint? _iconPaint;
    private readonly HudStyle _style;
    private readonly TextBlobCache _text;

    private HudSnapshot _snapshot = HudSnapshot.Empty;

    /// <summary>Creates the layer.</summary>
    /// <param name="data">The tick → HUD state function.</param>
    /// <param name="style">Colours and metrics; the shipped look when null.</param>
    /// <param name="text">A shared blob cache; a private one when null, disposed with the layer.</param>
    /// <param name="icons">
    ///     CS2's own artwork for the weapon and the modifiers. <b>Null keeps the text tokens</b>, which is
    ///     the shipped default: every export golden was baselined against them, so a source arrives here
    ///     only when a caller has decided to re-baseline.
    /// </param>
    public KillFeedLayer(
        IHudDataSource data,
        HudStyle? style = null,
        TextBlobCache? text = null,
        IIconSource? icons = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _icons = icons;
        _style = style ?? new HudStyle();
        _ownsText = text is null;
        _text = text ?? new TextBlobCache();

        // Hoisted, not per-draw. Design §6 requires 0 B/frame in steady state, and a paint plus a
        // blend-mode colour filter built per icon per frame is exactly the allocation it forbids. The
        // tint never varies — the feed draws its icons in the same dim colour as its middle run — so
        // both are built once here. The CI allocation bench mounts no HUD layer, so nothing else would
        // have caught this.
        if (icons is not null)
        {
            _iconPaint = new SKPaint
            {
                IsAntialias = true,
                ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor(ClockLayer.DimTextArgb), SKBlendMode.SrcIn)
            };
        }
        _paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.HudKillFeed;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Hud;

    /// <inheritdoc />
    public int Order => 80;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        _snapshot = _data.At(time.Tick);
        return false;
    }

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (!ClockLayer.IsTopBand(ctx))
        {
            return;
        }

        IReadOnlyList<KillFeedRow> rows = _snapshot.KillRows;
        int count = Math.Min(rows.Count, MaxRows);
        if (count == 0)
        {
            return;
        }

        float right = ctx.PaneBounds.Right - _style.MarginPx;
        float y = _style.MarginPx;
        float lineHeight = _style.FontSizePx * LineHeightFactor;

        // Oldest first, top to bottom, the same order the XAML feed stacks them in.
        int first = rows.Count - count;
        for (int i = first; i < rows.Count; i++)
        {
            KillFeedRow row = rows[i];
            RowVisual visual = Compose(row);
            RowText parts = visual.Text;

            if (_text.Get(parts.Attacker, _style.FontSizePx) is not { } attacker ||
                _text.Get(parts.Middle, _style.FontSizePx) is not { } middle ||
                _text.Get(parts.Victim, _style.FontSizePx) is not { } victim)
            {
                continue;
            }

            float padX = _style.MarginPx * 0.55f;
            float padY = _style.MarginPx * 0.3f;
            float iconH = _style.FontSizePx;
            float iconGap = iconH * 0.25f;
            float iconsW = MeasureIcons(visual.IconKeys, iconH, iconGap);
            float rowW = attacker.Width + iconsW + middle.Width + victim.Width;

            // Both rectangles are laid out from the same numbers: each part's Width is its ADVANCE, so
            // the three runs abut exactly as one shaped line would have; Height is one LINE BOX, so every
            // row is the same height whether or not it happens to contain a descender.
            _paint.Color = new SKColor(_style.PanelArgb);
            canvas.DrawRoundRect(
                new SKRect(right - rowW - padX * 2, y - padY, right, y + attacker.Height + padY),
                3f, 3f, _paint);

            float x = right - rowW - padX;
            x = DrawRun(canvas, attacker, x, y, SideColor(ctx, row.AttackerTeam));
            x = DrawIcons(canvas, visual.IconKeys, x, y, attacker.Height, iconH, iconGap);
            x = DrawRun(canvas, middle, x, y, new SKColor(ClockLayer.DimTextArgb));
            DrawRun(canvas, victim, x, y, SideColor(ctx, row.VictimTeam));

            y += lineHeight;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _paint.Dispose();
        _iconPaint?.Dispose();
        if (_ownsText)
        {
            _text.Dispose();
        }
    }

    /// <summary>
    ///     The vertical band this layer claims at the top of its pane, in pixels: the inset plus a full
    ///     <see cref="MaxRows" /> feed.
    ///     <para>
    ///         <b>Published because a sibling has to stay out of it.</b> <c>hud.roster</c>'s CT column
    ///         claims the same right edge; the feed is <see cref="Order" /> 80 against the roster's 65, so
    ///         where they meet the feed paints straight over the cards. With the shipped
    ///         <see cref="HudStyle" /> and a five-a-side roster they meet on any pane shorter than about
    ///         552 px, which includes the top band of a 1280×720 two-level stacked export.
    ///     </para>
    ///     <para>
    ///         Sized from <see cref="MaxRows" /> rather than from the live row count, deliberately: a
    ///         reservation that shrank with the feed would shove the whole roster up and down the frame
    ///         every time a kill aged out of the window.
    ///     </para>
    /// </summary>
    /// <param name="style">The style the feed will be drawn with.</param>
    public static float ReservedBandHeight(HudStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        // MarginPx is the first row's top; MaxRows pitches cover the last row's box, because the pitch
        // (1.75 em) is comfortably taller than a line box plus its panel padding at every size the style
        // can take.
        return style.MarginPx + MaxRows * style.FontSizePx * LineHeightFactor;
    }

    /// <summary>
    ///     The one-line form of a kill row: the three drawn runs, concatenated. Public so the snapshot
    ///     test can assert the export's text against the same row the XAML feed binds, rather than against
    ///     a picture; the split into runs is a colour concern and must not change what a row says.
    /// </summary>
    /// <param name="row">The kill to render.</param>
    public static string Format(KillFeedRow row)
    {
        RowText parts = Build(new StringBuilder(96), row, spellWeapon: true, spellModifiers: true);
        return parts.Attacker + parts.Middle + parts.Victim;
    }

    // The keys this row draws, in feed order: the weapon, then each modifier it carries. Materialised
    // once per distinct row into the row cache — including the composed "equipment/<weapon>" string,
    // which would otherwise be a fresh allocation on every frame the row is on screen.
    private static string[] IconKeys(KillFeedRow row)
    {
        List<string> keys = [Weapons.Key(row.Weapon)];

        if (row.Headshot) { keys.Add("modifier/headshot"); }
        if (row.Penetrated) { keys.Add("modifier/penetrate"); }
        if (row.NoScope) { keys.Add("modifier/noscope"); }
        if (row.ThroughSmoke) { keys.Add("modifier/smoke"); }
        if (row.AttackerBlind) { keys.Add("modifier/blind"); }
        if (row.AttackerInAir) { keys.Add("modifier/inair"); }

        return [.. keys];
    }

    // Icons are height-sized and keep their own aspect, so a rifle takes the width a rifle needs. A key
    // with no artwork contributes nothing at all, which is how an environment death draws no weapon.
    private float MeasureIcons(string[] keys, float iconH, float gap)
    {
        if (_icons is null)
        {
            return 0;
        }

        float w = 0;
        foreach (string key in keys)
        {
            if (_icons.Lookup(key, iconH) is { } img)
            {
                w += gap + iconH * img.Width / img.Height;
            }
        }

        return w;
    }

    // Tinted through SrcIn: the baked artwork is white on transparent, so the blend replaces its colour
    // wholesale and one set of bytes serves every palette the feed can be drawn in.
    private float DrawIcons(SKCanvas canvas, string[] keys, float x, float top, float lineH,
        float iconH, float gap)
    {
        if (_icons is null || _iconPaint is null)
        {
            return x;
        }

        float cy = top + (lineH - iconH) / 2f;
        foreach (string key in keys)
        {
            if (_icons.Lookup(key, iconH) is not { } img)
            {
                continue;
            }

            x += gap;
            float w = iconH * img.Width / img.Height;
            canvas.DrawImage(img, SKRect.Create(x, cy, w, iconH), _iconPaint);
            x += w;
        }

        return x;
    }

    // Draws one run at the cursor and returns where the next one starts.
    private float DrawRun(SKCanvas canvas, ShapedText run, float x, float top, SKColor color)
    {
        _paint.Color = color;
        (float ox, float baseline) = run.OriginForTopLeft(x, top);
        canvas.DrawText(run.Blob, ox, baseline, _paint);
        return x + run.Width;
    }

    // The side tokens, or the feed's own text colour when the demo could not say. Deliberately NOT
    // ScenePalette.TeamFill: its "neither playing side" answer is the grey used for a spectator marker,
    // and a kill whose attacker simply never emitted a player_team must read as a normal kill, not as a
    // spectator's.
    private SKColor SideColor(SceneRenderContext ctx, int team) => team switch
    {
        2 => ctx.Palette.TeamT,
        3 => ctx.Palette.TeamCt,
        _ => new SKColor(_style.TextArgb)
    };

    // Composed once per distinct row and kept: a demo has a few hundred kills, six of which are on
    // screen, and re-composing six strings every frame is exactly the per-frame allocation §6 forbids.
    private RowVisual Compose(KillFeedRow row)
    {
        if (_rendered.TryGetValue(row, out RowVisual cached))
        {
            return cached;
        }

        string[] keys = _icons is null ? [] : IconKeys(row);

        // Two separate decisions, decided once per distinct row and never per frame.
        //
        // The MODIFIERS are spelled only when there is no icon source at all: with one, every modifier
        // the row carries is drawn as artwork and spelling them too would say everything twice.
        //
        // The WEAPON is spelled whenever its icon will not appear — either because there is no source,
        // or because that one key is missing from the bake. The exception is a key CS2 ships
        // deliberately empty: an environment death has no weapon, and printing "world" would name a
        // thing the game is telling us is not there.
        bool spellModifiers = _icons is null;
        bool spellWeapon = _icons is null
                           || (_icons.Lookup(keys[0], _style.FontSizePx) is null
                               && !_icons.IsBlankByDesign(keys[0]));

        RowVisual composed = new(Build(_builder, row, spellWeapon, spellModifiers), keys);
        _rendered[row] = composed;
        return composed;
    }

    private static RowText Build(StringBuilder builder, KillFeedRow row, bool spellWeapon,
        bool spellModifiers)
    {
        builder.Clear();
        builder.Append(row.Attacker);

        if (!string.IsNullOrEmpty(row.Assister))
        {
            // The assist stays on the attacker's run: it is credited to the attacker's side, and splitting
            // it out would mean a fourth run and a fourth cache entry for a chip most rows do not have.
            builder.Append(" +").Append(row.Assister);
            if (row.AssistedFlash)
            {
                // '*', not the XAML feed's '⚡'. The embedded face is Inter Regular and nothing else
                // (TextBlobCache), so U+26A1 rasterised as a .notdef box in every exported frame: a
                // glyph that says "missing font", not "flash assist". Same reason ✱ became " BL" below.
                builder.Append('*');
            }
        }

        string attacker = builder.ToString();

        builder.Clear();

        // With a source, the weapon and every modifier are drawn as artwork and must not also be spelled
        // out: the middle run collapses to the arrow, and the icons occupy the gap ahead of it.
        if (spellWeapon)
        {
            builder.Append("  ").Append(row.Weapon);
        }

        if (spellModifiers)
        {
            Append(builder, row.Headshot, " HS");
            Append(builder, row.Penetrated, " WB");
            Append(builder, row.NoScope, " NS");
            Append(builder, row.ThroughSmoke, " ≈"); // ≈ through smoke: U+2248, present in Inter
            Append(builder, row.AttackerBlind, " BL"); // a word, because U+2731 is not in the embedded face
            Append(builder, row.AttackerInAir, " ↑"); // ↑ killer airborne: U+2191, present in Inter
        }

        builder.Append("  →  "); // →
        return new RowText(attacker, builder.ToString(), row.Victim);
    }

    private static void Append(StringBuilder builder, bool condition, string token)
    {
        if (condition)
        {
            builder.Append(token);
        }
    }

    /// <summary>
    ///     Everything a row needs to draw, composed once and kept: the three text runs and, when the feed
    ///     draws artwork, the icon keys in feed order. Both halves are per-frame-allocation hazards if
    ///     rebuilt — the keys include a concatenated <c>equipment/&lt;weapon&gt;</c> string.
    /// </summary>
    /// <param name="Text">The three coloured runs.</param>
    /// <param name="IconKeys">Icon keys in draw order; empty when the feed is drawing text tokens.</param>
    private readonly record struct RowVisual(RowText Text, string[] IconKeys);

    // Weapon keys, interned per distinct weapon. A demo has a few dozen; composing the string per row
    // would otherwise leave one allocation behind for every kill in the match.
    private static class Weapons
    {
        private static readonly Dictionary<string, string> Cache = new(64, StringComparer.Ordinal);

        public static string Key(string weapon)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(weapon, out string? key))
                {
                    key = "equipment/" + weapon;
                    Cache[weapon] = key;
                }

                return key;
            }
        }
    }

    /// <summary>One row split at the two colour boundaries: killer | weapon and modifiers | victim.</summary>
    /// <param name="Attacker">The killer, plus any assist chip.</param>
    /// <param name="Middle">The weapon and modifier glyphs, arrow included.</param>
    /// <param name="Victim">The victim.</param>
    private readonly record struct RowText(string Attacker, string Middle, string Victim);
}
