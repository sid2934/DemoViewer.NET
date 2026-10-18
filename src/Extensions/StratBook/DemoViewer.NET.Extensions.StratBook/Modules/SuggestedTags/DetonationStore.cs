#region

using System.Numerics;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>
///     What Suggested Tags read off a demo's parse: the detonations with their throwers resolved, unplaced, and
///     the clock they are on. Everything else a build reads (Round Facts rows, the round index) is stored
///     elsewhere, so proposals can be built again from these without a parse.
/// </summary>
public sealed class DetonationDocument
{
    /// <summary>The shape version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The shape version this was written at.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The map the parse named, or the library's.</summary>
    public string Map { get; set; } = "";

    /// <summary>The parse's frame clock header.</summary>
    public RoundFactsClock Clock { get; set; } = new();

    /// <summary>Every detonation in tick order, unplaced.</summary>
    public List<StoredDetonation> Events { get; set; } = [];

    /// <summary>The events as the detectors take them.</summary>
    public List<PlacedEvent> ToEvents() =>
        [.. Events.Select(e => new PlacedEvent(e.Tick, e.Kind, e.ThrowerSlot, 0, new Vector3(e.X, e.Y, e.Z), null, e.ThrowerSteamId, e.ThrowerName))];

    /// <summary>A document over unplaced events.</summary>
    /// <param name="map">The map.</param>
    /// <param name="clock">The parse's clock header.</param>
    /// <param name="events">The events, unplaced.</param>
    public static DetonationDocument From(string map, RoundFactsClock clock, IEnumerable<PlacedEvent> events) => new()
    {
        Map = map,
        Clock = clock,
        Events =
        [
            .. events.Select(e => new StoredDetonation
            {
                Tick = e.Tick,
                Kind = e.Kind,
                ThrowerSlot = e.ThrowerSlot,
                X = e.Position.X,
                Y = e.Position.Y,
                Z = e.Position.Z,
                ThrowerSteamId = e.ThrowerSteamId,
                ThrowerName = e.ThrowerName
            })
        ]
    };
}

/// <summary>One stored detonation.</summary>
public sealed class StoredDetonation
{
    /// <summary>The detonation, frame clock.</summary>
    public int Tick { get; set; }

    /// <summary>The <see cref="DetonationEvents" /> kind.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The thrower's slot, or -1.</summary>
    public int ThrowerSlot { get; set; }

    /// <summary>World X.</summary>
    public float X { get; set; }

    /// <summary>World Y.</summary>
    public float Y { get; set; }

    /// <summary>World Z.</summary>
    public float Z { get; set; }

    /// <summary>The thrower's SteamID64, or 0.</summary>
    public ulong ThrowerSteamId { get; set; }

    /// <summary>The thrower's name, or null.</summary>
    public string? ThrowerName { get; set; }
}

/// <summary>
///     Each demo's <see cref="DetonationDocument" />, kept as Strat Book per-demo data. The fingerprint is the
///     thrower source, so a changed collector or engine marks it stale.
/// </summary>
public sealed class DetonationStore
{
    /// <summary>The facet's name in the per-demo data.</summary>
    public const string Facet = "detonations";

    /// <summary>The stamp's schema.</summary>
    public const int Schema = DetonationDocument.CurrentSchemaVersion;

    /// <param name="data">The extension's per-demo data.</param>
    public DetonationStore(IExtensionDemoData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    /// <summary>The per-demo data the facet lives in.</summary>
    public IExtensionDemoData Data { get; }

    /// <summary>The fingerprint the documents are written under now.</summary>
    public static string Fingerprint => SuggestionsFingerprint.ThrowerSource;

    /// <summary>A demo's stamp, or null.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public DemoDataStamp? Stamp(string demoPath) => Data.Stamp(demoPath, Facet);

    /// <summary>Whether the demo's document was written under the current thrower source.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public bool IsCurrent(string demoPath) => Stamp(demoPath)?.IsCurrent(Schema, Fingerprint) ?? false;

    /// <summary>Writes a demo's document. Throws on an I/O failure.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="document">The document.</param>
    public void Write(string demoPath, DetonationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, SuggestedTagsJsonContext.Default.DetonationDocument));
        Data.Write(demoPath, new DemoDataWrite(Facet, Schema, Fingerprint, payload) { Count = document.Events.Count });
    }

    /// <summary>A demo's current document, or null when there is none, it is stale, or it does not parse.</summary>
    /// <param name="demoPath">The demo's path.</param>
    public DetonationDocument? TryRead(string demoPath)
    {
        if (Data.Read(demoPath, Facet, Schema, Fingerprint) is not { } bytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, SuggestedTagsJsonContext.Default.DetonationDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
