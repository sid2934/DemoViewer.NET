#region

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Overlay;

/// <summary>
///     Reads and writes an <see cref="OverlayDocument" /> as a <c>.dvoverlay.json</c> file: the overlay
///     fixture <c>dv2d</c> draws beside a scene, the way <c>queries/&lt;name&gt;.dvquery.json</c> is the
///     query it draws. Source-generated like every other pipeline format, so it works trimmed and on
///     WASM.
///     <para>
///         A fixture format, not a saved overlay. The App builds its overlay from the positions files
///         on every click and never writes one; this file exists so a golden can pin the layer over a
///         committed synthetic hit set with no demo and no index behind it.
///     </para>
///     <para>
///         The points are written one per line as <c>[x, y, z]</c> under <c>ct</c> and <c>t</c> rather
///         than through the indented serializer, which would put every number on its own line and turn
///         a thousand points into four thousand lines of diff.
///     </para>
/// </summary>
public static class OverlayFixtureStore
{
    /// <summary>The file suffix, including the JSON extension.</summary>
    public const string SidecarExtension = ".dvoverlay.json";

    /// <summary>The schema stamp a writer puts in the file.</summary>
    public const string SchemaVersion = "dvoverlay/1";

    /// <summary>Reads a document from a file.</summary>
    /// <param name="path">The <c>.dvoverlay.json</c> path.</param>
    /// <exception cref="JsonException">The payload is not an overlay fixture.</exception>
    public static OverlayDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Reads a document from a stream. Not closed.</summary>
    /// <param name="source">The stream.</param>
    /// <exception cref="JsonException">The payload is not an overlay fixture.</exception>
    public static OverlayDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        OverlayFixtureDto dto = JsonSerializer.Deserialize(source, OverlayFixtureJsonContext.Default.OverlayFixtureDto)
                                ?? throw new JsonException("Overlay fixture payload was null.");

        List<OverlayPoint> points = [];
        Append(points, dto.Ct, QuerySide.Ct);
        Append(points, dto.T, QuerySide.T);

        OverlayDocument document = new();
        if (points.Count > 0)
        {
            document.Replace(dto.Map ?? "", points, Math.Max(0, dto.States));
        }

        return document;
    }

    /// <summary>Writes a document to a file, creating the directory if needed. LF on every platform.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The <c>.dvoverlay.json</c> path.</param>
    public static void WriteFile(OverlayDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        Write(document, stream);
    }

    /// <summary>Writes a document to a stream. Not closed.</summary>
    /// <param name="document">The document.</param>
    /// <param name="destination">The stream.</param>
    public static void Write(OverlayDocument document, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);

        // An explicit LF, for the reason SceneFixtureSerializer gives: the corpus is committed text
        // pinned to LF, and Environment.NewLine would make a Windows write dirty every checkout.
        StringBuilder text = new();
        text.Append("{\n");
        text.Append("  \"schemaVersion\": ").Append(Quote(SchemaVersion)).Append(",\n");
        text.Append("  \"map\": ").Append(Quote(document.MapName)).Append(",\n");
        text.Append("  \"states\": ").Append(document.StateCount.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        AppendSide(text, "ct", document.Points, QuerySide.Ct);
        text.Append(",\n");
        AppendSide(text, "t", document.Points, QuerySide.T);
        text.Append("\n}\n");

        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        destination.Write(bytes, 0, bytes.Length);
    }

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private static void AppendSide(StringBuilder text, string name, IReadOnlyList<OverlayPoint> points, QuerySide side)
    {
        text.Append("  \"").Append(name).Append("\": [");
        bool first = true;
        for (int i = 0; i < points.Count; i++)
        {
            OverlayPoint point = points[i];
            if (point.Side != side)
            {
                continue;
            }

            text.Append(first ? "\n    [" : ",\n    [");
            first = false;
            text.Append(point.WorldX.ToString("R", CultureInfo.InvariantCulture)).Append(", ");
            text.Append(point.WorldY.ToString("R", CultureInfo.InvariantCulture)).Append(", ");
            text.Append(point.WorldZ.ToString("R", CultureInfo.InvariantCulture)).Append(']');
        }

        text.Append(first ? "]" : "\n  ]");
    }

    // Read as far as it parses: a tuple short of three numbers is a point this build cannot place,
    // and dropping the one point beats refusing the file.
    private static void Append(List<OverlayPoint> points, List<float[]>? tuples, QuerySide side)
    {
        foreach (float[] tuple in tuples ?? [])
        {
            if (tuple.Length < 3)
            {
                continue;
            }

            points.Add(new OverlayPoint(tuple[0], tuple[1], tuple[2], side));
        }
    }
}

internal sealed class OverlayFixtureDto
{
    public string? SchemaVersion { get; set; }
    public string? Map { get; set; }
    public int States { get; set; }
    public List<float[]>? Ct { get; set; }
    public List<float[]>? T { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OverlayFixtureDto))]
internal sealed partial class OverlayFixtureJsonContext : JsonSerializerContext;
