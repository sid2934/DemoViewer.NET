#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Strats;

// The on-disk shapes of the Strat Book (strat-model.md §3.2 to §3.8): a strat, its history line, the
// owner-level book, the per-map callouts table and the index. Mutable classes serialized directly, the
// TagDocument idiom, so the session and the patch applier share one shape with the file.
//
// Every object carries a [JsonExtensionData] bag: a field written by a newer build survives load, edit and
// save by this one. Property order is declaration order, which is what keeps the schema golden byte-stable.
// Vocabulary fields are strings rather than enums for the same reason: an unknown value is a validator
// warning or refusal, never a load failure.

/// <summary>The closed and reserved vocabularies of schema v1, spelled as the file spells them.</summary>
public static class StratVocabulary
{
    public const string SideT = "T";
    public const string SideCt = "CT";

    /// <summary>A step actor meaning every slot.</summary>
    public const string ActorAll = "all";

    /// <summary>The five strat slots, in the only order <c>slots[]</c> may take.</summary>
    public static readonly IReadOnlyList<string> Slots = ["A", "B", "C", "D", "E"];

    /// <summary>Opponent token slots, admitted in <c>positions[].slot</c> only (overview correction 17).</summary>
    public static readonly IReadOnlyList<string> OpponentSlots = ["O1", "O2", "O3", "O4", "O5"];

    /// <summary>The pro tactics-directory types (§3.3.1). Outside this list is a warning.</summary>
    public static readonly IReadOnlyList<string> Types =
        ["execute", "rush", "explode", "split", "wrap", "fake", "default", "setup", "retake", "anti-eco", "save"];

    /// <summary>The closed verb list (§3.3.2). Outside it is a refusal, because Role View phrases by verb.</summary>
    public static readonly IReadOnlyList<string> Verbs =
        ["move", "hold", "throw", "plant", "defuse", "peek", "fake", "rotate", "wait", "call", "other"];

    /// <summary>Grenade kinds a step's utility may name (§3.3.3).</summary>
    public static readonly IReadOnlyList<string> UtilityKinds = ["smoke", "molotov", "he", "flash", "decoy"];

    /// <summary>
    ///     Round Facts' buy types lower-cased, plus <c>any</c> (overview correction 10, which replaced the design's
    ///     four-value list). Read off the enum so a buy type Round Facts adds is admitted here the same day.
    /// </summary>
    public static readonly IReadOnlyList<string> Economies =
        [.. Enum.GetNames<BuyType>().Select(n => n.ToLowerInvariant()), "any"];

    public static readonly IReadOnlyList<string> Tempos = ["slow", "mid", "fast"];

    public static readonly IReadOnlyList<string> TriggerKinds = ["time", "contact", "utility", "call"];

    /// <summary>The motion kinds Step Authoring defines (correction 17). <c>path</c> is reserved and warns.</summary>
    public static readonly IReadOnlyList<string> Interpolations = ["linear", "hold"];

    public const string InterpolationPathReserved = "path";

    public static readonly IReadOnlyList<string> TargetSites = ["A", "B"];
}

/// <summary>The four lifecycle states (§3.3). The file spells them as the enum names.</summary>
public enum StratStatus
{
    Theory,
    InProgress,
    Active,
    Archived
}

/// <summary>Who a book belongs to: a Team Identity team, or the user alone (<c>me</c>).</summary>
public sealed class StratOwner : IEquatable<StratOwner>
{
    public const string TeamKind = "team";
    public const string MeKind = "me";

    /// <summary><c>team</c> or <c>me</c>.</summary>
    public string Kind { get; set; } = MeKind;

    /// <summary>The Team Identity <c>Team.Id</c>; null for <c>me</c>.</summary>
    public Guid? TeamId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    ///     The owner folder under the strats root, and the key the index and the in-memory store use:
    ///     <c>team-&lt;guid&gt;</c> or <c>me</c>.
    /// </summary>
    [JsonIgnore]
    public string FolderName => IsTeam ? TeamKind + "-" + TeamId!.Value.ToString("D", CultureInfo.InvariantCulture) : MeKind;

