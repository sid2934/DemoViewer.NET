#region

using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>Where a theme came from: a shipped built-in or a user drop-in JSON file.</summary>
public enum ThemeSource
{
    /// <summary>Shipped with the app.</summary>
    BuiltIn,

    /// <summary>Loaded from the user's <c>&lt;config&gt;/themes/</c> directory.</summary>
    User
}

/// <summary>
///     One selectable theme in the central theme system. The built-ins
///     <c>dark</c> / <c>light</c> / <c>system</c> map to Avalonia's native <see cref="ThemeVariant" />s (their
///     tokens live in <c>DarkPalette.axaml</c>'s ThemeDictionaries). A custom theme (High-Contrast, E-Girl, or a
///     user drop-in) declares a <b>base</b> variant it inherits (Light or Dark) plus a set of token overrides;
///     the <see cref="ThemeRegistry" /> registers it as a custom <c>ThemeVariant(Id, base)</c> so its overrides
///     win and everything it omits falls through to the base: including all FluentTheme base-control colours.
/// </summary>
public sealed record Theme
{
    /// <summary>Stable id, persisted in <c>settings.json</c> (e.g. <c>"dark"</c>, <c>"egirl"</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name shown in the Settings theme picker.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The variant to set as <c>RequestedThemeVariant</c>: native (Dark/Light/Default) or a custom one.</summary>
    public required ThemeVariant Variant { get; init; }

    /// <summary>Built-in vs a user drop-in.</summary>
    public ThemeSource Source { get; init; } = ThemeSource.BuiltIn;
}
