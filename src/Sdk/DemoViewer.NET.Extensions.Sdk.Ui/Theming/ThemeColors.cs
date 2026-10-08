#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Theming;

/// <summary>
///     Resolves a palette token (a <see cref="ThemeTokens" /> key) to a colour for a theme variant, so a control
///     that draws itself reads the same colours XAML gets from <c>{DynamicResource}</c>, in whichever theme is
///     active, built-in or a user's.
///     <para>
///         Resolution goes through the running <see cref="Application" />'s resources for the requested variant,
///         falling back through the variant's inherited variant to <c>Default</c>. A missing token, or no
///         running application (a unit test), yields the supplied fallback, so a surface always renders.
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
