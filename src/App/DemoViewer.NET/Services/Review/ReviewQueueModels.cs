#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.Services.Review;

/// <summary>What a queue entry is: a clip to watch, or the title card that opens a section.</summary>
public enum ReviewEntryKind
{
    Clip,
    Section
}

/// <summary>
///     Where a clip came from. Plain strings rather than an enum so a surface that lands later (a
///     strat's failure list, Pack Export) adds a value without a schema bump, and a file written by a
///     newer build still reads.
/// </summary>
public static class ReviewSources
{
    /// <summary>Staged from the Reels tray, Match Overview's [ + ] or the Add-clips picker.</summary>
    public const string Highlight = "highlight";

    /// <summary>Sent from Situation Search's Result Cards.</summary>
    public const string Situation = "situation";

    /// <summary>A tag instance: one ref of a Matrix cell or of a <c>TagQuery.Find</c> result.</summary>
    public const string Tag = "tag";

    /// <summary>Picked by hand at the playhead.</summary>
    public const string Manual = "manual";
}

/// <summary>
///     The identity of a staged highlight, carried on the clip the Reels tray put in the queue so the
///     tray can find its own clips again and re-resolve them against the cache. The tray's key minus
///     the path, which the entry already carries.
/// </summary>
/// <param name="RulesetId">Ruleset that emitted the highlight.</param>
/// <param name="HighlightId">Highlight id inside that ruleset.</param>
/// <param name="Tick">Firing tick, frame clock.</param>
/// <param name="PlayerSlot">Attributed player slot.</param>
public sealed record ReviewHighlightRef(string RulesetId, string HighlightId, int Tick, int PlayerSlot);

/// <summary>
///     One row of the queue: a clip, <c>(demo, from, to, note)</c> plus the one-line question the
///     reviewer is asked, or a section's title card. One shape for both in one ordered list, because a
///     section is simply where its card sits: every clip after a title card and before the next belongs
///     to it, so moving a card moves the boundary and nothing else is rewritten.
///     <para>
///         Immutable: the queue replaces an entry with <c>with</c>, so a reader holding the list it was
///         handed never sees a field change under it.
///     </para>
/// </summary>
public sealed record ReviewEntry
{
    /// <summary>Stable across edits and moves; the key every mutation names.</summary>
    public Guid Id { get; init; }

    public ReviewEntryKind Kind { get; init; }

    /// <summary>A section's title; empty on a clip.</summary>
    public string Title { get; init; } = "";

    /// <summary>The demo's path when the clip was queued.</summary>
    public string DemoPath { get; init; } = "";

    /// <summary>The demo's content hash when the surface knew it: the join that survives a moved file.</summary>
    public string? Sha256 { get; init; }

    /// <summary>First tick of the clip, frame clock (the file's <c>clock</c> block says so).</summary>
    public int FromTick { get; init; }

    /// <summary>Last tick of the clip, frame clock, inclusive.</summary>
    public int ToTick { get; init; }

    /// <summary>The demo's tick rate, for the clip's length in seconds; 0 when the surface did not know it.</summary>
    public int TickRate { get; init; }

    /// <summary>What the clip is, as the surface described it; on a title card, the line under the title.</summary>
    public string Note { get; init; } = "";

    /// <summary>The one-line question the reviewer answers while watching; empty until someone writes one.</summary>
    public string Question { get; init; } = "";

    /// <summary>One of <see cref="ReviewSources" />; empty on a section.</summary>
    public string Source { get; init; } = "";

    /// <summary>The staged highlight's identity on a clip the Reels tray owns; null on every other clip.</summary>
    public ReviewHighlightRef? Highlight { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>The clip's length in seconds, or null without a tick rate.</summary>
    [JsonIgnore]
    public double? Seconds => TickRate > 0 ? (ToTick - FromTick) / (double)TickRate : null;

    /// <summary>A new clip with a fresh id. A reversed range is put the right way round.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="fromTick">First tick, frame clock.</param>
    /// <param name="toTick">Last tick, frame clock.</param>
    /// <param name="note">What the clip is.</param>
    /// <param name="source">One of <see cref="ReviewSources" />.</param>
    /// <param name="tickRate">The demo's tick rate, or 0.</param>
    /// <param name="sha256">The demo's content hash, or null.</param>
    /// <param name="highlight">The highlight identity, for a Reels clip.</param>
    public static ReviewEntry Clip(string demoPath, int fromTick, int toTick, string note, string source,
        int tickRate = 0, string? sha256 = null, ReviewHighlightRef? highlight = null)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        return new ReviewEntry
        {
            Id = Guid.NewGuid(),
            Kind = ReviewEntryKind.Clip,
            DemoPath = demoPath,
            Sha256 = sha256,
            FromTick = Math.Max(0, Math.Min(fromTick, toTick)),
            ToTick = Math.Max(0, Math.Max(fromTick, toTick)),
            TickRate = Math.Max(0, tickRate),
            Note = note ?? "",
            Source = source ?? "",
            Highlight = highlight
        };
    }

    /// <summary>A new section title card with a fresh id.</summary>
    /// <param name="title">The card's title.</param>
    /// <param name="subtitle">The line under it, or empty.</param>
    public static ReviewEntry Section(string title, string subtitle = "") => new()
    {
        Id = Guid.NewGuid(),
        Kind = ReviewEntryKind.Section,
        Title = title ?? "",
        Note = subtitle ?? ""
    };
}

/// <summary>
///     <c>review-queue.json</c> under the config root: the ordered queue, title cards and clips in one
///     list. User truth, refused rather than overwritten when it cannot be read (the
///     watched-situations rule).
///     <para>
///         <b>The clock block.</b> Every tick here is on the frame clock, the one the timeline, the
///         annotation sidecar and the tag store use (F15). The queue spans demos, so the header names
///         the clock and each clip carries its own demo's tick rate; there is no single frame count to
///         record.
///     </para>
/// </summary>
public sealed class ReviewQueueFile
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public int SchemaVersion { get; set; } = CurrentSchema;

    public ReviewClockHeader Clock { get; set; } = new();

    /// <summary>The queue in order.</summary>
    public List<ReviewEntry> Entries { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The file's <c>clock</c> block: which clock every tick in it is on.</summary>
public sealed class ReviewClockHeader
{
    public string Kind { get; set; } = ClockIdentity.DvFrameClock;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
