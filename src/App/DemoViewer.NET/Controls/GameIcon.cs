#region

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DemoViewer.NET.GameIcons;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Draws one baked CS2 icon, tinted to <see cref="Foreground" />.
///     <para>
///         <b>Tinted through an opacity mask, because Avalonia has no image tint.</b> Every baked icon is
///         white on transparent, which makes it an alpha mask rather than a picture: the control fills its
///         bounds with the foreground brush and lets the icon's own alpha cut the shape out. One set of
///         bytes therefore serves T orange, CT blue, and both themes, with no per-colour variants to bake
///         or keep in sync. It also means the icon re-themes live like everything else, because
///         <see cref="Foreground" /> is an ordinary styled property and takes a <c>DynamicResource</c>.
///     </para>
///     <para>
///         <b>Sized by height, never by a square box.</b> Most of the catalogue is not square — an AWP is
///         3.42× wider than it is tall — so <see cref="IconHeight" /> is the input and the width follows
///         from the manifest's intrinsic aspect. Measuring the bitmap instead would work, but only after a
///         decode; the manifest knows the aspect without touching pixels.
///     </para>
/// </summary>
public sealed class GameIcon : Control
{
    // Decoded bitmaps are shared process-wide and never evicted: the whole catalogue at both scales is
    // well under a megabyte decoded, and a kill feed re-renders the same dozen icons every frame.
    private static readonly ConcurrentDictionary<string, Bitmap?> Decoded = new(StringComparer.Ordinal);

    /// <summary>The namespace-qualified icon key, e.g. <c>equipment/ak47</c>.</summary>
    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<GameIcon, string?>(nameof(Key));

    /// <summary>Text drawn in the icon's place when the key is missing from the bake.</summary>
    public static readonly StyledProperty<string?> FallbackProperty =
        AvaloniaProperty.Register<GameIcon, string?>(nameof(Fallback));

    /// <summary>Face for the fallback text. Inherited, so it matches the surrounding run by default.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<GameIcon>();

    /// <summary>Size for the fallback text. Inherited.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<GameIcon>();

    /// <summary>The drawn height in device-independent pixels. Width follows the icon's aspect.</summary>
    public static readonly StyledProperty<double> IconHeightProperty =
        AvaloniaProperty.Register<GameIcon, double>(nameof(IconHeight), 16d);

    /// <summary>The tint. Inherited, so an icon in a themed panel picks the panel's colour up for free.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<GameIcon>();

    private FormattedText? _fallbackText;

    static GameIcon()
    {
        AffectsMeasure<GameIcon>(KeyProperty, IconHeightProperty, FallbackProperty,
            FontFamilyProperty, FontSizeProperty);
        AffectsRender<GameIcon>(KeyProperty, IconHeightProperty, ForegroundProperty, FallbackProperty,
            FontFamilyProperty, FontSizeProperty);
    }

    /// <summary>The namespace-qualified icon key, e.g. <c>equipment/ak47</c>.</summary>
    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>The drawn height in device-independent pixels.</summary>
    public double IconHeight
    {
        get => GetValue(IconHeightProperty);
        set => SetValue(IconHeightProperty, value);
    }

    /// <summary>The tint applied to the icon's alpha, and the colour of the fallback text.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>
    ///     Text drawn where the icon would have gone when the key is <b>missing</b> from the bake — a
    ///     typo, an uncurated key, or one a CS2 update renamed.
    ///     <para>
    ///         <b>Not drawn for artwork CS2 ships deliberately empty</b> (the environment deaths). That
    ///         absence is the game's own answer and the correct render is nothing at all; substituting a
    ///         word there would invent meaning. The catalogue draws that distinction, not the call site,
    ///         which is what lets icons be adopted one site at a time without either a hole or a lie.
    ///     </para>
    /// </summary>
    public string? Fallback
    {
        get => GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    /// <summary>Face for the fallback text.</summary>
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>Size for the fallback text.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FallbackProperty || change.Property == FontFamilyProperty
                                                || change.Property == FontSizeProperty
                                                || change.Property == ForegroundProperty)
        {
            _fallbackText = null;
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double h = IconHeight;
        if (Resolve(out IconAvailability availability) is { } icon)
        {
            return new Size(icon.WidthAt(h), h);
        }

        return ShowsFallback(availability) && Text() is { } text
            ? new Size(text.Width, Math.Max(h, text.Height))
            : default;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IBrush? brush = Foreground;
        if (Resolve(out IconAvailability availability) is not { } icon)
        {
            if (ShowsFallback(availability) && brush is not null && Text() is { } text)
            {
                context.DrawText(text, new Point(0, (Math.Max(IconHeight, text.Height) - text.Height) / 2));
            }

            return;
        }

        if (icon.Tintable && brush is null)
        {
            return;
        }

        double h = IconHeight;
        Rect area = new(0, 0, icon.WidthAt(h), h);
        if (area.Width <= 0 || area.Height <= 0 || Bitmap(icon, h) is not { } bitmap)
        {
            return;
        }


        // A picture is drawn as it was baked. Rank badges and Premier emblems carry their own colour,
        // and masking one would replace all of it with a single flat fill — a Global Elite badge would
        // come out as an orange blob. Only hue-free artwork is a mask.
        if (!icon.Tintable)
        {
            context.DrawImage(bitmap, area);
            return;
        }

        // The mask is the artwork; the fill is the colour. Order matters: the mask has to be pushed
        // before the rectangle that it cuts.
        using (context.PushOpacityMask(new ImageBrush(bitmap) { Stretch = Stretch.Fill }, area))
        {
            context.FillRectangle(brush!, area);
        }
    }

    private IconRef? Resolve(out IconAvailability availability) =>
        IconCatalogue.Demand(Key, out availability);

    /// <summary>
    ///     Whether the fallback text stands in for the icon.
    ///     <para>
    ///         <b>Only <see cref="IconAvailability.Blank" /> suppresses it.</b> A missing key falls back
    ///         because something should have drawn and did not; a <see cref="IconAvailability.None" />
    ///         — no key bound at all — also falls back, because that is a caller who has a glyph of its
    ///         own and simply no icon to prefer over it, which is how the timeline draws annotation and
    ///         round markers CS2 has no artwork for. Blank is the one case where the game itself is
    ///         saying there is nothing here, and inventing a word for it would be a lie.
    ///     </para>
    /// </summary>
    private static bool ShowsFallback(IconAvailability availability) =>
        availability is IconAvailability.Missing or IconAvailability.None;

    // Memoised: the fallback is re-rendered only when its text, face, size or colour changes.
    private FormattedText? Text()
    {
        if (Fallback is not { Length: > 0 } fallback || Foreground is not { } brush)
        {
            return null;
        }

        return _fallbackText ??= new FormattedText(
            fallback, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), FontSize, brush);
    }

    private static Bitmap? Bitmap(IconRef icon, double drawnHeight)
    {
        // Pick the master by the height actually being drawn, so a 16 px chip decodes the 32 px file
        // rather than a 96 px one it would only throw away.
        int scale = IconCatalogue.ScaleFor(drawnHeight);
        return Decoded.GetOrAdd($"{icon.Key}@{scale}", _ =>
        {
            try
            {
                using Stream stream = icon.Open(scale);
                return new Bitmap(stream);
            }
            catch (ArgumentOutOfRangeException)
            {
                // A key that is in the manifest but missing its bytes is a broken bake, not a caller
                // error; draw nothing rather than taking the whole view down with it.
                return null;
            }
        });
    }
}
