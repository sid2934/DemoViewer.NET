#region

using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     A parsed theme definition — the neutral shape a <see cref="ThemeRegistry" /> registers as a custom
///     variant (docs/ui/theme-system-plan.md). Produced by <see cref="ThemeJson" /> from either a user drop-in
///     file under <c>&lt;config&gt;/themes/</c> or an embedded built-in JSON (High-Contrast / E-Girl).
/// </summary>
/// <param name="Id">Stable id, persisted in <c>settings.json</c> and used as the <see cref="ThemeVariant" /> key.</param>
/// <param name="DisplayName">Human-readable name shown in the Settings theme picker.</param>
/// <param name="BaseVariant">The Light or Dark palette every omitted token (and FluentTheme colour) inherits.</param>
/// <param name="Tokens">The token overrides this theme supplies (a subset of the token namespace).</param>
public sealed record ThemeDefinition(
    string Id,
    string DisplayName,
    ThemeVariant BaseVariant,
    IReadOnlyDictionary<string, Color> Tokens);

/// <summary>
///     Safe JSON reader for theme definitions (docs/ui/theme-system-plan.md, T3). A theme file is pure DATA —
///     <c>{ id, name, base, tokens{ key: "#RRGGBB" } }</c> — parsed into a <see cref="ThemeDefinition" /> with no
///     object instantiation or code execution, so loading an untrusted drop-in is never code-exec (unlike runtime
///     AXAML). Malformed input degrades gracefully: an unparseable file yields <c>null</c>; individual bad token
///     entries are skipped while the rest of the file loads (partial files are valid — omitted tokens inherit the
///     base palette).
/// </summary>
public static class ThemeJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    ///     Parses <paramref name="json" /> into a <see cref="ThemeDefinition" />, or returns <c>null</c> if it is
    ///     not a usable theme (unparseable, or missing a non-empty <c>id</c>). <paramref name="sourceLabel" /> is
    ///     used only in a diagnostic — a file path for a drop-in, the built-in name otherwise.
    /// </summary>
    public static ThemeDefinition? TryParse(string json, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ThemeFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ThemeFileDto>(json, _options);
        }
        catch (JsonException)
        {
            return null; // not valid JSON — degrade to "theme unavailable"
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
        {
            return null; // a theme with no id cannot be persisted or resolved
        }

        string id = dto.Id.Trim();
        string name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name.Trim();
        ThemeVariant baseVariant = ParseBase(dto.Base);

        Dictionary<string, Color> tokens = new(StringComparer.Ordinal);
        if (dto.Tokens is not null)
        {
            foreach ((string key, string? hex) in dto.Tokens)
            {
                if (string.IsNullOrWhiteSpace(key) || hex is null)
                {
                    continue;
                }

                if (TryParseColor(hex, out Color color))
                {
                    tokens[key.Trim()] = color;
                }
                // else: skip this one token, keep the rest (partial files degrade gracefully)
            }
        }

        return new ThemeDefinition(id, name, baseVariant, tokens);
    }

    // "light" → Light, anything else (incl. null / "dark") → Dark. Dark is the app's canonical base, so an
    // unspecified or unrecognized base defaults to it.
    private static ThemeVariant ParseBase(string? baseName) =>
        string.Equals(baseName?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

    // Color.Parse throws on a bad string; wrap it so one malformed token never fails the whole file.
    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = Color.Parse(hex.Trim());
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            color = default;
            return false;
        }
    }

    // The on-disk shape. Nullable everywhere so a partial file deserializes without throwing; validation
    // happens in TryParse. Token values are strings (hex) parsed to Color in code — never objects.
    private sealed class ThemeFileDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("base")]
        public string? Base { get; set; }

        [JsonPropertyName("tokens")]
        public Dictionary<string, string?>? Tokens { get; set; }
    }
}
