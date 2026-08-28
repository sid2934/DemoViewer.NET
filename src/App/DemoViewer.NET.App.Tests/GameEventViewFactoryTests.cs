#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Part 1 of the kill feed (module event delivery): <see cref="GameEventViewFactory" /> must enrich a
///     frame's <see cref="GameEventView" />s with TYPED, de-stringified fields from the decoded event, so a
///     module can read killer/victim/weapon/headshot directly. Verified on a real <c>player_death</c>: int
///     slots, a bool headshot, and an UNQUOTED weapon string (the parser's <c>F()</c> wrapper quotes strings;
///     the factory must strip them).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class GameEventViewFactoryTests
{
    [Test]
    public async Task FromEvent_StampsGameTick_NotServerTick_SoTheFeedAlignsWithThePlayhead()
    {
        // The kill feed window-filters event ticks against the PLAYHEAD tick (PlaybackController.CurrentTick
        // = DemoFrame.ServerTick, which IS the game tick in CS2). CS2 delivers a player_death message in a
        // later demo frame than it fired, so GameEvent.ServerTick (delivery) = GameEvent.GameTick + a
        // constant ServerStartTick. Stamping the view with ServerTick made the kill appear ServerStartTick
        // ticks late; it must use GameTick.
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        GameEvent? death = demo.AllGameEvents.FirstOrDefault(e => e.Name == "player_death");
        if (death is null)
        {
            throw new SkipTestException("no player_death in demo");
        }

        GameEventView view = GameEventViewFactory.FromEvent(death);
        Console.WriteLine($"[tick-align] player_death ServerTick={death.ServerTick} " +
                          $"GameTick={death.GameTick} view.Tick={view.Tick}");

        await Assert.That(view.Tick).IsEqualTo(death.GameTick);

        // On a demo with a ServerStartTick offset (server tick ≠ game tick) this also proves the view is NOT
        // stamped with the late delivery tick — the exact bug.
        if (death.ServerTick != death.GameTick)
        {
            await Assert.That(view.Tick).IsNotEqualTo(death.ServerTick);
        }

        // The kill-NAV (A3) uses frame INDICES (SemanticNavigator), so confirm the player_death MESSAGE sits
        // in the frame at the TRUE game tick — i.e. the event is NOT delivered in a later frame. If this
        // holds, "jump to next kill" lands correctly and shares no bug with the feed (which used the wrong
        // tick FIELD, not a late frame).
        int msgFrameTick = -1;
        foreach (DemoFrame f in demo.Frames)
        {
            if (f.InnerMessages.Any(m => m is GameEventMessage g &&
                                         g.DecodedEvent.Name == "player_death"))
            {
                msgFrameTick = f.ServerTick;
                break;
            }
        }

        Console.WriteLine($"[tick-align] first player_death message-frame ServerTick={msgFrameTick} " +
                          $"(event GameTick={death.GameTick})");
        // The message frame sits at ~the true game tick (±frame granularity), NOT shifted by ServerStartTick
        // — so the kill-nav (frame-index based) lands on the kill, unaffected by the feed's tick-field bug.
        await Assert.That(Math.Abs(msgFrameTick - death.GameTick)).IsLessThan(64); // within ~1s of true tick
        if (death.ServerTick != death.GameTick)
        {
            await Assert.That(Math.Abs(msgFrameTick - death.ServerTick)).IsGreaterThan(64); // far from delivery
        }
    }

    [Test]
    public async Task FromEvent_EnrichesPlayerDeath_WithTypedUnquotedFields()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        GameEvent? deathEvent = demo.AllGameEvents.FirstOrDefault(e => e.Name == "player_death");
        if (deathEvent is null)
        {
            throw new SkipTestException("no player_death in demo");
        }

        GameEventView death = GameEventViewFactory.FromEvent(deathEvent);

        // Enriched with the typed fields a kill feed needs (de-stringified from GetDecodedFields()).
        await Assert.That(death.Fields.Count).IsGreaterThan(0);
        await Assert.That(death.Fields.ContainsKey("Attacker")).IsTrue();
        await Assert.That(death.Fields["Attacker"] is int).IsTrue();
        await Assert.That(death.Fields["UserId"] is int).IsTrue();
        await Assert.That(death.Fields["Headshot"] is bool).IsTrue();

        // Weapon is a real, UNQUOTED string (the F() wrapper's surrounding quotes were stripped).
        await Assert.That(death.Fields["Weapon"] is string).IsTrue();
        string weapon = (string)death.Fields["Weapon"]!;
        await Assert.That(weapon.Length).IsGreaterThan(0);
        await Assert.That(weapon.Contains('"')).IsFalse();

        Console.WriteLine($"[kill-feed-part1] player_death killer={death.Fields["Attacker"]} " +
                          $"victim={death.Fields["UserId"]} weapon={weapon} hs={death.Fields["Headshot"]}");
    }
}
