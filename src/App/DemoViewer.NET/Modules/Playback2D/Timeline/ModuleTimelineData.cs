#region

using System.Globalization;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     The app-side <see cref="ITimelineData" /> adapter over <see cref="IModuleContext" />. It does ALL the
///     demo-domain work — projecting <see cref="GameEventView" />s onto the frame axis, resolving player
///     slots to roster display names, flattening boxed field values to invariant strings — so a track never
///     sees a host type and the whole <c>Timeline/</c> contract folder stays renderer- and host-independent.
///     <para>
///         This file is deliberately NOT part of that Core-clean set: it is the boundary.
///     </para>
/// </summary>
public sealed class ModuleTimelineData : ITimelineData
{
    // Raw SDK payload property names. The catalog embedded in CS2DemoKit.Analysis is the authoritative
    // spelling; GameEventView.Fields is OrdinalIgnoreCase, so casing here is documentation, not a lookup key.
    private const string RawAttacker = "Attacker";
    private const string RawUserId = "UserId";
    private const string RawAssister = "Assister";
    private const string RawWeapon = "Weapon";
    private const string RawHeadshot = "Headshot";
    private const string RawSite = "Site";
    private const string RawWinner = "Winner";

    private readonly Dictionary<string, TimelineEventRecord[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IModuleContext _context;

    /// <summary>Wraps a live module context. The adapter holds no subscriptions of its own.</summary>
    public ModuleTimelineData(IModuleContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public int TotalFrames => _context.TotalFrames;

    /// <inheritdoc />
    public int TickRate => _context.TickRate;

    /// <inheritdoc />
    public int FrameIndexAtTick(int tick) => _context.FrameIndexAtTick(tick);

    /// <inheritdoc />
    public IReadOnlyList<int> FramesForEvent(string eventName) =>
        string.IsNullOrEmpty(eventName) ? Array.Empty<int>() : _context.EventFrames(eventName);

    /// <inheritdoc />
    public bool HasEvent(string eventName)
    {
        if (string.IsNullOrEmpty(eventName))
        {
            return false;
        }

        foreach (string name in _context.AvailableEventNames)
        {
            if (string.Equals(name, eventName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName)
    {
        if (string.IsNullOrEmpty(eventName))
        {
            return Array.Empty<TimelineEventRecord>();
        }

        if (_cache.TryGetValue(eventName, out TimelineEventRecord[]? cached))
        {
            return cached;
        }

        TimelineEventRecord[] built = Build(eventName);
        _cache[eventName] = built;
        return built;
    }

    /// <summary>Drops the per-name cache. Called when the demo changes under the tab.</summary>
    public void Invalidate() => _cache.Clear();

    private TimelineEventRecord[] Build(string eventName)
    {
        IReadOnlyList<GameEventView> views = _context.GetEventTimeline(eventName);
        if (views.Count == 0)
        {
            return Array.Empty<TimelineEventRecord>();
        }

        List<TimelineEventRecord> records = new(views.Count);

        // GetEventTimeline explicitly does NOT guarantee order (the parse is two-pass parallel), and a
        // marker list handed back unsorted would place tooltips on the wrong neighbours after coalescing.
        foreach (GameEventView view in views.OrderBy(v => v.Tick))
        {
            int frameIndex = _context.FrameIndexAtTick(view.Tick);
            if (frameIndex < 0)
            {
                continue; // past the end of the frame list — no place to draw it
            }

            records.Add(new TimelineEventRecord(view.Tick, frameIndex, Flatten(view)));
        }

        return records.ToArray();
    }

    // Flattens the boxed field values to invariant strings, then overwrites the normalized TimelineEventKeys
    // with the display-resolved forms a track expects. The dictionary is OrdinalIgnoreCase, so writing
    // "attacker" replaces the raw "Attacker" slot number rather than sitting beside it.
    private Dictionary<string, string> Flatten(GameEventView view)
    {
        Dictionary<string, string> fields = new(view.Fields.Count + 4, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, object?> kv in view.Fields)
        {
            fields[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? "";
        }

        SetName(fields, view, RawAttacker, TimelineEventKeys.Attacker);
        SetName(fields, view, RawUserId, TimelineEventKeys.Victim);
        SetName(fields, view, RawAssister, TimelineEventKeys.Assister);
        Copy(fields, view, RawWeapon, TimelineEventKeys.Weapon);
        Copy(fields, view, RawSite, TimelineEventKeys.Site);
        Copy(fields, view, RawWinner, TimelineEventKeys.Winner);

        if (view.Fields.TryGetValue(RawHeadshot, out object? headshot))
        {
            fields[TimelineEventKeys.Headshot] = headshot is true ? "1" : "0";
        }

        return fields;
    }

    private void SetName(Dictionary<string, string> fields, GameEventView view, string rawKey, string key)
    {
        int slot = ReadSlot(view, rawKey);
        if (slot < 0)
        {
            fields.Remove(key);
            return;
        }

        fields[key] = NameForSlot(slot);
    }

    private static void Copy(Dictionary<string, string> fields, GameEventView view, string rawKey, string key)
    {
        if (view.Fields.TryGetValue(rawKey, out object? value))
        {
            fields[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    // The slot-reading shape the 2D tab already uses for its kill feed: a player_* field materializes as a
    // boxed int controller slot; anything else (absent, sentinel handle) means "no player".
    private static int ReadSlot(GameEventView view, string key) =>
        view.Fields.TryGetValue(key, out object? v) && v is int i ? i : -1;

    private string NameForSlot(int slot)
    {
        foreach (PlayerRosterEntry entry in _context.Players)
        {
            if (entry.Slot == slot)
            {
                return string.IsNullOrWhiteSpace(entry.Name) ? "world" : entry.Name;
            }
        }

        return "world";
    }
}
