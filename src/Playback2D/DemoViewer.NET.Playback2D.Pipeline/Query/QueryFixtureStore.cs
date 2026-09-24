#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Core.Query;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Query;

/// <summary>
///     Reads and writes a <see cref="QueryCanvasDocument" /> as a <c>.dvquery.json</c> file: the query
///     fixture <c>dv2d</c> draws beside a scene, the way <c>annotations/&lt;name&gt;.dvann.json</c> is
///     the ink it burns in. Source-generated like every other pipeline format, so it works trimmed and
///     on WASM.
///     <para>
///         A fixture format, not a saved query. What a saved query has to carry (the zones version the
///         places were resolved under, the frame-clock header) is Watched Situations' concern; this file
///         holds exactly what the layer needs to draw and nothing a later schema would have to migrate
///         away from.
///     </para>
/// </summary>
public static class QueryFixtureStore
{
    /// <summary>The file suffix, including the JSON extension.</summary>
    public const string SidecarExtension = ".dvquery.json";

    /// <summary>The schema stamp a writer puts in the file.</summary>
    public const string SchemaVersion = "dvquery/1";

    /// <summary>Reads a document from a file.</summary>
    /// <param name="path">The <c>.dvquery.json</c> path.</param>
    /// <exception cref="JsonException">The payload is not a query fixture.</exception>
    public static QueryCanvasDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Reads a document from a stream. Not closed.</summary>
    /// <param name="source">The stream.</param>
    /// <exception cref="JsonException">The payload is not a query fixture.</exception>
    public static QueryCanvasDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        QueryFixtureDto dto = JsonSerializer.Deserialize(source, QueryFixtureJsonContext.Default.QueryFixtureDto)
                              ?? throw new JsonException("Query fixture payload was null.");

        QueryCanvasDocument document = new()
        {
            MapName = dto.Map ?? ""
        };

        foreach (QueryTokenDto token in dto.Tokens ?? [])
        {
            // Read as far as it parses: a slot out of range or an unknown side is a fixture this build
            // cannot place, and dropping the one token beats refusing the file.
            if (!Enum.TryParse(token.Side, true, out QuerySide side)
                || token.Slot < 0 || token.Slot >= QueryCanvasDocument.SlotsPerSide)
            {
                continue;
            }

            document.Place(new QueryToken(side, token.Slot, token.WorldX, token.WorldY, token.LevelMinZ,
                token.Place));
        }

        return document;
    }

    /// <summary>Writes a document to a file, creating the directory if needed. Indented, LF on every platform.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The <c>.dvquery.json</c> path.</param>
    public static void WriteFile(QueryCanvasDocument document, string path)
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
    public static void Write(QueryCanvasDocument document, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);

        QueryFixtureDto dto = new()
        {
            SchemaVersion = SchemaVersion,
            Map = document.MapName,
            Tokens =
            [
                .. document.Placed.Select(t => new QueryTokenDto
                {
                    Side = t.Side == QuerySide.Ct ? "ct" : "t",
                    Slot = t.Slot,
                    WorldX = t.WorldX,
                    WorldY = t.WorldY,
                    LevelMinZ = t.LevelMinZ,
                    Place = t.Place
                })
            ]
        };

        // An explicit LF, for the reason SceneFixtureSerializer gives: the corpus is committed text
        // pinned to LF, and Environment.NewLine would make a Windows write dirty every checkout.
        using Utf8JsonWriter writer = new(destination, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n"
        });
        JsonSerializer.Serialize(writer, dto, QueryFixtureJsonContext.Default.QueryFixtureDto);
    }
}

internal sealed class QueryFixtureDto
{
    public string? SchemaVersion { get; set; }
    public string? Map { get; set; }
    public List<QueryTokenDto>? Tokens { get; set; }
}

internal sealed class QueryTokenDto
{
    public string? Side { get; set; }
    public int Slot { get; set; }
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public double LevelMinZ { get; set; }
    public string? Place { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(QueryFixtureDto))]
internal sealed partial class QueryFixtureJsonContext : JsonSerializerContext;
