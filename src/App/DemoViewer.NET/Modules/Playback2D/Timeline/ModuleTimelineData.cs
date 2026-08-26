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
    private const string RawTeam = "Team";
    private const string RawOldTeam = "OldTeam";

    /// <summary>
    ///     The only demo-carried record of who was on which side, and the same one the parser's own team
    ///     post-pass is fed by — so a timeline tint and <c>PlayerInfo.Team</c> cannot disagree.
    /// </summary>
    private const string TeamEvent = "player_team";

    private readonly Dictionary<string, TimelineEventRecord[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IModuleContext _context;

    // slot → its side changes, ascending by tick. Built once on first team read and dropped by Invalidate.
    // Null means "not built yet"; an EMPTY map means the demo carries no player_team at all, which is a
    // real answer (every team read then misses and the consumer keeps its neutral rendering).
    private Dictionary<int, List<TeamChange>>? _teamChanges;

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

    /// <summary>
    ///     Which side a slot was on at a tick, in <see cref="TimelineEventKeys.Winner" />'s encoding
    ///     ("2" = T, "3" = CT as integers), or <c>0</c> when the demo cannot say.
    ///     <para>
    ///         Public because the kill FEED needs the same answer the kill TRACK gets, and it cannot get
    ///         it from <see cref="EventsOfType" />: that list is tick-sorted and drops events with no
    ///         frame, so it is not index-aligned with <c>IModuleContext.GetEventTimeline</c> and pairing
    ///         the two by position would silently misattribute a side the moment either happened. One
    ///         resolver, asked directly, is the only version of this that cannot drift — the halftime
    ///         finding it encodes lives in exactly one place.
    ///     </para>
    /// </summary>
    /// <param name="slot">The roster slot.</param>
    /// <param name="tick">The tick to answer for.</param>
    public int TeamForSlotAtTick(int slot, int tick) => slot < 0 ? 0 : TeamForSlotAt(slot, tick);

    /// <summary>Drops the per-name cache. Called when the demo changes under the tab.</summary>
    public void Invalidate()
    {
        _cache.Clear();
        _teamChanges = null;
    }

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

        SetSide(fields, view, RawAttacker, TimelineEventKeys.Team);
        SetSide(fields, view, RawUserId, TimelineEventKeys.VictimTeam);
        return fields;
    }

    // One participant's side, resolved AT the event's own tick. Only events that actually name that
    // participant get a key: there is nothing to resolve otherwise, and writing a key the raw payload
    // also spells would clobber it.
    //
    // An unresolvable side leaves the key ABSENT rather than writing "0" — the consumer's fallback is a
    // missing key, and a kill must never lose its marker to a demo that cannot say who shot it.
    //
    // Attacker and victim share this because they share the failure mode: one walk, one place that knows
    // player_team is halftime-only, no second copy to drift.
    private void SetSide(Dictionary<string, string> fields, GameEventView view, string rawSlotKey,
        string outKey)
    {
        int slot = ReadSlot(view, rawSlotKey);
        if (slot < 0)
        {
            return;
        }

        int team = TeamForSlotAt(slot, view.Tick);
        if (team > 0)
        {
            fields[outKey] = team.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            fields.Remove(outKey);
        }
    }

    // Which side a slot was on at a tick, or 0 when the demo cannot say.
    //
    // GOTV does NOT emit player_team for the initial seating — measured on both reference demos, the only
    // player_team events in the whole file are the halftime swap, all on one tick (the finding that made
    // the demo trimmer synthesize them; see tools/DemoViewer.NET.DemoTrimmer/TeamEventSynthesizer.cs).
    // That is why the walk reads OldTeam when the first recorded change lies AHEAD of the tick asked
    // about: for every kill in the first half, the side a player is swapping away from at half is the
    // only record of the side they spent that half on.
    private int TeamForSlotAt(int slot, int tick)
    {
        _teamChanges ??= BuildTeamChanges();

        if (!_teamChanges.TryGetValue(slot, out List<TeamChange>? changes))
        {
            return 0;
        }

        int team = 0;
        foreach (TeamChange change in changes)
        {
            if (change.Tick > tick)
            {
                return team != 0 ? team : change.OldTeam;
            }

            team = change.Team;
        }

        return team;
    }

    private Dictionary<int, List<TeamChange>> BuildTeamChanges()
    {
        Dictionary<int, List<TeamChange>> map = new();

        // Sorted here for the same reason Build sorts: GetEventTimeline's order is not guaranteed, and the
        // walk above is a first-match-wins scan that reads an unsorted list as a different demo.
        foreach (GameEventView view in _context.GetEventTimeline(TeamEvent).OrderBy(v => v.Tick))
        {
            int slot = ReadSlot(view, RawUserId);
            if (slot < 0)
            {
                continue;
            }

            if (!map.TryGetValue(slot, out List<TeamChange>? changes))
            {
                map[slot] = changes = new List<TeamChange>(2);
            }

            changes.Add(new TeamChange(view.Tick, ReadInt(view, RawTeam), ReadInt(view, RawOldTeam)));
        }

        return map;
    }

    // A KV1 `byte` field materialises as a boxed int (GameEventViewFactory.ToFields); anything else is a
    // field this demo did not carry.
    private static int ReadInt(GameEventView view, string key) =>
        view.Fields.TryGetValue(key, out object? v) && v is int i ? i : 0;

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

    /// <summary>One <c>player_team</c> fire: the side taken, and the side left behind.</summary>
    private readonly record struct TeamChange(int Tick, int Team, int OldTeam);
}
