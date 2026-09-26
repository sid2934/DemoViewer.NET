#region

using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The round positions file: the <c>.dvrp.json.gz</c> sibling of a demo's <c>.dvri.json</c>. Per
///     live round, per sampled step, the alive players' world positions and place ids, the tuples the
///     index builder's walk already had in hand when it closed the row. A Result Card thumbnail is a
///     scene built from one step of this file, and Overlay View reads every step, so neither ever
///     opens a demo: measured, a seek from the demo costs 0.6 to 3.3 s and half a gigabyte of heap per
///     hit, and the render itself half a millisecond.
///     <para>
///         A sibling rather than a block inside the sidecar because the query service deserializes
///         every sidecar at startup and never looks at a position; positions would double that read
///         for nothing. Gzipped because the compact JSON is 170 to 210 KB per demo and 56 to 60 KB
///         zipped, about the size of the sidecar it sits beside. The file carries the same fingerprint
///         as its sidecar, so a reader with the current fingerprint ignores a stale one.
///     </para>
/// </summary>
public sealed class RoundPositionsDocument
{
    /// <summary>The file shape version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     What a tuple means: integer world units, alive slots only, the last sample per slot at the
    ///     sampled tick. In the index fingerprint as <c>pos=</c>, so a change re-indexes rather than
    ///     reinterprets, and a sidecar written before positions existed carries no <c>pos=</c> and is stale.
    ///     2: alive and <see cref="RoundPositionsRound.Ct" /> read from the sample's own <c>IsAlive</c>
    ///     and <c>Team</c> (CS2DemoKit #58) and an empty place is unplaced. Both files are rebuilt, since
    ///     the fingerprint is theirs together: a version 1 <c>.dvri.json</c> counted <c>""</c> as a place
    ///     in its summaries and transitions and gated its tokens on the Round Facts kill tick. The
    ///     sidecar's own <c>schemaVersion</c> stays 1, because its shape did not change and
    ///     <c>SituationIndex</c> holds it equal to <c>DemoCacheRecord.RoundIndexSchema</c>.
    /// </summary>
    public const int PositionSchema = 2;

    /// <summary>The serializer options: camel case and compact like the sidecar, tuples through their converter.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new RoundPositionConverter()
        }
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Identical to the <c>.dvri.json</c> this file belongs to.</summary>
    public string Fingerprint { get; set; } = "";

    public RoundPositionsDemo Demo { get; set; } = new();

    /// <summary>Ticks between sampled steps, the sidecar's value: <c>tick = freezeEndTick + step * cadenceTicks</c>.</summary>
    public int CadenceTicks { get; set; }

    /// <summary>The place table a tuple's place id indexes; <see cref="PlaceCountToken.NullPlace" /> stands for a null place.</summary>
    public List<string> Places { get; set; } = [];

    public List<RoundPositionsRound> Rounds { get; set; } = [];

    /// <summary>The round with this number, or null.</summary>
    /// <param name="number">The Round Facts round number.</param>
    public RoundPositionsRound? Round(int number) => Rounds.FirstOrDefault(r => r.Number == number);

    /// <summary>The step a frame-clock tick maps to within a round: the inverse of the sidecar's tick rule.</summary>
    /// <param name="round">The round.</param>
    /// <param name="tick">Frame clock.</param>
    public int StepFor(RoundPositionsRound round, int tick)
    {
        ArgumentNullException.ThrowIfNull(round);
        return CadenceTicks <= 0 ? 0 : (tick - round.FreezeEndTick) / CadenceTicks;
    }

    /// <summary>The place a tuple names, or null for the reserved null place.</summary>
    /// <param name="placeId">The tuple's place id.</param>
    public string? PlaceOf(int placeId)
    {
        if (placeId < 0 || placeId >= Places.Count)
        {
            return null;
        }

        string place = Places[placeId];
        return string.Equals(place, PlaceCountToken.NullPlace, StringComparison.Ordinal) ? null : place;
    }

    /// <summary>The compact JSON text.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The gzipped compact JSON: what the store writes.</summary>
    public byte[] SerializeGzip()
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(Serialize()));
        }

        return buffer.ToArray();
    }

    /// <summary>The document a file's text describes, or null when it does not parse.</summary>
    /// <param name="json">The uncompressed text.</param>
    public static RoundPositionsDocument? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RoundPositionsDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The document a gzipped file holds, or null when it does not inflate or parse.</summary>
    /// <param name="gzipped">The file bytes.</param>
    public static RoundPositionsDocument? TryDeserializeGzip(byte[] gzipped)
    {
        try
        {
            using MemoryStream source = new(gzipped);
            using GZipStream gzip = new(source, CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            return TryDeserialize(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>The join keys, so a reader with a hash can refuse a file that belongs to another demo.</summary>
public sealed class RoundPositionsDemo
{
    public string StableKey { get; set; } = "";

    public string? Sha256 { get; set; }
}

/// <summary>One live round: which slots sat CT, and the alive tuples per sampled step.</summary>
public sealed class RoundPositionsRound
{
    /// <summary>== <c>RoundFacts.Number</c>.</summary>
    public int Number { get; set; }

    /// <summary>Frame clock. Step 0 is sampled here.</summary>
    public int FreezeEndTick { get; set; }

    /// <summary>Slots whose sample was CT this round (CS2DemoKit #58 <c>Team</c>); every other tuple is T.</summary>
    public int[] Ct { get; set; } = [];

    /// <summary>
    ///     Indexed by step. A step the walk did not sample is an empty list, so the list is as long as
    ///     the round's last sampled step plus one and a tuple's tick is always its index times the cadence.
    /// </summary>
    public List<List<RoundPosition>> Pos { get; set; } = [];

    /// <summary>The tuples at a step, or none when the step is out of range or was not sampled.</summary>
    /// <param name="step">The step.</param>
    public IReadOnlyList<RoundPosition> At(int step) =>
        step >= 0 && step < Pos.Count ? Pos[step] : [];
}

/// <summary>One alive player at one sampled step, stored as <c>[slot, x, y, z, placeId]</c> in integer world units.</summary>
public readonly record struct RoundPosition(int Slot, int X, int Y, int Z, int PlaceId);

/// <summary><c>[slot, x, y, z, placeId]</c>.</summary>
internal sealed class RoundPositionConverter : JsonConverter<RoundPosition>
{
    public override RoundPosition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.StartArray);
        reader.Read();
        int slot = reader.GetInt32();
        reader.Read();
        int x = reader.GetInt32();
        reader.Read();
        int y = reader.GetInt32();
        reader.Read();
        int z = reader.GetInt32();
        reader.Read();
        int place = reader.GetInt32();
        reader.Read();
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.EndArray);
        return new RoundPosition(slot, x, y, z, place);
    }

    public override void Write(Utf8JsonWriter writer, RoundPosition value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Slot);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.PlaceId);
        writer.WriteEndArray();
    }
}