    [JsonIgnore]
    public bool IsTeam => string.Equals(Kind, TeamKind, StringComparison.Ordinal) && TeamId is not null;

    public bool Equals(StratOwner? other) =>
        other is not null && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal);

    /// <summary>The user's own book.</summary>
    public static StratOwner Me() => new() { Kind = MeKind };

    /// <summary>A team's book.</summary>
    /// <param name="teamId">The Team Identity id.</param>
    public static StratOwner Team(Guid teamId) => new() { Kind = TeamKind, TeamId = teamId };

    /// <summary>The owner a folder name names, or null when it names none.</summary>
    /// <param name="folderName">An owner folder's name.</param>
    public static StratOwner? FromFolderName(string folderName)
    {
        if (string.Equals(folderName, MeKind, StringComparison.Ordinal))
        {
            return Me();
        }

        return folderName.StartsWith(TeamKind + "-", StringComparison.Ordinal)
               && Guid.TryParse(folderName.AsSpan(TeamKind.Length + 1), out Guid id)
            ? Team(id)
            : null;
    }

    public StratOwner Clone() => new() { Kind = Kind, TeamId = TeamId, Extra = Extra };

    public override bool Equals(object? obj) => Equals(obj as StratOwner);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(FolderName);

    public override string ToString() => FolderName;
}

/// <summary>
///     One strat (<c>&lt;id&gt;.dvstrat.json</c>, schema v1, §3.3). It is on the round clock and never stores a
///     player name, a demo path or a frame-clock tick; everything that touches a demo goes through a Tag Store
///     instance or <see cref="StratClock" />.
/// </summary>
public sealed class StratDocument
{
    /// <summary>Advisory: a higher number is read for what this build understands.</summary>
    public int SchemaVersion { get; set; } = StratStore.SchemaVersion;

    /// <summary>Stable for the life of the strat, across export and import.</summary>
    public Guid Id { get; set; }

    public StratOwner Owner { get; set; } = StratOwner.Me();

    public string Name { get; set; } = "";

    /// <summary>The parser's spelling, lower case: what <c>IModuleContext.MapName</c> and the bundles use.</summary>
    public string Map { get; set; } = "";

    /// <summary><c>T</c> or <c>CT</c>.</summary>
    public string Side { get; set; } = StratVocabulary.SideT;

    /// <summary>One of <see cref="StratVocabulary.Types" />; anything else warns.</summary>
    public string Type { get; set; } = "default";

    /// <summary><c>A</c>, <c>B</c> or null.</summary>
    public string? TargetSite { get; set; }

    public string? Economy { get; set; }

    public string? Tempo { get; set; }

    public StratTrigger? Trigger { get; set; }

    /// <summary>One of the <see cref="StratStatus" /> names.</summary>
    public string Status { get; set; } = nameof(StratStatus.Theory);

    /// <summary>Monotonic; moved only by a commit, which also appends the history line.</summary>
    public int Revision { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ModifiedUtc { get; set; }

    /// <summary>Set by Create Strat From Round; null otherwise.</summary>
    public StratOrigin? Origin { get; set; }

    /// <summary>Free strings, for filtering only.</summary>
    public List<string> Tags { get; set; } = [];

    public string? Notes { get; set; }

    /// <summary>The strat clock (§3.4), declared per plan F15. Not a demo clock.</summary>
    public StratClockInfo Clock { get; set; } = new();

    /// <summary>Step Authoring's canvas defaults (overview correction 17); null until it writes them.</summary>
    public StratCanvas? Canvas { get; set; }

    /// <summary>Exactly five, <c>A</c> to <c>E</c>, in order.</summary>
    public List<StratSlot> Slots { get; set; } = [];

    /// <summary>Authoring order, which is Role View's print order; <c>atSeconds</c> never increases along it.</summary>
    public List<StratStep> Steps { get; set; } = [];

    public List<StratBranch> Branches { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The parsed status, or null when the file holds a value this build does not know.</summary>
    [JsonIgnore]
    public StratStatus? StatusValue =>
        Enum.TryParse(Status, false, out StratStatus status) && Enum.IsDefined(status) && !int.TryParse(Status, out _)
            ? status
            : null;

    /// <summary>A deep copy, unknown fields included.</summary>
    public StratDocument Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, StratJsonContext.Default.StratDocument),
            StratJsonContext.Default.StratDocument)!;

