#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The user-facing keymap (D1). <see cref="Playback2DKeymap" /> stays a compile-time contract whose
///     static constructor throws on a bad table; <see cref="Playback2DKeymapProfile" /> is the one that
///     composes a hand-editable settings file over it, and its whole reason to exist is that it must
///     <b>never</b> throw. Every case below is therefore a bad row that has to be dropped, reported, and
///     survived — with everything else still resolving.
/// </summary>
public class Playback2DKeymapProfileTests
{
    [Test]
    public async Task NoOverrides_ResolvesExactlyTheShippedTable()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides([],
            out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty();
        await Assert.That(profile.Bindings).IsEquivalentTo(Playback2DKeymap.Default);
        await Assert.That(profile.IsOverridden(Playback2DAction.NextRound)).IsFalse();
    }

    [Test]
    public async Task ValidOverride_MovesOneActionAndLeavesEverythingElseAlone()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(["NextRound=Shift+R"],
            out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty();
        await Assert.That(Resolve(profile, Key.R, KeyModifiers.Shift))
            .IsEqualTo(Playback2DAction.NextRound);
        await Assert.That(profile.GestureText(Playback2DAction.NextRound)).IsEqualTo("Shift+R");
        await Assert.That(profile.IsOverridden(Playback2DAction.NextRound)).IsTrue();

        // The vacated key really is vacated — an override that only ADDS a gesture would pass a
        // "Shift+R works" test while leaving two keys doing the same thing.
        await Assert.That(profile.TryResolve(Key.E, KeyModifiers.None, false, out _)).IsFalse();

        // …and nothing else moved.
        await Assert.That(Resolve(profile, Key.Q, KeyModifiers.None)).IsEqualTo(Playback2DAction.PrevRound);
        await Assert.That(Resolve(profile, Key.Space, KeyModifiers.None))
            .IsEqualTo(Playback2DAction.TogglePlay);
        await Assert.That(Resolve(profile, Key.Z, KeyModifiers.Control | KeyModifiers.Shift))
            .IsEqualTo(Playback2DAction.Redo);
        await Assert.That(profile.IsOverridden(Playback2DAction.PrevRound)).IsFalse();
    }

    /// <summary>
    ///     The five ways a settings file can be wrong, in one array. Each is dropped and named, and the
    ///     surviving profile is still the complete shipped table — the failure mode this type exists to
    ///     prevent is a bad row taking the tab's keyboard with it.
    /// </summary>
    [Test]
    public async Task EveryKindOfBadRow_IsDroppedAndReported_AndTheRestStillResolves()
    {
        string[] rows =
        [
            "NextRoundShift+R",   // malformed — no '='
            "NextRound=Bogus",    // unparseable gesture
            "Teleport=Y",         // unknown action
            "NextRound=Ctrl+O",   // shell accelerator (MainView.axaml's Open)
            "NextRound=D"         // duplicate within the Always scope (ToolDraw already has D)
        ];

        Playback2DKeymapProfile profile =
            Playback2DKeymapProfile.FromOverrides(rows, out IReadOnlyList<string> rejected);

        Console.WriteLine("[keymap-profile] " + string.Join(" | ", rejected));
        await Assert.That(rejected.Count).IsEqualTo(5)
            .Because("each bad row must be reported on its own — a single 'the file is bad' is unfixable");

        foreach (string row in rows)
        {
            await Assert.That(rejected.Any(r => r.StartsWith(row, StringComparison.Ordinal))).IsTrue()
                .Because($"the report has to name the offending row: {row}");
        }

        await Assert.That(profile.Rejected).IsEquivalentTo(rejected);
        await Assert.That(profile.Bindings).IsEquivalentTo(Playback2DKeymap.Default)
            .Because("no row survived, so the profile is the shipped table exactly");

        // Every shipped gesture still resolves — including the one all five rows were aiming at.
        await Assert.That(Resolve(profile, Key.E, KeyModifiers.None)).IsEqualTo(Playback2DAction.NextRound);
        await Assert.That(Resolve(profile, Key.D, KeyModifiers.None)).IsEqualTo(Playback2DAction.ToolDraw);
        await Assert.That(Resolve(profile, Key.X, KeyModifiers.Control))
            .IsEqualTo(Playback2DAction.ClearAnnotations);
    }

