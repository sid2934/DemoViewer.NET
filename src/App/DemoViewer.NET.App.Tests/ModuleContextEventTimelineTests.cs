#region

using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using Cs2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Validates the real wiring the kill feed depends on (the part the synthetic kill-feed test can't see):
///     the host hands <see cref="ModuleContext.SetGameEvents" /> the demo's <c>AllGameEvents</c>, and
///     <see cref="ModuleContext.GetEventTimeline" /> projects + caches the player_death subset into enriched
///     <see cref="GameEventView" />s. Asserts the count matches the parse, the events are enriched, and the
///     per-name result is cached (same instance on the second call).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ModuleContextEventTimelineTests
{
    [Test]
    public async Task GetEventTimeline_ProjectsAndCachesThePlayerDeathSubset()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        PlaybackController controller = new();
        controller.LoadDemo(demo.Frames, demo.TickRate);
        ModuleContext context = new(controller, () => path);
        context.SetGameEvents(demo.AllGameEvents); // exactly what the shell does at load

        IReadOnlyList<GameEventView> kills = context.GetEventTimeline("player_death");

        int expected = demo.AllGameEvents.Count(e => e.Name == "player_death");
        await Assert.That(expected).IsGreaterThan(0);
        await Assert.That(kills.Count).IsEqualTo(expected);
        await Assert.That(kills.All(k => k.Name == "player_death")).IsTrue();

        // Enriched (typed fields), each carrying its own tick.
        GameEventView sample = kills[0];
        await Assert.That(sample.Fields.ContainsKey("Attacker")).IsTrue();
        await Assert.That(sample.Fields["UserId"] is int).IsTrue();
        await Assert.That(sample.Fields["Weapon"] is string).IsTrue();
        await Assert.That(sample.Tick).IsGreaterThan(0);

        // Cached per name — the second call returns the SAME instance (built once).
        await Assert.That(ReferenceEquals(kills, context.GetEventTimeline("player_death"))).IsTrue();

        // A name the demo lacks (or that's never requested) yields an empty timeline, not a throw.
        await Assert.That(context.GetEventTimeline("definitely_not_an_event").Count).IsEqualTo(0);
    }
}