    /// <summary>A new strat with the five empty slots, not yet saved.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="owner">The book it belongs to.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    /// <param name="side"><c>T</c> or <c>CT</c>.</param>
    /// <param name="type">The strat type.</param>
    /// <param name="name">The display name.</param>
    /// <param name="nowUtc">The creation time.</param>
    public static StratDocument Create(Guid id, StratOwner owner, string map, string side, string type, string name,
        DateTime nowUtc) => new()
    {
        Id = id,
        Owner = owner.Clone(),
        Name = name,
        Map = map.ToLowerInvariant(),
        Side = side,
        Type = type,
        CreatedUtc = nowUtc,
        ModifiedUtc = nowUtc,
        Slots = [.. StratVocabulary.Slots.Select(s => new StratSlot { Slot = s })]
    };
}

/// <summary>When the strat starts: the human text and an optional structured kind.</summary>
public sealed class StratTrigger
{
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="StratVocabulary.TriggerKinds" />, or null.</summary>
    public string? Kind { get; set; }

    /// <summary>Round clock remaining, for a <c>time</c> trigger.</summary>
    public double? AtSeconds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The round a strat was created from. Keyed by content hash, never by path.</summary>
public sealed class StratOrigin
{
    public string DemoSha256 { get; set; } = "";

    /// <summary><c>ClipRound.Number</c> (overview correction 12).</summary>
    public int Round { get; set; }

    public string? FileName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     The strat clock block (§3.4). <c>round</c> counts down from <see cref="RoundSeconds" />; <c>plant</c> is
///     reserved for post-plant strats and is not defined in v1 (decision 9).
/// </summary>
public sealed class StratClockInfo
{
    public string Kind { get; set; } = StratClock.RoundKind;

    public double RoundSeconds { get; set; } = StratClock.DefaultRoundSeconds;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Step Authoring's canvas block (overview correction 17), stored here so strats carry it from schema 1.</summary>
public sealed class StratCanvas
{
    public int FadeInTicks { get; set; } = 8;

    public int FadeOutTicks { get; set; } = 16;

    public bool ShowOpponents { get; set; } = true;

