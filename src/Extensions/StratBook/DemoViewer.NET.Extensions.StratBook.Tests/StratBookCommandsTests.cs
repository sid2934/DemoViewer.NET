#region

using Avalonia.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The Strat Book extension's 22 keymap commands, declared in <see cref="StratBookCommands" /> and
///     read by <c>CommandRegistry</c> through <c>IExtension.Commands</c>. Pins each one's gesture and
///     scope, so a diff here is a deliberate rebind, not a refactor accident.
/// </summary>
[NotInParallel]
public class StratBookCommandsTests
{
    // One row per Strat Book extension action: (action, key, modifiers, scope string, label).
    private static readonly (Playback2DAction Action, Key Key, KeyModifiers Modifiers, string Scope, string Label)[]
        _expected =
        [
            (Playback2DAction.FindRoundsLikeThis, Key.F, KeyModifiers.Control, "playback2d",
                "Find rounds like this: snapshot the alive players onto the Situations query"),
            (Playback2DAction.NextSituationResult, Key.J, KeyModifiers.None, "playback2d",
                "Next situation result: seek to the next card of the Situations search"),
            (Playback2DAction.PrevSituationResult, Key.K, KeyModifiers.None, "playback2d",
                "Previous situation result: seek to the previous card of the Situations search"),
            (Playback2DAction.FocusTagPalette, Key.C, KeyModifiers.None, "playback2d",
                "Tag palette: focus it so its hotkeys tag (press again to leave)"),
            (Playback2DAction.TagPaletteBack, Key.Escape, KeyModifiers.None, "playback2d.palette",
                "Tag palette: finish the tag and go back to the codes, or leave the palette"),
            (Playback2DAction.TagNote, Key.M, KeyModifiers.Control, "playback2d.palette",
                "Tag palette: write a note on the tag just made"),
            (Playback2DAction.TagClearSticky, Key.Back, KeyModifiers.Control, "playback2d.palette",
                "Tag palette: clear the sticky labels"),
            (Playback2DAction.TagLabelMode, Key.L, KeyModifiers.Control, "playback2d.palette",
                "Tag palette: label mode, adding labels to the tag under the playhead instead of making tags"),
            (Playback2DAction.TagLabelGroupNext, Key.G, KeyModifiers.Control, "playback2d.palette",
                "Tag palette: in label mode, show the next label group"),
            (Playback2DAction.SuggestionNext, Key.J, KeyModifiers.None, "playback2d.suggestion",
                "Suggested tags: select the next pending proposal and seek to it"),
            (Playback2DAction.SuggestionPrev, Key.K, KeyModifiers.None, "playback2d.suggestion",
                "Suggested tags: select the previous pending proposal and seek to it"),
            (Playback2DAction.SuggestionAccept, Key.Y, KeyModifiers.None, "playback2d",
                "Suggested tags: accept the selected proposal as a tag and go to the next"),
            (Playback2DAction.SuggestionReject, Key.N, KeyModifiers.None, "playback2d",
                "Suggested tags: reject the selected proposal and go to the next"),
            (Playback2DAction.SuggestionEdit, Key.Enter, KeyModifiers.None, "playback2d",
                "Suggested tags: edit the selected proposal before accepting it"),
            (Playback2DAction.SuggestionAcceptAll, Key.Y, KeyModifiers.Control, "playback2d",
                "Suggested tags: accept every pending proposal the queue's filter shows, after a confirm"),
            (Playback2DAction.ToggleReviewMode, Key.R, KeyModifiers.Shift, "playback2d",
                "Review mode: show the tag palette, the suggestions and the tag lanes, or hide them"),
            (Playback2DAction.ToolToken, Key.V, KeyModifiers.None, "playback2d",
                "Strat canvas: token tool (press again for pan)"),
            (Playback2DAction.AddStep, Key.N, KeyModifiers.Shift, "playback2d",
                "Strat canvas: insert a step after the active one at the playhead's round-clock time"),
            (Playback2DAction.DuplicateStep, Key.D, KeyModifiers.Control, "playback2d",
                "Strat canvas: copy the active step's positions and strokes into a new step 5 s later"),
            (Playback2DAction.DeleteStep, Key.Delete, KeyModifiers.Control, "playback2d",
                "Strat canvas: delete the active step"),
            (Playback2DAction.PrevStep, Key.OemOpenBrackets, KeyModifiers.None, "playback2d",
                "Strat canvas: seek to the previous step and make it active"),
            (Playback2DAction.NextStep, Key.OemCloseBrackets, KeyModifiers.None, "playback2d",
                "Strat canvas: seek to the next step and make it active")
        ];