/// <summary>The one build that yields both files, so they can never disagree on a row.</summary>
/// <param name="Index">The <c>.dvri.json</c> document.</param>
/// <param name="Positions">The <c>.dvrp.json.gz</c> document, carrying the same fingerprint.</param>
/// <param name="Disagreements">
///     Per round, how often a sample's own <c>IsAlive</c>/<c>Team</c> disagreed with Round Facts'
///     <c>Slots</c>/<c>Kills</c> (CS2DemoKit #58 cross-check). The sample always won; this is
///     diagnostic only. Empty on every demo measured so far.
/// </param>
public sealed record RoundIndexBuild(
    RoundIndexDocument Index,
    RoundPositionsDocument Positions,
    IReadOnlyList<RoundIndexDisagreement> Disagreements)
{
    /// <summary>World units rounded to the integer a tuple stores; a marker at thumbnail size is thirty units wide.</summary>
    /// <param name="value">A world coordinate.</param>
    public static int Quantize(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>One round's disagreement count between a sample's own fields and Round Facts, see <see cref="RoundIndexBuild.Disagreements" />.</summary>
/// <param name="Round">The round number.</param>
/// <param name="SideMismatches">Slots where the sample's <c>Team</c> disagreed with the row's seated side.</param>
/// <param name="AliveMismatches">Slots the row's <c>Kills</c> call dead where the sample's <c>IsAlive</c> is true.</param>
public sealed record RoundIndexDisagreement(int Round, int SideMismatches, int AliveMismatches);

/// <summary>
///     Counts, per round, how often a walk sample's own <c>IsAlive</c>/<c>Team</c> disagreed with the
///     Round Facts row it was cross-checked against (CS2DemoKit #58). Shared by
///     <c>RoundIndexBuilder</c> and <c>RoundOccupancyBuilder</c>, the two walks that still hold the
///     facts-derived roster beside the sample. Not a gate: the sample always wins.
/// </summary>
public sealed class RoundIndexDisagreementTally
{
    private readonly Dictionary<int, (int Side, int Alive)> _byRound = [];

    /// <summary>Adds one slot's disagreement, if any, to its round's counts.</summary>
    /// <param name="round">The round number.</param>
    /// <param name="sideMismatch">The sample's <c>Team</c> disagreed with the row's seated side.</param>
    /// <param name="aliveMismatch">The row's <c>Kills</c> call the slot dead where the sample is alive.</param>
    public void Record(int round, bool sideMismatch, bool aliveMismatch)
    {
        if (!sideMismatch && !aliveMismatch)
        {
            return;
        }

        (int Side, int Alive) counts = _byRound.GetValueOrDefault(round);
        _byRound[round] = (counts.Side + (sideMismatch ? 1 : 0), counts.Alive + (aliveMismatch ? 1 : 0));
    }

    /// <summary>The tallied rounds, in round order; empty when nothing disagreed.</summary>
    public IReadOnlyList<RoundIndexDisagreement> ToDisagreements() =>
        [.. _byRound.OrderBy(kv => kv.Key).Select(kv => new RoundIndexDisagreement(kv.Key, kv.Value.Side, kv.Value.Alive))];
}
