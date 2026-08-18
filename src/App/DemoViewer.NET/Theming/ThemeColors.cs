#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     Resolves a colour TOKEN (a <see cref="ISolidColorBrush" /> key in the app's <c>ThemeDictionaries</c>)
///     for a given theme variant, so <b>code-drawn</b> surfaces (the 2D radar renderer, the syntax highlighter)
///     read the SAME token namespace that XAML <c>{DynamicResource}</c> uses. This is the "no per-file changes"
///     contract of the central theme system: a colour lives in the palette once,
///     every theme (built-in or a user drop-in) supplies it, and both markup and code pick it up — so a new
///     theme needs no edits to any consuming surface.
///     <para>
///         Resolution goes through <see cref="Application" /> resources for the requested variant, honouring
///         the variant → <c>InheritVariant</c> → <c>Default</c> fallback chain. A missing token (or no running
///         Application, e.g. a unit test) yields the supplied fallback, so a surface always renders.
///     </para>
/// </summary>
public static class ThemeColors
{
    /// <summary>
    ///     Resolves <paramref name="key" /> to a <see cref="Color" /> for <paramref name="variant" />, else
    ///     <paramref name="fallback" />.
    /// </summary>
    public static Color Get(string key, ThemeVariant? variant, Color fallback) =>
        Application.Current?.TryGetResource(key, variant ?? ThemeVariant.Default, out object? o) == true
        && o is ISolidColorBrush b
            ? b.Color
            : fallback;

    /// <summary>Convenience overload taking a hex fallback (e.g. <c>"#15181C"</c>).</summary>
    public static Color Get(string key, ThemeVariant? variant, string fallbackHex) =>
        Get(key, variant, Color.Parse(fallbackHex));
}
