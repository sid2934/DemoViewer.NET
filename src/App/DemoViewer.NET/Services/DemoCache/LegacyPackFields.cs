#region

using System.Buffers;
using System.Text.Json;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     The Strat Book fields the record and the index row carried flat before <see cref="DemoCacheRecord.Packs" />
///     and <see cref="PackStamp" /> existed. A file written in that shape is folded on read, once per read,
///     into the pack's payload and its stamps; the next save writes the new shape, so nothing is rewritten
///     until something changes and no library-wide pass is needed. Every stamp is derived from the old
///     fields so the evaluators' backlogs come out exactly as they were.
///     <para>
///         The ids and member names are literals on purpose: they are the pack's as it wrote them, and core
///         cannot name the pack's types. Only <see cref="LiftRoundFacts" /> acts on a file in the payload shape.
///     </para>
/// </summary>
internal static class LegacyPackFields
{
    /// <summary>The pack every old record's fields belonged to.</summary>
    public const string PackId = "net.demoviewer.pack.stratbook";

    /// <summary>The stamp ids, which are the evaluators' ids.</summary>
    public const string RoundFactsId = "roundfacts";

    public const string RoundIndexId = "roundindex";
    public const string SuggestionsId = "suggestedtags";
    public const string GrenadesId = "grenades";

    // Payload member names, as the pack's payload type spells them.
    private const string RoundFactsMember = "RoundFacts";
    private const string GrenadeInputCoverageMember = "GrenadeInputCoverage";

    /// <summary>Folds the flat fields of an old record into <paramref name="record" />'s payload and stamps.</summary>
    /// <param name="record">The record being read.</param>
    /// <param name="members">The members no property claimed.</param>
    public static void Fold(DemoCacheRecord record, Dictionary<string, JsonElement> members)
    {
        // The flat rows bind to DemoCacheRecord.RoundFacts directly, so their schema is read from there.
        double coverage = Number(members, "GrenadeInputCoverage");
        if (coverage != 0 && !record.Packs.ContainsKey(PackId))
        {
            record.Packs[PackId] = Payload(coverage);
        }

        int roundFactsSchema = record.RoundFacts?.Schema ?? 0;
        (int riSchema, long riTicks) = TierStamp(members, "RoundIndex");
        (int gSchema, long gTicks) = TierStamp(members, "Grenades");

        Stamps(record.PackStamps,
            roundFactsSchema, String(members, "RoundFactsFingerprint"),
            riSchema, riTicks, State(members, "RoundIndexState"), String(members, "RoundIndexFingerprint"), Int(members, "RoundIndexRowCount"),
            String(members, "SuggestionsFingerprint"), Int(members, "SuggestionCount"),
            gSchema, gTicks, State(members, "GrenadeState"), String(members, "GrenadeWalker"), Int(members, "GrenadeCount"));
    }

    /// <summary>
    ///     Moves Round Facts rows an older build kept in the Strat Book's payload onto
    ///     <see cref="DemoCacheRecord.RoundFacts" />, and drops only that member from the payload: whatever else
    ///     rides it (the grenade input coverage) stays. Rows already on the record win. The stamp is not
    ///     touched, so the moved rows stay current. The next save writes the new shape.
    /// </summary>
    /// <param name="record">The record being read.</param>
    public static void LiftRoundFacts(DemoCacheRecord record)
    {
        if (!record.Packs.TryGetValue(PackId, out JsonElement payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(RoundFactsMember, out JsonElement rows))
        {
            return;
        }

        if (record.RoundFacts is null && rows.ValueKind == JsonValueKind.Object)
        {
            try
            {
                record.RoundFacts = rows.Deserialize<RoundFactsRows>();
            }
            catch (JsonException)
            {
                // Rows that do not read are "not written", the rule every sidecar reader follows.
            }
        }

        ArrayBufferWriter<byte> buffer = new();
        int kept = 0;
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty member in payload.EnumerateObject())
            {
                if (!string.Equals(member.Name, RoundFactsMember, StringComparison.Ordinal))
                {
                    member.WriteTo(writer);
                    kept++;
                }
            }

            writer.WriteEndObject();
        }

        if (kept == 0)
        {
            record.Packs.Remove(PackId);
            return;
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        record.Packs[PackId] = document.RootElement.Clone();
    }

