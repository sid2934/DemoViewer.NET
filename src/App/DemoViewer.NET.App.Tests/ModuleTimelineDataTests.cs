#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The boundary adapter. It carries every demo-domain concern the Core-clean track contract refuses to
///     know about: unordered host timelines, boxed field values, slot numbers that mean nothing without the
///     roster, and events whose tick lands past the end of the frame list.
/// </summary>
public class ModuleTimelineDataTests
{
    [Test]
    public async Task EventsOfType_SortsByTick()
    {
        // GetEventTimeline's order is explicitly not guaranteed (the parse is two-pass parallel).
        Playback2DFakeContext ctx = Context();
        ctx.Timelines["player_death"] = [Event(400), Event(100), Event(250)];

        ModuleTimelineData data = new(ctx);
        IReadOnlyList<TimelineEventRecord> records = data.EventsOfType("player_death");

        int[] expectedTicks = [100, 250, 400];
        await Assert.That(records.Select(r => r.Tick)).IsEquivalentTo(expectedTicks);
        await Assert.That(records[0].FrameIndex).IsEqualTo(50);
    }

    [Test]
    public async Task EventsOfType_CachesPerName()
    {
        Playback2DFakeContext ctx = Context();
        ctx.Timelines["player_death"] = [Event(100)];

        ModuleTimelineData data = new(ctx);
        IReadOnlyList<TimelineEventRecord> first = data.EventsOfType("player_death");
        IReadOnlyList<TimelineEventRecord> second = data.EventsOfType("player_death");

        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task EventsOfType_ResolvesSlotFieldsToRosterNames()
    {
        Playback2DFakeContext ctx = Context();
        ctx.Timelines["player_death"] =
        [
            Event(100, ("Attacker", 0), ("UserId", 2), ("Assister", 1), ("Weapon", "ak47"),
                ("Headshot", true))
        ];

        ModuleTimelineData data = new(ctx);
        TimelineEventRecord record = data.EventsOfType("player_death")[0];

        await Assert.That(record.Fields[TimelineEventKeys.Attacker]).IsEqualTo("Alpha");
        await Assert.That(record.Fields[TimelineEventKeys.Victim]).IsEqualTo("Charlie");
        await Assert.That(record.Fields[TimelineEventKeys.Assister]).IsEqualTo("Bravo");
        await Assert.That(record.Fields[TimelineEventKeys.Weapon]).IsEqualTo("ak47");
        await Assert.That(record.Fields[TimelineEventKeys.Headshot]).IsEqualTo("1");
    }

    [Test]
    public async Task EventsOfType_OmitsAssisterWhenThereWasNone()
    {
        Playback2DFakeContext ctx = Context();
        ctx.Timelines["player_death"] = [Event(100, ("Attacker", 0), ("UserId", 1), ("Assister", -1))];

        ModuleTimelineData data = new(ctx);
        TimelineEventRecord record = data.EventsOfType("player_death")[0];

        await Assert.That(record.Fields.ContainsKey(TimelineEventKeys.Assister)).IsFalse();
    }

    [Test]
    public async Task EventsOfType_DropsUnresolvableTicks()
    {
        // TotalFrames 1000 and two frames per tick ⇒ tick 5000 resolves to -1 and must not be placed at all.
        Playback2DFakeContext ctx = Context();
        ctx.Timelines["player_death"] = [Event(100), Event(5000)];

        ModuleTimelineData data = new(ctx);
        IReadOnlyList<TimelineEventRecord> records = data.EventsOfType("player_death");

        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Tick).IsEqualTo(100);
    }

    [Test]
    public async Task Invalidate_ClearsCache()
    {
        Playback2DFakeContext ctx = Context();
        ctx.Timelines["player_death"] = [Event(100)];

        ModuleTimelineData data = new(ctx);
        IReadOnlyList<TimelineEventRecord> first = data.EventsOfType("player_death");

        data.Invalidate();
        ctx.Timelines["player_death"] = [Event(100), Event(200)];

        IReadOnlyList<TimelineEventRecord> second = data.EventsOfType("player_death");

        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(second.Count).IsEqualTo(2);
    }

    [Test]
    public async Task HasEvent_IsCaseInsensitive_AndFramesForEventDelegates()
    {
        Playback2DFakeContext ctx = Context();
        ctx.Frames["round_freeze_end"] = [0, 300];

        ModuleTimelineData data = new(ctx);

        await Assert.That(data.HasEvent("ROUND_FREEZE_END")).IsTrue();
        await Assert.That(data.HasEvent("bomb_planted")).IsFalse();
        int[] expectedFrames = [0, 300];
        await Assert.That(data.FramesForEvent("round_freeze_end")).IsEquivalentTo(expectedFrames);
        await Assert.That(data.TotalFrames).IsEqualTo(ctx.TotalFrames);
    }

    private static Playback2DFakeContext Context()
    {
        Playback2DFakeContext ctx = new();
        ctx.AddPlayer(0, "Alpha", 2);
        ctx.AddPlayer(1, "Bravo", 2);
        ctx.AddPlayer(2, "Charlie", 3);
        return ctx;
    }

    private static GameEventView Event(int tick, params (string Key, object? Value)[] fields)
    {
        Dictionary<string, object?> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object? value) in fields)
        {
            map[key] = value;
        }

        return new GameEventView
        {
            Name = "player_death",
            Tick = tick,
            Fields = map
        };
    }
}
