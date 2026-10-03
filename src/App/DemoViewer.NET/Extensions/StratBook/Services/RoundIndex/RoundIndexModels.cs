#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>The outcome of the last index attempt for a demo: the <see cref="DemoCache.DemoAnalysisState" /> shape.</summary>
public enum RoundIndexState
{
    /// <summary>Never run, or invalidated by a fingerprint change.</summary>
    Pending,

    /// <summary>Ran successfully under <c>DemoCacheRecord.RoundIndexFingerprint</c>.</summary>
    Indexed,

    /// <summary>Ran and threw. Excluded from the backlog until the user retries it.</summary>
    Failed
}

/// <summary>Which string a row's place comes from. A setting; changing it re-indexes the library.</summary>
public enum RoundIndexTokenSource
{
    /// <summary>The pawn's <c>m_szLastPlaceName</c>: Valve's names, no asset needed.</summary>
    Pawn,

    /// <summary>The effective zone set's resolver over the sample position: the team's names, per map.</summary>
    Zones
}

/// <summary>
///     The parameters that decide what a row means. Every one of them is in the fingerprint, so a
///     change re-indexes rather than reinterprets. Neither is a user setting.
/// </summary>
public sealed record RoundIndexOptions
{
    /// <summary>The shipped parameters.</summary>
    public static RoundIndexOptions Default { get; } = new();

    /// <summary>
    ///     Seconds between sampled ticks. One second is the finest cadence that costs nothing visible
    ///     and the coarsest that loses nothing the consumers ask for; stated in seconds so a 128-tick
    ///     demo samples at 128 ticks and rows mean the same thing across sources.
    /// </summary>
    public double CadenceSeconds { get; init; } = 1;

    /// <summary>
    ///     The <c>PositionSampler.Walk</c> frame stride. Four keeps every one-second row on the measured
    ///     demos and costs the decode floor; eight dropped rows. Not in the fingerprint: it changes
    ///     which frame a row is read from, never what the row means.
    /// </summary>
    public int FrameStride { get; init; } = 4;

    /// <summary>The cadence in ticks at a demo's rate.</summary>
    /// <param name="tickRate">The demo's tick rate; 64 when unknown.</param>
    public int CadenceTicks(int tickRate) =>
        Math.Max(1, (int)Math.Round(CadenceSeconds * (tickRate > 0 ? tickRate : 64)));
}