    /// <summary>Folds the flat fields of an old index row into <paramref name="entry" />'s stamps.</summary>
    /// <param name="entry">The row being read.</param>
    /// <param name="members">The members no property claimed.</param>
    public static void Fold(DemoCacheIndexEntry entry, Dictionary<string, JsonElement> members) =>
        Stamps(entry.PackStamps,
            Int(members, "RoundFactsSchema"), String(members, "RoundFactsFingerprint"),
            Int(members, "RoundIndexSchema"), Long(members, "RoundIndexComputedAtTicks"), State(members, "RoundIndexState"),
            String(members, "RoundIndexFingerprint"), Int(members, "RoundIndexRowCount"),
            String(members, "SuggestionsFingerprint"), Int(members, "SuggestionCount"),
            Int(members, "GrenadeSchema"), 0, State(members, "GrenadeState"),
            String(members, "GrenadeWalker"), Int(members, "GrenadeCount"));

    // A facet that was never touched (every field at its default) gets no stamp, so a record indexed
    // while the pack was off stays "missing", not "pending at schema 0". A stamp in the list already
    // (a new-shape file with stale flat members beside it) wins.
    private static void Stamps(List<PackStamp> stamps,
        int rfSchema, string? rfFingerprint,
        int riSchema, long riTicks, DemoAnalysisState riState, string? riFingerprint, int riCount,
        string? sgFingerprint, int sgCount,
        int gSchema, long gTicks, DemoAnalysisState gState, string? gWalker, int gCount)
    {
        if (rfSchema != 0 || rfFingerprint is not null)
        {
            Add(stamps, new PackStamp(RoundFactsId, rfSchema, rfFingerprint)
            {
                State = rfSchema > 0 ? DemoAnalysisState.Indexed : DemoAnalysisState.Pending
            });
        }

        if (riSchema != 0 || riState != DemoAnalysisState.Pending || riFingerprint is not null || riCount != 0)
        {
            Add(stamps, new PackStamp(RoundIndexId, riSchema, riFingerprint)
            {
                State = riState,
                ComputedAtTicks = riTicks,
                Count = riCount
            });
        }

        if (sgFingerprint is not null || sgCount != 0)
        {
            Add(stamps, new PackStamp(SuggestionsId, sgFingerprint is null ? 0 : 1, sgFingerprint)
            {
                State = sgFingerprint is null ? DemoAnalysisState.Pending : DemoAnalysisState.Indexed,
                Count = sgCount
            });
        }

        if (gSchema != 0 || gState != DemoAnalysisState.Pending || gWalker is not null || gCount != 0)
        {
            Add(stamps, new PackStamp(GrenadesId, gSchema, gWalker)
            {
                State = gState,
                ComputedAtTicks = gTicks,
                Count = gCount
            });
        }
    }

    private static void Add(List<PackStamp> stamps, PackStamp stamp)
    {
        if (!stamps.Exists(s => string.Equals(s.Id, stamp.Id, StringComparison.Ordinal)))
        {
            stamps.Add(stamp);
        }
    }

    private static JsonElement Payload(double coverage)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(GrenadeInputCoverageMember, coverage);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static (int Schema, long ComputedAtTicks) TierStamp(Dictionary<string, JsonElement> members, string name)
    {
        if (Object(members, name) is not { } stamp)
        {
            return (0, 0);
        }

        int schema = stamp.TryGetProperty("Schema", out JsonElement s) && s.TryGetInt32(out int value) ? value : 0;
        long ticks = stamp.TryGetProperty("ComputedAtTicks", out JsonElement t) && t.TryGetInt64(out long at) ? at : 0;
        return (schema, ticks);
    }

    private static JsonElement? Object(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Object ? element : null;

    private static string? String(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static int Int(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value
            : 0;

    private static long Long(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value)
            ? value
            : 0;

    private static double Number(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value)
            ? value
            : 0;

    // The old enums were written as numbers with the same ordinals as DemoAnalysisState; a name reads too.
    private static DemoAnalysisState State(Dictionary<string, JsonElement> members, string name)
    {
        if (!members.TryGetValue(name, out JsonElement element))
        {
            return DemoAnalysisState.Pending;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int ordinal)
            && Enum.IsDefined(typeof(DemoAnalysisState), ordinal))
        {
            return (DemoAnalysisState)ordinal;
        }

        return element.ValueKind == JsonValueKind.String
               && Enum.TryParse(element.GetString(), true, out DemoAnalysisState parsed)
            ? parsed
            : DemoAnalysisState.Pending;
    }
}
