#region

using System.Collections;
using System.Globalization;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     Coercions for table cells and cached scalars. A cell arrives as whatever the row source used
///     (boxed numbers, strings, lists) and a cached <see cref="RoundFacts.Extra" /> value comes back
///     from JSON as a <see cref="JsonElement" />, so every reader goes through here rather than
///     pattern-matching on its own.
/// </summary>
internal static class RoundFactsValues
{
    public static int? ToInt(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case int i:
                return i;
            case long l:
                return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
            case short s:
                return s;
            case byte b:
                return b;
            case uint u:
                return u <= int.MaxValue ? (int)u : null;
            case double d:
                return double.IsFinite(d) ? (int)Math.Round(d) : null;
            case float f:
                return float.IsFinite(f) ? (int)MathF.Round(f) : null;
            case decimal m:
                return (int)Math.Round(m);
            case bool flag:
                return flag ? 1 : 0;
            case string text:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble)
                        ? (int)Math.Round(asDouble)
                        : null;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number => element.TryGetInt32(out int n) ? n
                        : element.TryGetDouble(out double dd) ? (int)Math.Round(dd) : null,
                    JsonValueKind.String => ToInt(element.GetString()),
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    _ => null
                };
            default:
                return null;
        }
    }

    public static bool? ToBool(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool flag:
                return flag;
            case string text:
                return bool.TryParse(text, out bool parsed) ? parsed
                    : ToInt(text) is int fromText ? fromText != 0 : null;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => ToInt(element) is int number && number != 0,
                    JsonValueKind.String => ToBool(element.GetString()),
                    _ => null
                };
            default:
                return ToInt(value) is int i ? i != 0 : null;
        }
    }

    /// <summary>A list cell as ints; null when the cell is absent or not list-shaped. Non-numeric items are dropped.</summary>
    public static int[]? ToIntList(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text.Length == 0
                    ? []
                    : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ToInt).OfType<int>().ToArray();
            case JsonElement element:
                return element.ValueKind == JsonValueKind.Array
                    ? element.EnumerateArray().Select(e => ToInt(e)).OfType<int>().ToArray()
                    : null;
            case IEnumerable items:
                return items.Cast<object?>().Select(ToInt).OfType<int>().ToArray();
            default:
                return null;
        }
    }

    /// <summary>The cell as label text; null for a null cell.</summary>
    public static string? ToText(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Array => string.Join(",", element.EnumerateArray().Select(e => ToText(e) ?? "")),
                    _ => element.GetRawText()
                };
            case IEnumerable items:
                return string.Join(",", items.Cast<object?>().Select(v => ToText(v) ?? ""));
            default:
                return value.ToString();
        }
    }

    /// <summary>
    ///     A cell as the scalar the cache stores: null, bool, long, double or string. Lists flatten to
    ///     comma-joined text so an <see cref="RoundFacts.Extra" /> value is always one filterable value.
    /// </summary>
    public static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool or string:
                return value;
            case int or long or short or byte or uint or ushort or sbyte:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case float or double or decimal:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                    _ => ToText(element)
                };
            default:
                return ToText(value);
        }
    }

    /// <summary>Whether two cells carry the same value, compared as text so a boxed int and a JSON number agree.</summary>
    public static bool SameValue(object? a, object? b) =>
        string.Equals(ToText(a), ToText(b), StringComparison.Ordinal);

    /// <summary>An enum name in the label vocabulary's spelling: <c>Pistol</c> to <c>pistol</c>, <c>MidRound</c> to <c>midRound</c>.</summary>
    public static string LowerCamel<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