/// <summary>
///     One demo's index: the <c>.dvri.json</c> sidecar (schema 1). Runs per live round, the alive
///     sample sums per place and Z bucket, and the one-second place transitions. Serialized compact and
///     camel-cased through <see cref="JsonOptions" />; the shape is pinned by a committed fixture.
/// </summary>
public sealed class RoundIndexDocument
{
    /// <summary>The sidecar shape version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     The serializer options every reader and writer of the sidecar uses: camel case like the
    ///     annotation sidecar, compact because nobody reads it by hand and it is two to three times
    ///     smaller that way, nulls written so <c>demo.sha256</c> is visibly null until Content Identity fills it.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new RoundIndexRunConverter(),
            new PlaceZBucketSumConverter(),
            new PlaceTransitionConverter()
        }
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The <see cref="RoundIndexFingerprint" /> the rows were built under; a mismatch means rebuild, never reinterpret.</summary>
    public string Fingerprint { get; set; } = "";

    public RoundIndexDemo Demo { get; set; } = new();

    /// <summary>The <c>dv-frame-clock</c> header, the annotation sidecar's block verbatim.</summary>
    public RoundFactsClock Clock { get; set; } = new();

    public string Map { get; set; } = "";

    /// <summary>Ticks between sampled steps: <c>tick = freezeEndTick + step * cadenceTicks</c>.</summary>
    public int CadenceTicks { get; set; }

    public List<RoundIndexRound> Rounds { get; set; } = [];

    /// <summary>Alive samples with a non-null place, summed per place and per 64-unit Z bucket, for the Query Canvas snap.</summary>
    public Dictionary<string, PlaceSampleSummary> Places { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Unordered place pairs with the count of one-second place changes by one alive player.</summary>
    public List<PlaceTransition> Transitions { get; set; } = [];

    /// <summary>One row per sampled step, from the runs. For consumers that want per-second occupancy.</summary>
    /// <param name="round">A round of this document.</param>
    public IEnumerable<RoundIndexRow> ExpandRows(RoundIndexRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        foreach (RoundIndexRun run in round.Runs)
        {
            for (int step = run.FromStep; step <= run.ToStep; step++)
            {
                yield return new RoundIndexRow(step, round.FreezeEndTick + step * CadenceTicks, run.Ct, run.T);
            }
        }
    }

    /// <summary>Rows across every round. Derived, so not written.</summary>
    [JsonIgnore]
    public int RowCount => Rounds.Sum(r => r.Runs.Sum(run => run.ToStep - run.FromStep + 1));

    /// <summary>The compact JSON text of this document.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The document a sidecar's text describes, or null when it does not parse.</summary>
    /// <param name="json">The sidecar text.</param>
    public static RoundIndexDocument? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RoundIndexDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The demo block: the join key and what a human needs to recognise the file.</summary>
public sealed class RoundIndexDemo
{
    /// <summary>Null until Content Identity has hashed the demo; a reader with a hash ignores a mismatching file.</summary>
    public string? Sha256 { get; set; }

    /// <summary><c>DemoCacheStore.StableKey</c> of the path: the join key until the hash exists, the file name afterwards.</summary>
    public string StableKey { get; set; } = "";

    public string FileName { get; set; } = "";

    public long SizeBytes { get; set; }
}

/// <summary>One live round's sampled steps, tiled by runs.</summary>
public sealed class RoundIndexRound
{
    /// <summary>== <c>RoundFacts.Number</c>: the foreign key.</summary>
    public int Number { get; set; }

    /// <summary>Frame clock. Step 0 is sampled here.</summary>
    public int FreezeEndTick { get; set; }

    /// <summary>Frame clock. The first tick not sampled.</summary>
    public int EndTick { get; set; }

    /// <summary>
    ///     Runs tile the steps: run <i>i</i> ends at its <c>ToStep</c> and run <i>i+1</i> starts at
    ///     <c>ToStep + 1</c>. A run is one (CT, T) token pair held over consecutive samples.
    /// </summary>
    public List<RoundIndexRun> Runs { get; set; } = [];
}

/// <summary>A (CT, T) pair held from <see cref="FromStep" /> to <see cref="ToStep" /> inclusive. Stored as <c>[from, to, ct, t]</c>.</summary>
public sealed record RoundIndexRun(int FromStep, int ToStep, string Ct, string T);

/// <summary>One expanded row: a sampled step with its tick and both tokens.</summary>
public sealed record RoundIndexRow(int Step, int Tick, string Ct, string T);

/// <summary>Alive samples in one place: the total and the per-bucket sums the centroid is folded from.</summary>
public sealed class PlaceSampleSummary
{
    [JsonPropertyName("n")]
    public int Count { get; set; }

    /// <summary>Per <c>MapSpace.QuantizeZ</c> bucket of the sample Z (never a level key): <c>[bucket, n, sumX, sumY]</c>.</summary>
    [JsonPropertyName("z")]
    public List<PlaceZBucketSum> Buckets { get; set; } = [];
}

/// <summary>The partial sums for one place on one Z bucket; per-map folding is plain addition.</summary>
public sealed record PlaceZBucketSum(int ZBucket, int Count, double SumX, double SumY);

/// <summary>An unordered place pair and how often an alive player crossed it between consecutive rows.</summary>
public sealed record PlaceTransition(string A, string B, int Count);

/// <summary><c>[fromStep, toStep, ct, t]</c>.</summary>
internal sealed class RoundIndexRunConverter : JsonConverter<RoundIndexRun>
{
    public override RoundIndexRun Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        reader.Read();
        int from = reader.GetInt32();
        reader.Read();
        int to = reader.GetInt32();
        reader.Read();
        string ct = reader.GetString() ?? "";
        reader.Read();
        string t = reader.GetString() ?? "";
        reader.Read();
        Expect(ref reader, JsonTokenType.EndArray);
        return new RoundIndexRun(from, to, ct, t);
    }

    public override void Write(Utf8JsonWriter writer, RoundIndexRun value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.FromStep);
        writer.WriteNumberValue(value.ToStep);
        writer.WriteStringValue(value.Ct);
        writer.WriteStringValue(value.T);
        writer.WriteEndArray();
    }

    internal static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (reader.TokenType != token)
        {
            throw new JsonException($"expected {token}, found {reader.TokenType}");
        }
    }
}

/// <summary><c>[bucket, n, sumX, sumY]</c>.</summary>
internal sealed class PlaceZBucketSumConverter : JsonConverter<PlaceZBucketSum>
{
    public override PlaceZBucketSum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.StartArray);
        reader.Read();
        int bucket = reader.GetInt32();
        reader.Read();
        int count = reader.GetInt32();
        reader.Read();
        double sumX = reader.GetDouble();
        reader.Read();
        double sumY = reader.GetDouble();
        reader.Read();
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.EndArray);
        return new PlaceZBucketSum(bucket, count, sumX, sumY);
    }

    public override void Write(Utf8JsonWriter writer, PlaceZBucketSum value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.ZBucket);
        writer.WriteNumberValue(value.Count);
        writer.WriteNumberValue(value.SumX);
        writer.WriteNumberValue(value.SumY);
        writer.WriteEndArray();
    }
}

/// <summary><c>[a, b, count]</c>.</summary>
internal sealed class PlaceTransitionConverter : JsonConverter<PlaceTransition>
{
    public override PlaceTransition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.StartArray);
        reader.Read();
        string a = reader.GetString() ?? "";
        reader.Read();
        string b = reader.GetString() ?? "";
        reader.Read();
        int count = reader.GetInt32();
        reader.Read();
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.EndArray);
        return new PlaceTransition(a, b, count);
    }

    public override void Write(Utf8JsonWriter writer, PlaceTransition value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.A);
        writer.WriteStringValue(value.B);
        writer.WriteNumberValue(value.Count);
        writer.WriteEndArray();
    }
}