    /// <summary>
    ///     One good row among the bad ones still lands. A validator that gave up on the whole file at the
    ///     first bad row would pass the case above and lose the user's real rebind here.
    /// </summary>
    [Test]
    public async Task AGoodRowSurvivesABadNeighbour()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(
            ["Teleport=Y", "NextRound=Shift+R", "NextRound=Ctrl+O"],
            out IReadOnlyList<string> rejected);

        await Assert.That(rejected.Count).IsEqualTo(2);
        await Assert.That(Resolve(profile, Key.R, KeyModifiers.Shift))
            .IsEqualTo(Playback2DAction.NextRound);
    }

    [Test]
    public async Task BlankRows_AreSkippedSilently()
    {
        _ = Playback2DKeymapProfile.FromOverrides(["", "   ", "\t"], out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty()
            .Because("an empty index is a shrunk array, not a mistake — reporting it buries the real one");
    }

    /// <summary>
    ///     A RESERVED binding (today <c>Home</c> / fit camera) is declared so the conflict checker guards
    ///     its gesture. It stays unroutable, it cannot be rebound, and nothing else may claim its key.
    /// </summary>
    [Test]
    public async Task ReservedBinding_StaysUnroutableAndUnbindable()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(
            ["FitCamera=G", "TogglePlay=Home"], out IReadOnlyList<string> rejected);

        await Assert.That(rejected.Count).IsEqualTo(2);
        await Assert.That(rejected[0]).Contains("reserved");

        await Assert.That(profile.TryResolve(Key.Home, KeyModifiers.None, false, out Playback2DAction fit))
            .IsFalse();
        await Assert.That(fit).IsEqualTo(Playback2DAction.None);
        await Assert.That(profile.TryResolve(Key.G, KeyModifiers.None, false, out _)).IsFalse();
        await Assert.That(Resolve(profile, Key.Space, KeyModifiers.None))
            .IsEqualTo(Playback2DAction.TogglePlay);
    }

    /// <summary>
    ///     The tool-scoped shadowing rule is what makes Space hold-to-pan and Esc cancel-the-gesture while
    ///     the pen is out. It has to survive a rebind — otherwise rebinding hold-pan silently turns the
    ///     drawing surface's most-used key back into play/pause.
    /// </summary>
    [Test]
    public async Task ToolScopedShadowing_SurvivesARebind()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(["HoldPan=B"],
            out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty();

        await Assert.That(profile.TryResolve(Key.B, KeyModifiers.None, true, out Playback2DAction drawing))
            .IsTrue();
        await Assert.That(drawing).IsEqualTo(Playback2DAction.HoldPan);

        // Tool-scoped means tool-scoped: B does nothing with no tool selected.
        await Assert.That(profile.TryResolve(Key.B, KeyModifiers.None, false, out _)).IsFalse();

        // Space is no longer shadowed, so it is play/pause even under the pen — the user asked for that.
        await Assert.That(Resolve(profile, Key.Space, KeyModifiers.None, true))
            .IsEqualTo(Playback2DAction.TogglePlay);

        // Esc's own shadow is untouched.
        await Assert.That(Resolve(profile, Key.Escape, KeyModifiers.None, true))
            .IsEqualTo(Playback2DAction.CancelGesture);
        await Assert.That(Resolve(profile, Key.Escape, KeyModifiers.None))
            .IsEqualTo(Playback2DAction.ClearFollow);
    }

    /// <summary>
    ///     A tool-scoped binding may share a key with an always-scoped one — that is the whole shadowing
    ///     mechanism — so the duplicate check must be per SCOPE, not per key.
    /// </summary>
    [Test]
    public async Task ARebindMayReuseAKeyFromTheOtherScope()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(["HoldPan=F"],
            out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty();
        await Assert.That(Resolve(profile, Key.F, KeyModifiers.None, true)).IsEqualTo(Playback2DAction.HoldPan);
        await Assert.That(Resolve(profile, Key.F, KeyModifiers.None))
            .IsEqualTo(Playback2DAction.CycleFollowNext);
    }

    /// <summary>
    ///     Exchanging two keys is only ever clean as a SET: row-by-row, the first half collides with the
    ///     second half's not-yet-replaced default. The batch pass is what makes a swap expressible at all.
    /// </summary>
    [Test]
    public async Task SwappingTwoBindings_IsAcceptedAsABatch()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(
            ["PrevRound=E", "NextRound=Q"], out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty();
        await Assert.That(Resolve(profile, Key.E, KeyModifiers.None)).IsEqualTo(Playback2DAction.PrevRound);
        await Assert.That(Resolve(profile, Key.Q, KeyModifiers.None)).IsEqualTo(Playback2DAction.NextRound);
    }

    [Test]
    public async Task TheSameActionTwice_KeepsTheFirstRowAndReportsTheSecond()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(
            ["NextRound=Shift+R", "NextRound=Shift+T"], out IReadOnlyList<string> rejected);

        await Assert.That(rejected.Count).IsEqualTo(1);
        await Assert.That(Resolve(profile, Key.R, KeyModifiers.Shift)).IsEqualTo(Playback2DAction.NextRound);
    }

    /// <summary>
    ///     <see cref="Playback2DKeymapProfile.Row" /> writes what the loader reads. The display formatter
    ///     is NOT that — it spells arrows "←" and Escape "Esc" — so persisting display text would lose
    ///     every arrow-key rebind on the next launch. Feeding the whole shipped table back through both
    ///     ends proves the writer and the parser agree, for every key shape the table contains.
    /// </summary>
    [Test]
    public async Task EveryShippedGesture_RoundTripsThroughItsPersistedRow()
    {
        string[] rows =
        [
            .. Playback2DKeymap.Default.Where(b => !b.IsReserved)
                .Select(b => Playback2DKeymapProfile.Row(b.Action, b.Key, b.Modifiers))
        ];

        Playback2DKeymapProfile profile =
            Playback2DKeymapProfile.FromOverrides(rows, out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty()
            .Because("a row this type wrote must be a row this type can read: " + string.Join(",", rows));
        await Assert.That(profile.Bindings).IsEquivalentTo(Playback2DKeymap.Default);
    }

    [Test]
    public async Task Row_UsesParseableTokens_NotDisplayText()
    {
        await Assert.That(Playback2DKeymapProfile.Row(Playback2DAction.StepBack, Key.Left, KeyModifiers.None))
            .IsEqualTo("StepBack=Left");
        await Assert.That(Playback2DKeymapProfile.Row(Playback2DAction.Redo, Key.Z,
            KeyModifiers.Control | KeyModifiers.Shift)).IsEqualTo("Redo=Ctrl+Shift+Z");

        // …while the DISPLAY text stays the human one.
        await Assert.That(Playback2DKeymapProfile.Default.GestureText(Playback2DAction.StepBack))
            .IsEqualTo("←");
    }

    /// <summary>
    ///     The pre-flight the Settings rebind affordance runs. It must reject for the same reasons the
    ///     loader would, so a rebind is never accepted by the UI and then dropped on the next launch.
    /// </summary>
    [Test]
    public async Task ValidateOverride_AnswersForTheCandidateOnly()
    {
        await Assert.That(Playback2DKeymapProfile.ValidateOverride([], "NextRound=Shift+R")).IsEqualTo("");
        await Assert.That(Playback2DKeymapProfile.ValidateOverride([], "NextRound=Ctrl+O"))
            .Contains("app-wide");
        await Assert.That(Playback2DKeymapProfile.ValidateOverride([], "NextRound=D")).IsNotEmpty();
        await Assert.That(Playback2DKeymapProfile.ValidateOverride([], "FitCamera=G")).Contains("reserved");

        // Re-binding an action that is ALREADY overridden replaces its row rather than colliding with it.
        await Assert.That(Playback2DKeymapProfile.ValidateOverride(["NextRound=Shift+R"], "NextRound=Shift+T"))
            .IsEqualTo("");

        // …but it still has to clear everyone else's.
        await Assert.That(Playback2DKeymapProfile.ValidateOverride(["PrevRound=Shift+R"], "NextRound=Shift+R"))
            .IsNotEmpty();
    }

    [Test]
    public async Task BindingFor_IsTheKeyUpHandlersSource()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(["HoldPan=B"], out _);

        await Assert.That(profile.BindingFor(Playback2DAction.HoldPan)!.Value.Key).IsEqualTo(Key.B);
        await Assert.That(Playback2DKeymapProfile.Default.BindingFor(Playback2DAction.HoldPan)!.Value.Key)
            .IsEqualTo(Key.Space);
        await Assert.That(profile.BindingFor(Playback2DAction.None)).IsNull();
    }

    private static Playback2DAction Resolve(Playback2DKeymapProfile profile, Key key,
        KeyModifiers modifiers, bool toolActive = false)
    {
        profile.TryResolve(key, modifiers, toolActive, out Playback2DAction action);
        return action;
    }
}