    [Test]
    public async Task AllCommands_MatchTheShippedTableBeforeTheMove_IdLabelScopeAndChord()
    {
        await Assert.That(StratBookCommands.All.Count).IsEqualTo(_expected.Length);

        foreach ((Playback2DAction action, Key key, KeyModifiers modifiers, string scope, string label) in _expected)
        {
            CommandDescriptor command = StratBookCommands.All.Single(c => c.Id == action.ToString());
            using (Assert.Multiple())
            {
                await Assert.That(command.Label).IsEqualTo(label);
                await Assert.That(command.Scope).IsEqualTo(scope);
                await Assert.That(command.DefaultChord).IsNotNull();
                await Assert.That(command.DefaultChord!.Key).IsEqualTo(key);
                await Assert.That(command.DefaultChord!.KeyModifiers).IsEqualTo(modifiers);
            }
        }
    }

    [Test]
    public async Task Playback2DKeymap_NoLongerDeclaresTheMovedActions()
    {
        foreach ((Playback2DAction action, _, _, _, _) in _expected)
        {
            await Assert.That(Playback2DKeymap.Default.Any(b => b.Action == action)).IsFalse()
                .Because($"{action} moved to StratBookCommands; Playback2DKeymap's own table must not carry it twice");
        }
    }

    /// <summary>
    ///     With the pack compiled in (every real build), <c>CommandRegistry.Default</c> resolves every
    ///     one of these 22 actions to its chord and scope, with no conflicts.
    /// </summary>
    [Test]
    public async Task CommandRegistry_Default_ComposesThePackBackIn_WithNoConflicts()
    {
        await Assert.That(CommandRegistry.Default.Conflicts).IsEmpty();

        foreach ((Playback2DAction action, Key key, KeyModifiers modifiers, string scope, _) in _expected)
        {
            bool resolved = CommandRegistry.Default.TryResolve(key, modifiers, scope, _ => true,
                out CommandDescriptor? command);
            await Assert.That(resolved).IsTrue().Because($"{action} should resolve with the pack on");
            await Assert.That(command!.Id).IsEqualTo(action.ToString());

            PackCommand owner = CommandRegistry.Default.PackOwnerByAction[action];
            await Assert.That(owner.PackId).IsEqualTo("net.demoviewer.pack.stratbook");
            await Assert.That(owner.PackFeatureId).IsEqualTo(StratBookPack.PackFeatureId);
            await Assert.That(owner.PackLabel).IsEqualTo("Strat Book extension");
        }
    }

    /// <summary>
    ///     Pack off: the same chord resolves to nothing through the gate-aware API. The legacy
    ///     <c>Playback2DKeymapProfile</c> path a running tab actually uses does not gate (no live wiring
    ///     reaches it; see its own file), so this is the registry's own contract, not the view's.
    /// </summary>
    [Test]
    public async Task CommandRegistry_Default_TryResolve_PackOff_ResolvesToNothing()
    {
        foreach ((Playback2DAction _, Key key, KeyModifiers modifiers, string scope, _) in _expected)
        {
            bool resolved = CommandRegistry.Default.TryResolve(key, modifiers, scope, _ => false,
                out CommandDescriptor? command);
            await Assert.That(resolved).IsFalse();
            await Assert.That(command).IsNull();
        }
    }