    public double? DefaultLevelMinZ { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A role in the strat, never a person. <see cref="SteamId" /> pins one player to it (§3.5).</summary>
public sealed class StratSlot
{
    public string Slot { get; set; } = "";

    public string? Role { get; set; }

    /// <summary>A SteamID64 as text; null means the book default for the epoch applies.</summary>
    public string? SteamId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     One instruction on the round clock (§3.3). <see cref="Positions" />, <see cref="Strokes" />,
///     <see cref="HoldSeconds" /> and <see cref="Interpolation" /> are Step Authoring's (§3.9); this build stores
///     them and does not interpret them.
/// </summary>
public sealed class StratStep
{
    public Guid Id { get; set; }

    /// <summary>Round clock REMAINING. Negative means after the timer stopped for a plant.</summary>
    public double AtSeconds { get; set; }

    /// <summary>A slot letter or <c>all</c>.</summary>
    public string Actor { get; set; } = StratVocabulary.ActorAll;

    public string Verb { get; set; } = "move";

    public PlaceRef? From { get; set; }

    public PlaceRef? To { get; set; }

    public UtilityRef? Utility { get; set; }

    public string? Note { get; set; }

    public List<StepPosition> Positions { get; set; } = [];

    /// <summary>
    ///     Annotation elements in the <c>.dvann.json</c> element shape, time fields omitted. Held as JSON so
    ///     whatever Step Authoring writes round-trips untouched until it defines the projection.
    /// </summary>
    public List<JsonObject> Strokes { get; set; } = [];

    public double? HoldSeconds { get; set; }

    /// <summary><c>linear</c>, <c>hold</c>, or null for Step Authoring's default; <c>path</c> is reserved.</summary>
    public string? Interpolation { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A canonical nav place name. Steps store places, never callouts.</summary>
public sealed class PlaceRef
{
    public string? Place { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     A token's position at a step (§3.9). <see cref="Slot" /> is a strat slot or an opponent slot
///     <c>O1..O5</c> (correction 17); <see cref="LevelMinZ" /> is the level's quantized lower Z, never a floor index.
/// </summary>
public sealed class StepPosition
{
    public string Slot { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double LevelMinZ { get; set; }

    public double? YawDegrees { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     A grenade a step throws (§3.3.3). <see cref="LineupId" /> is opaque here by design: this model does
///     not dereference it, only stores and round-trips it. Lineup On A Strat Step fills it from the Utility
///     Book's <c>GrenadeLineup.Id</c>, a deterministic key over the map, kind, landing cell and rounded
///     origin (not a persisted row: correction noted in docs/strat-format.md, since the design that reserved
///     this field assumed a minted <c>Lineup.Id</c>), and resolves it back through
///     <c>GrenadeIndex.DescribeLineup</c> for display on the step and in LAN Print.
/// </summary>
public sealed class UtilityRef
{
    public string Kind { get; set; } = "smoke";

    public Guid? LineupId { get; set; }

    public UtilityLanding? Landing { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where the grenade should land: a place, and the world point Step Authoring adds (correction 17).</summary>
public sealed class UtilityLanding
{
    public string? Place { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? LevelMinZ { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     After a step, if a condition holds, continue elsewhere (§3.3.4). A target in this strat
///     (<c>stratId == id</c>) is how a step chain is written without a second object kind.
/// </summary>
public sealed class StratBranch
{
    public Guid Id { get; set; }

    public Guid AfterStepId { get; set; }

    public BranchCondition Condition { get; set; } = new();

    public BranchTarget Target { get; set; } = new();

    public string? Note { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The human rule; <see cref="Kind" /> is reserved for a structured trigger and null in v1.</summary>
public sealed class BranchCondition
{
    public string Text { get; set; } = "";

    public string? Kind { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BranchTarget
{
    public Guid StratId { get; set; }

    public Guid? StepId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     <c>&lt;owner&gt;/book.json</c> (§3.5): the owner's default slot map per Team Identity epoch. Names are
///     never stored; <c>me</c> uses the single key <c>"me"</c>.
/// </summary>
public sealed class StratBook
{
    public int SchemaVersion { get; set; } = StratStore.SchemaVersion;

    public StratOwner Owner { get; set; } = StratOwner.Me();

    /// <summary>Epoch id to slot letter to SteamID64 text.</summary>
    public Dictionary<string, Dictionary<string, string>> SlotDefaults { get; set; } = [];

    public string? Notes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary><c>&lt;owner&gt;/&lt;map&gt;/callouts.json</c> (§3.7): the owner's words for the map's places.</summary>
public sealed class CalloutTable
{
    public int SchemaVersion { get; set; } = StratStore.SchemaVersion;

    public string Map { get; set; } = "";

    /// <summary>The canonical list when the aliases were last edited; used only to warn about a vanished name.</summary>
    public CalloutCanonicalSnapshot? Canonical { get; set; }

    public List<CalloutAlias> Aliases { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CalloutCanonicalSnapshot
{
    public string? Source { get; set; }

    public List<string> Names { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One word for one place. Several aliases may share a place; one alias names one place.</summary>
public sealed class CalloutAlias
{
    public string Alias { get; set; } = "";

    public string Place { get; set; } = "";

    /// <summary>The alias the UI shows for the place. Written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Primary { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
///     One line of <c>&lt;id&gt;.history.jsonl</c> (§3.8): the ops of one commit. Revision 1 is a single
///     <c>add</c> at <c>""</c> carrying the whole document.
/// </summary>
public sealed class HistoryEntry
{
    public int Revision { get; set; }

    public DateTime AtUtc { get; set; }

    public string? Summary { get; set; }

    public List<PatchOp> Ops { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The entry that undoes this one: ops reversed, each inverted. Pure; no replay needed.</summary>
    public HistoryEntry Inverse() => new()
    {
        Revision = Revision,
        AtUtc = AtUtc,
        Summary = Summary,
        Ops = [.. Ops.AsEnumerable().Reverse().Select(o => o.Inverse())]
    };
}

/// <summary>
///     A subset of RFC 6902: <c>add</c>, <c>remove</c>, <c>replace</c>, with <see cref="Path" /> an RFC 6901
///     pointer into the strat document. <see cref="From" /> is this design's addition, not RFC 6902's: the value
///     displaced by a <c>replace</c> or <c>remove</c>, so an entry renders and inverts without replaying.
///     A null <see cref="Value" /> is written by omission and read back as JSON null. The applier accepts RFC
///     6902's <c>-</c> (append), but ops bound for the log name the concrete index: the inverse of an append
///     has to say which element it removes.
/// </summary>
public sealed class PatchOp
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Replace = "replace";

    public string Op { get; set; } = Replace;

    public string Path { get; set; } = "";

    public JsonNode? From { get; set; }

    public JsonNode? Value { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static PatchOp AddOp(string path, JsonNode? value) => new() { Op = Add, Path = path, Value = value };

    public static PatchOp RemoveOp(string path, JsonNode? from) => new() { Op = Remove, Path = path, From = from };

    public static PatchOp ReplaceOp(string path, JsonNode? from, JsonNode? value) =>
        new() { Op = Replace, Path = path, From = from, Value = value };

    /// <summary>The op that undoes this one.</summary>
    public PatchOp Inverse() => Op switch
    {
        Add => RemoveOp(Path, Value?.DeepClone()),
        Remove => AddOp(Path, From?.DeepClone()),
        _ => ReplaceOp(Path, Value?.DeepClone(), From?.DeepClone())
    };

    public PatchOp Clone() => new() { Op = Op, Path = Path, From = From?.DeepClone(), Value = Value?.DeepClone() };
}

/// <summary>
///     One row of <c>strats/index.json</c>: the small projection the Strat Book lists and filters on without
///     opening a strat. Evidence counts are deliberately absent; they belong to the Tag Store.
/// </summary>
public sealed class StratIndexEntry
{
    public Guid Id { get; set; }

    public StratOwner Owner { get; set; } = StratOwner.Me();

    public string Map { get; set; } = "";

    public string Side { get; set; } = "";

    public string Type { get; set; } = "";

    public string? TargetSite { get; set; }

    public string Status { get; set; } = "";

    public string Name { get; set; } = "";

    public int Revision { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public int StepCount { get; set; }

    /// <summary>The projection of a document.</summary>
    /// <param name="document">The strat.</param>
    public static StratIndexEntry From(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new StratIndexEntry
        {
            Id = document.Id,
            Owner = document.Owner.Clone(),
            Map = document.Map,
            Side = document.Side,
            Type = document.Type,
            TargetSite = document.TargetSite,
            Status = document.Status,
            Name = document.Name,
            Revision = document.Revision,
            ModifiedUtc = document.ModifiedUtc,
            StepCount = document.Steps.Count
        };
    }
}

/// <summary><c>strats/index.json</c>, the <c>DemoCacheIndexFile</c> shape. Derived; rebuilt from the folders when lost.</summary>
public sealed class StratIndexFile
{
    public int Version { get; set; } = 1;

    public List<StratIndexEntry> Entries { get; set; } = [];
}

/// <summary>
///     Source-generated, like the tag and annotation contexts: the browser head cannot trim a reflection-based
///     serializer. Indented, for the files a person reads and the golden pins.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    NewLine = "\n")]
[JsonSerializable(typeof(StratDocument))]
[JsonSerializable(typeof(StratIndexFile))]
[JsonSerializable(typeof(StratBook))]
[JsonSerializable(typeof(CalloutTable))]
[JsonSerializable(typeof(HistoryEntry))]
public sealed partial class StratJsonContext : JsonSerializerContext;

/// <summary>The history log's context: the same shapes on one line each, which is what makes the log a <c>.jsonl</c>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HistoryEntry))]
public sealed partial class StratHistoryJsonContext : JsonSerializerContext;
