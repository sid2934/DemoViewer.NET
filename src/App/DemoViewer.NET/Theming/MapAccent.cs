#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     Attached behavior that paints a <see cref="Border" />'s background with a stable per-map
///     accent (deterministic name-hash → hue): the v0.6.0 replacement for
///     <c>MapAccentConverter</c>, which as an <c>IValueConverter</c> had no theme-variant access
///     and no invalidation channel (its S/V tuning was frozen for Dark).
///     <para>
///         The HUE still comes from the map-name hash (identity must stay stable per map), but
///         saturation/value are DECODED from the <c>MapAccentRef</c> token (hue-ignored by
///         convention, see the token catalog) and the empty-key neutral is
///         <c>MapAccentNeutral</c>, so both re-tune per theme. Re-applies on the host's
///         <see cref="StyledElement.ActualThemeVariantChanged" />; the hook is per-border and
///         self-referencing, so recycled virtualized rows neither leak nor double-subscribe.
///     </para>
/// </summary>
public static class MapAccent
{
    /// <summary>The map name/key driving the accent. Null/empty → the neutral token.</summary>
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<Border, string?>("Key", typeof(MapAccent));

    // Guards the one-time ActualThemeVariantChanged hookup per border (never unhooked: the
    // subscription is border→border, so it cannot outlive the control).
    private static readonly AttachedProperty<bool> _hookedProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("Hooked", typeof(MapAccent));

    static MapAccent() =>
        KeyProperty.Changed.AddClassHandler<Border>(static (border, _) =>
        {
            if (!border.GetValue(_hookedProperty))
            {
                border.SetValue(_hookedProperty, true);
                border.ActualThemeVariantChanged += static (s, _) => Apply((Border)s!);
            }

            Apply(border);
        });

    /// <summary>Gets the attached map key.</summary>
    public static string? GetKey(Border border) => border.GetValue(KeyProperty);

    /// <summary>Sets the attached map key.</summary>
    public static void SetKey(Border border, string? value) => border.SetValue(KeyProperty, value);

    private static void Apply(Border border)
    {
        string key = border.GetValue(KeyProperty) ?? "";
        if (key.Length == 0)
        {
            border.Background = new SolidColorBrush(
                ThemeColors.Get("MapAccentNeutral", border.ActualThemeVariant, "#404068"));
            return;
        }

        // Deterministic hash → hue (unchanged from the converter, so per-map identity is stable
        // across the migration); S/V come from the theme's MapAccentRef.
        int hash = 0;
        foreach (char c in key)
        {
            hash = hash * 31 + char.ToLowerInvariant(c) & 0x7FFFFFFF;
        }

        Color reference = ThemeColors.Get("MapAccentRef", border.ActualThemeVariant, "#B85353");
        (double s, double v) = SaturationValueOf(reference);
        border.Background = new SolidColorBrush(FromHsv(hash % 360, s, v));
    }

    // Extracts (saturation, value) from the reference token; its hue is ignored by convention.
    private static (double S, double V) SaturationValueOf(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        double min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        return (max <= 0 ? 0 : (max - min) / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