    /// <summary>
    ///     <c>Playback2DKeymapProfile</c>, the profile a running 2D Playback tab and Strat canvas actually
    ///     route keys through, still resolves every moved action to the same chord and scope as before the
    ///     move: "pack on behaviour is identical" is a property of this type, not just the
    ///     registry's own data.
    /// </summary>
    [Test]
    public async Task Playback2DKeymapProfile_Default_StillResolvesEveryMovedAction()
    {
        foreach ((Playback2DAction action, Key key, KeyModifiers modifiers, string scope, _) in _expected)
        {
            Playback2DBindingScope expectedScope = CommandRegistry.ParseScope(scope);
            bool toolActive = expectedScope == Playback2DBindingScope.WhenToolActive;

            bool resolved = expectedScope == Playback2DBindingScope.Always
                ? Playback2DKeymapProfile.Default.TryResolve(key, modifiers, toolActive, out Playback2DAction resolvedAlways)
                  && resolvedAlways == action
                : Playback2DKeymapProfile.Default.TryResolveInScope(expectedScope, key, modifiers, out Playback2DAction resolvedScoped)
                  && resolvedScoped == action;

            await Assert.That(resolved).IsTrue().Because($"{action} should still resolve through the profile a tab routes through");
            await Assert.That(Playback2DKeymapProfile.Default.GestureText(action))
                .IsEqualTo(Playback2DKeymap.Format(key, modifiers));
        }
    }

    /// <summary>A persisted override for a moved action still applies: the id did not change, so neither does the row it replaces.</summary>
    [Test]
    public async Task PersistedOverride_ForAMovedAction_StillApplies()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(
            ["TagNote=Ctrl+Shift+M"], out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsEmpty();
        await Assert.That(profile.GestureText(Playback2DAction.TagNote)).IsEqualTo("Ctrl+Shift+M");
        await Assert.That(profile.IsOverridden(Playback2DAction.TagNote)).IsTrue();
    }

    /// <summary>
    ///     A core rebind refused because a pack row already holds the gesture names the owning pack, so
    ///     the refusal means something even while that row is hidden (the Strat Book extension off and
    ///     its keybind row gone from the list).
    /// </summary>
    [Test]
    public async Task CoreRebind_RefusedByAPackRow_NamesTheOwningPackInTheMessage()
    {
        Playback2DKeymapProfile.FromOverrides(["NextRound=Ctrl+F"], out IReadOnlyList<string> rejected);

        await Assert.That(rejected).IsNotEmpty();
        await Assert.That(rejected.Single()).Contains("FindRoundsLikeThis (Strat Book extension)");
    }

    /// <summary>The keybind settings list: a moved action's row carries the pack's label and is gated by its switch.</summary>
    [Test]
    public async Task SettingsKeybindList_PackOff_HidesMovedRows_PackOn_LabelsThem()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            services.AddSingleton<IFeatureGate>(s =>
                new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
            using ServiceProvider sp = services.BuildServiceProvider();

            SettingsViewModel vm = new(svc, sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
                sp.GetRequiredService<IFeatureGate>(), new ThemeRegistry());

            KeybindRow movedOff = vm.Playback2DKeybindRows.Single(r => r.Action == Playback2DAction.TagNote);
            KeybindRow coreRow = vm.Playback2DKeybindRows.Single(r => r.Action == Playback2DAction.NextRound);
            using (Assert.Multiple())
            {
                await Assert.That(movedOff.IsVisible).IsFalse();
                await Assert.That(movedOff.PackLabel).IsEqualTo("Strat Book extension");
                await Assert.That(coreRow.IsVisible).IsTrue();
                await Assert.That(coreRow.PackLabel).IsNull();
            }

            svc.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = true);
            await Assert.That(movedOff.IsVisible).IsTrue()
                .Because("IFeatureGate.Changed must refresh the keybind rows live, same as the Features list");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvstratcmds_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
