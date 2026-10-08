#region

using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using CS2DemoKit.Analysis.Output;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     A <see cref="FactTable" /> as the gzipped JSON sidecar it is stored in, and the engine's table as one. A
///     number with a fraction is written with a decimal point or an exponent and a whole number without, so the
///     two kinds read back as they were written.
/// </summary>
internal static class FactsCodec
{
    private const int Format = 1;

    /// <summary>The engine's table as the SDK's, every cell normalized.</summary>
    public static FactTable FromMetric(MetricTable table, FactKey key, string fingerprint, int schema, string grain) =>
        new(key, fingerprint, schema, grain,
            [.. table.DimensionColumns],
            [.. table.ValueColumns],
            new Dictionary<string, string>(table.ColumnClocks, StringComparer.Ordinal),
            [
                .. table.Rows.Select(r => new FactRow(
                    r.Dimensions.ToDictionary(c => c.Key, c => FromEngine(c.Value), StringComparer.Ordinal),
                    r.Values.ToDictionary(c => c.Key, c => FromEngine(c.Value), StringComparer.Ordinal)))
            ]);

    /// <summary>An engine cell: null, a boolean, a whole number, a number, text, or a list of those.</summary>
    public static FactValue FromEngine(object? value) => value switch
    {
        null => FactValue.Null,
        FactValue fact => fact,
        bool flag => FactValue.Of(flag),
        string text => FactValue.Of(text),
        sbyte or byte or short or ushort or int or uint or long => FactValue.Of(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong big => big <= long.MaxValue ? FactValue.Of((long)big) : FactValue.Of((double)big),
        float or double or decimal => FactValue.Of(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        JsonElement element => FromJson(element),
        IEnumerable items => FactValue.Of(items.Cast<object?>().Select(FromEngine)),
        _ => FactValue.Of(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    public static byte[] Encode(FactTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, true))
        using (Utf8JsonWriter json = new(gzip))
        {
            json.WriteStartObject();
            json.WriteNumber("format", Format);
            json.WriteString("ruleset", table.Key.RulesetId);
            json.WriteString("output", table.Key.Output);
            json.WriteString("fingerprint", table.Fingerprint);
            json.WriteNumber("schema", table.Schema);
            json.WriteString("grain", table.Grain);
            WriteStrings(json, "dimensions", table.Dimensions);
            WriteStrings(json, "values", table.Values);
            json.WriteStartObject("clocks");
            foreach ((string column, string clock) in table.ColumnClocks)
            {
                json.WriteString(column, clock);
            }

            json.WriteEndObject();
            json.WriteStartArray("rows");
            foreach (FactRow row in table.Rows)
            {
                json.WriteStartObject();
                WriteCells(json, "d", row.Dimensions);
                WriteCells(json, "v", row.Values);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The table in <paramref name="bytes" />, or null when they are not one this format reads.</summary>
    public static FactTable? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using MemoryStream input = new(bytes);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using JsonDocument document = JsonDocument.Parse(gzip);
            JsonElement root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != Format)
            {
                return null;
            }

            return new FactTable(
                new FactKey(root.GetProperty("ruleset").GetString()!, root.GetProperty("output").GetString()!),
                root.GetProperty("fingerprint").GetString()!,
                root.GetProperty("schema").GetInt32(),
                root.GetProperty("grain").GetString() ?? "",
                [.. root.GetProperty("dimensions").EnumerateArray().Select(e => e.GetString()!)],
                [.. root.GetProperty("values").EnumerateArray().Select(e => e.GetString()!)],
                root.GetProperty("clocks").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
                [
                    .. root.GetProperty("rows").EnumerateArray().Select(r => new FactRow(
                        ReadCells(r.GetProperty("d")),
                        ReadCells(r.GetProperty("v"))))
                ]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException
                                       or FormatException or IOException)
        {
            return null;
        }
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static void WriteCells(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, FactValue> cells)
    {
        json.WriteStartObject(name);
        foreach ((string column, FactValue value) in cells)
        {
            json.WritePropertyName(column);
            WriteValue(json, value);
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, FactValue value)
    {
        switch (value.Kind)
        {
            case FactValueKind.Whole:
                json.WriteNumberValue(value.AsInteger()!.Value);
                break;
            case FactValueKind.Number:
                double number = value.AsNumber()!.Value;
                if (!double.IsFinite(number))
                {
                    // JSON has no NaN or infinity; the text form keeps the value readable.
                    json.WriteStringValue(number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                }

                string text = number.ToString("R", CultureInfo.InvariantCulture);
                json.WriteRawValue(text.Contains('.') || text.Contains('E') ? text : text + ".0");
                break;
            case FactValueKind.Boolean:
                json.WriteBooleanValue(value.AsBoolean()!.Value);
                break;
            case FactValueKind.Text:
                json.WriteStringValue(value.AsText());
                break;
            case FactValueKind.List:
                json.WriteStartArray();
                foreach (FactValue item in value.Items)
                {
                    WriteValue(json, item);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteNullValue();
                break;
        }
    }

    private static Dictionary<string, FactValue> ReadCells(JsonElement cells) =>
        cells.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal);

    private static FactValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => FactValue.Of(true),
        JsonValueKind.False => FactValue.Of(false),
        JsonValueKind.String => FactValue.Of(element.GetString()),
        JsonValueKind.Number => IsWhole(element.GetRawText()) && element.TryGetInt64(out long whole)
            ? FactValue.Of(whole)
            : FactValue.Of(element.GetDouble()),
        JsonValueKind.Array => FactValue.Of(element.EnumerateArray().Select(FromJson)),
        _ => FactValue.Null
    };

    private static bool IsWhole(string raw) => raw.IndexOfAny(['.', 'e', 'E']) < 0;
}
