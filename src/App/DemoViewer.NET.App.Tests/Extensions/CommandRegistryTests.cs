#region

using Avalonia.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="CommandRegistry" />: the merged command set a keymap or a settings list reads, core
///     union every pack's commands. Built with a fake pack rather than <see cref="FeaturePacks.Default" />
///     so a collision or a disabled gate proves something without touching the Strat Book's own table
///     (that is <c>StratBookCommandsTests</c>' job).
/// </summary>
public class CommandRegistryTests
{
    [Test]
    public async Task Build_WithNoPacks_IsExactlyTheCoreTable()
    {
        CommandRegistry registry = CommandRegistry.Build([]);

        await Assert.That(registry.EffectiveBindings)
            .IsEquivalentTo(Playback2DKeymap.Default, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(registry.PackCommands).IsEmpty();
        await Assert.That(registry.Conflicts).IsEmpty();
        await Assert.That(registry.PackOwnerByAction).IsEmpty();
    }

    private const string Fake = "net.demoviewer.test.pack.fake.";

    private static readonly Func<IExtension, bool> CompiledIn = _ => false;
    private static readonly Func<IExtension, bool> ThirdParty = _ => true;

    private static IReadOnlyDictionary<string, string> AliasesOf(IExtension pack, IReadOnlyList<CommandDescriptor> _) =>
        pack is FakePack fake ? fake.Aliases : new Dictionary<string, string>();

    [Test]
    public async Task Build_WithAPack_AddsItsChordToEffectiveBindings_AtTheDeclaredScope()
    {
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d",
            new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        FakePack pack = new("pack.fake", [command]);

        CommandRegistry registry = CommandRegistry.Build([pack]);

        await Assert.That(registry.Conflicts).IsEmpty();
        Playback2DBinding added = registry.EffectiveBindings.Single(b => b.ActionId == Fake + "AddStep");
        await Assert.That(added.Key).IsEqualTo(Key.F9);
        await Assert.That(added.Scope).IsEqualTo(Playback2DBindingScope.Always);
        await Assert.That(added.CoreAction).IsEqualTo(Playback2DAction.None);

        PackCommand owner = registry.PackOwnerByAction[Fake + "AddStep"];
        await Assert.That(owner.PackId).IsEqualTo("net.demoviewer.test.pack.fake");
        await Assert.That(owner.PackFeatureId).IsEqualTo("pack.fake");
        await Assert.That(owner.PackLabel).IsEqualTo("Fake pack");
    }

    /// <summary>A core and a pack default chord colliding is REPORTED, not silently resolved either way.</summary>
    [Test]
    public async Task Build_PackChordCollidingWithCore_IsReportedAndDropped_CoreKeepsIt()
    {
        // NextRound ships on bare E, Always. A pack claiming the same chord for a different action must
        // not silently win OR silently lose: it must show up in Conflicts, and core keeps the chord.
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d",
            new KeyGesture(Key.E, KeyModifiers.None), _ => true);
        FakePack pack = new("pack.fake", [command]);

        CommandRegistry registry = CommandRegistry.Build([pack]);

        await Assert.That(registry.Conflicts).IsNotEmpty();
        await Assert.That(registry.Conflicts.Single()).Contains("pack.fake");
        await Assert.That(registry.EffectiveBindings.Single(b => b.Key == Key.E && b.Modifiers == KeyModifiers.None
            && b.Scope == Playback2DBindingScope.Always).CoreAction).IsEqualTo(Playback2DAction.NextRound);
        await Assert.That(registry.EffectiveBindings.Any(b => b.ActionId == Fake + "AddStep")).IsFalse();

        // The dropped row is still a known command: PackOwnerByAction does not forget it just because its
        // default chord lost.
        await Assert.That(registry.PackOwnerByAction.ContainsKey(Fake + "AddStep")).IsTrue();
    }

    [Test]
    public async Task Build_TwoPacksCollidingWithEachOther_ReportsAgainstTheFirst()
    {
        CommandDescriptor first = new("a.AddStep", "a", "playback2d", new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        CommandDescriptor second = new("b.DeleteStep", "b", "playback2d", new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        FakePack packA = new("pack.a", [first]);
        FakePack packB = new("pack.b", [second]);

        CommandRegistry registry = CommandRegistry.Build([packA, packB]);

        await Assert.That(registry.Conflicts).IsNotEmpty();
        await Assert.That(registry.EffectiveBindings.Single(b => b.Key == Key.F9).ActionId)
            .IsEqualTo("a.AddStep").Because("the earlier pack's row already claimed the chord");
    }

    // An id outside the core enum is an ordinary action id; only a collision is refused.
    [Test]
    public async Task Build_ACommandIdOutsideTheCoreEnum_JoinsTheKeymap()
    {
        CommandDescriptor command = new("NotACoreAction", "x", "playback2d", null, _ => true);

        CommandRegistry registry = CommandRegistry.Build([new FakePack("pack.fake", [command])], isThirdParty: CompiledIn);

        await Assert.That(registry.PackOwnerByAction.ContainsKey("NotACoreAction")).IsTrue();
        await Assert.That(registry.ActionIds).Contains("NotACoreAction");
    }

    [Test]
    public void Build_CompiledInCommandRepeatingACoreId_Throws_IgnoringCase()
    {
        CommandDescriptor bad = new("nextround", "x", "playback2d", null, _ => true);

        Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Build([new FakePack("pack.fake", [bad])], isThirdParty: CompiledIn));
    }

    [Test]
    public void Build_TwoCompiledInPacksClaimingOneId_Throws()
    {
        CommandDescriptor a = new("shared.Step", "a", "playback2d", null, _ => true);
        CommandDescriptor b = new("shared.Step", "b", "playback2d", null, _ => true);

        Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Build([new FakePack("pack.a", [a]), new FakePack("pack.b", [b])], isThirdParty: CompiledIn));
    }

    // A third-party extension cannot take the app down with a bad id: the command is reported and left out,
    // and its other commands still join.
    [Test]
    public async Task Build_AThirdPartyCommandNamingACoreId_IsReportedAndDropped()
    {
        CommandDescriptor core = new(Fake + "NextRound", "ok", "playback2d", null, _ => true);
        CommandDescriptor clash = new("NextRound", "clash", "playback2d", new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        CommandDescriptor fine = new(Fake + "Wave", "fine", "playback2d", new KeyGesture(Key.F10, KeyModifiers.None), _ => true);
        FakePack pack = new("pack.fake", [clash, core, fine]);

        CommandRegistry registry = CommandRegistry.Build([pack], isThirdParty: ThirdParty, legacyIds: AliasesOf);

        using (Assert.Multiple())
        {
            await Assert.That(registry.Conflicts.Count).IsEqualTo(1);
            await Assert.That(registry.Conflicts[0]).Contains("'NextRound'");
            await Assert.That(registry.EffectiveBindings.Any(b => b.Key == Key.F9)).IsFalse();
            await Assert.That(registry.PackOwnerByAction.Keys).IsEquivalentTo([Fake + "NextRound", Fake + "Wave"]);
        }
    }

    [Test]
    public async Task Build_AThirdPartyCommandWithoutTheExtensionPrefix_IsReportedAndDropped()
    {
        CommandDescriptor bare = new("Wave", "bare", "playback2d", new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        CommandDescriptor other = new("net.demoviewer.test.pack.other.Wave", "someone else's", "playback2d", null, _ => true);

        CommandRegistry registry = CommandRegistry.Build([new FakePack("pack.fake", [bare, other])], isThirdParty: ThirdParty);

        using (Assert.Multiple())
        {
            await Assert.That(registry.Conflicts.Count).IsEqualTo(2);
            await Assert.That(registry.Conflicts.All(c => c.Contains("is not prefixed with 'net.demoviewer.test.pack.fake.'"))).IsTrue();
            await Assert.That(registry.PackCommands).IsEmpty();
        }
    }

    [Test]
    public async Task Build_AThirdPartyCommandRepeatingAnIdInAnotherCase_IsDropped()
    {
        CommandDescriptor mine = new("net.demoviewer.test.pack.a.Step", "a", "playback2d", null, _ => true);
        FakePack compiledIn = new("pack.a", [mine]);
        FakePack thirdParty = new("pack.b", [new CommandDescriptor("net.demoviewer.test.pack.b.Step", "b", "playback2d", null, _ => true),
            new CommandDescriptor("NET.DEMOVIEWER.TEST.PACK.B.STEP", "b again", "playback2d", null, _ => true)]);

        CommandRegistry registry = CommandRegistry.Build([compiledIn, thirdParty], isThirdParty: p => p == thirdParty);

        await Assert.That(registry.Conflicts.Count).IsEqualTo(1);
        await Assert.That(registry.PackCommands.Select(c => c.Command.Label)).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Build_ADeclaredScope_IsTheRowsScope_AndSettingsReadsItsLabel()
    {
        CommandScope panel = new(Fake + "panel", "while the fake panel has focus");
        CommandDescriptor command = new(Fake + "Poke", "poke", panel.Id, new KeyGesture(Key.P, KeyModifiers.None), _ => true);

        CommandRegistry registry = CommandRegistry.Build([new FakePack("pack.fake", [command], [panel])], isThirdParty: ThirdParty);

        Playback2DBinding row = registry.EffectiveBindings.Single(b => b.ActionId == command.Id);
        using (Assert.Multiple())
        {
            await Assert.That(registry.Conflicts).IsEmpty();
            await Assert.That(row.Scope).IsEqualTo(new Playback2DBindingScope(panel.Id));
            await Assert.That(row.Scope.IsCore).IsFalse();
            await Assert.That(registry.ScopeLabel(row.Scope)).IsEqualTo(panel.Label);
            await Assert.That(registry.ScopeLabel(Playback2DBindingScope.Always)).IsEqualTo("always");
            await Assert.That(registry.ScopeLabel(Playback2DBindingScope.WhenToolActive)).IsEqualTo("while drawing");
        }
    }

    [Test]
    public async Task Build_AnUndeclaredScope_FailsACompiledInPack_AndDropsAThirdPartyCommand()
    {
        CommandDescriptor command = new(Fake + "Poke", "poke", Fake + "panel", null, _ => true);

        Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Build([new FakePack("pack.fake", [command])], isThirdParty: CompiledIn));

        CommandRegistry registry = CommandRegistry.Build([new FakePack("pack.fake", [command])], isThirdParty: ThirdParty);
        await Assert.That(registry.PackCommands).IsEmpty();
        await Assert.That(registry.Conflicts.Single()).Contains("which it does not declare");
    }

    [Test]
    public void Build_AScopeWithoutTheExtensionPrefix_FailsACompiledInPack()
    {
        CommandScope bare = new("palette", "while tagging");

        Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Build([new FakePack("pack.fake", [], [bare])], isThirdParty: CompiledIn));
    }

    [Test]
    public async Task Aliases_ResolveAnOldId_IgnoringCase_ToTheCurrentOne()
    {
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d", null, _ => true);
        FakePack pack = new("pack.fake", [command], aliases: new Dictionary<string, string> { ["AddStep"] = command.Id });

        CommandRegistry registry = CommandRegistry.Build([pack], isThirdParty: CompiledIn, legacyIds: AliasesOf);

        using (Assert.Multiple())
        {
            await Assert.That(registry.Canonical("addstep")).IsEqualTo(command.Id);
            await Assert.That(registry.Canonical(command.Id.ToUpperInvariant())).IsEqualTo(command.Id);
            await Assert.That(registry.Canonical("nextround")).IsEqualTo("NextRound");
            await Assert.That(registry.Canonical("Nothing")).IsNull();
        }
    }

    [Test]
    public async Task Aliases_AreNeverReadFromAThirdPartyExtension()
    {
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d", null, _ => true);
        FakePack pack = new("pack.fake", [command], aliases: new Dictionary<string, string> { ["AddStep"] = command.Id });

        CommandRegistry registry = CommandRegistry.Build([pack], isThirdParty: ThirdParty, legacyIds: AliasesOf);

        await Assert.That(registry.Aliases).IsEmpty();
    }

    [Test]
    public async Task LegacyIds_MapTheStratBooksBareIds_AndNoOtherExtensions()
    {
        CommandDescriptor step = new("net.demoviewer.pack.stratbook.AddStep", "insert a step", "playback2d", null, _ => true);
        IExtension stratBook = new IdPack("net.demoviewer.pack.stratbook");
        IExtension other = new IdPack("net.demoviewer.test.other");

        using (Assert.Multiple())
        {
            await Assert.That(LegacyCommandIds.For(stratBook, [step])["AddStep"]).IsEqualTo(step.Id);
            await Assert.That(LegacyCommandIds.For(stratBook, [step]).Count).IsEqualTo(1);
            await Assert.That(LegacyCommandIds.For(other, [step])).IsEmpty();
        }
    }

    [Test]
    public void Aliases_ShadowingACoreId_OrNamingAnotherPacksCommand_Throw()
    {
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d", null, _ => true);
        FakePack shadow = new("pack.fake", [command], aliases: new Dictionary<string, string> { ["NextRound"] = command.Id });
        Assert.Throws<InvalidOperationException>(() => CommandRegistry.Build([shadow], isThirdParty: CompiledIn, legacyIds: AliasesOf));

        CommandDescriptor theirs = new("net.demoviewer.test.pack.b.Step", "b", "playback2d", null, _ => true);
        FakePack other = new("pack.b", [theirs]);
        FakePack poacher = new("pack.fake", [command], aliases: new Dictionary<string, string> { ["Step"] = theirs.Id });
        Assert.Throws<InvalidOperationException>(() => CommandRegistry.Build([other, poacher], isThirdParty: CompiledIn, legacyIds: AliasesOf));
    }

    [Test]
    public async Task ActionIds_AreEveryCoreId_ThenEveryPackCommand_AndNeverNone()
    {
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d", null, _ => true);

        CommandRegistry registry = CommandRegistry.Build([new FakePack("pack.fake", [command])]);

        using (Assert.Multiple())
        {
            await Assert.That(registry.ActionIds).Contains(nameof(Playback2DAction.TogglePlay));
            await Assert.That(registry.ActionIds).Contains(Fake + "AddStep");
            await Assert.That(registry.ActionIds).DoesNotContain(nameof(Playback2DAction.None));
            await Assert.That(registry.ActionIds[^1]).IsEqualTo(Fake + "AddStep");
        }
    }

    [Test]
    public async Task TryResolve_PackOff_ResolvesToNothing_PackOn_ResolvesTheCommand()
    {
        CommandDescriptor command = new(Fake + "AddStep", "insert a step", "playback2d",
            new KeyGesture(Key.F9, KeyModifiers.None), ctx => ctx.Target is string);
        FakePack pack = new("pack.fake", [command]);
        CommandRegistry registry = CommandRegistry.Build([pack]);

        bool off = registry.TryResolve(Key.F9, KeyModifiers.None, "playback2d", _ => false, out CommandDescriptor? whenOff);
        await Assert.That(off).IsFalse();
        await Assert.That(whenOff).IsNull();

        bool on = registry.TryResolve(Key.F9, KeyModifiers.None, "playback2d", _ => true, out CommandDescriptor? whenOn);
        await Assert.That(on).IsTrue();
        await Assert.That(whenOn!.Id).IsEqualTo(Fake + "AddStep");
    }

    [Test]
    public async Task TryResolve_CoreChord_AlwaysResolves_RegardlessOfTheGatePredicate()
    {
        CommandRegistry registry = CommandRegistry.Build([]);

        bool resolved = registry.TryResolve(Key.E, KeyModifiers.None, "playback2d", _ => false, out CommandDescriptor? command);

        await Assert.That(resolved).IsTrue();
        await Assert.That(command!.Id).IsEqualTo(nameof(Playback2DAction.NextRound));
    }

    [Test]
    public async Task TryResolve_ReservedCoreRow_DoesNotResolve()
    {
        CommandRegistry registry = CommandRegistry.Build([]);

        bool resolved = registry.TryResolve(Key.Home, KeyModifiers.None, "playback2d", _ => true, out CommandDescriptor? command);

        await Assert.That(resolved).IsFalse();
        await Assert.That(command).IsNull();
    }

    [Test]
    public async Task CoreCommand_Run_ReturnsFalse_ForATargetThatIsNotTheTabViewModel()
    {
        CommandRegistry registry = CommandRegistry.Build([]);
        registry.TryResolve(Key.E, KeyModifiers.None, "playback2d", _ => true, out CommandDescriptor? command);

        // Proves Run is wired (not a stub returning true unconditionally) without pulling in the headless
        // UI harness: a mismatched target is exactly what a resolved core command against, say, the
        // Strat canvas VM would be, and it must come back false rather than throw.
        bool ran = command!.Run(new CommandContext(new object()));

        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task TheCoreScopes_KeepTheirCommandScopeSpellings()
    {
        await Assert.That(Playback2DBindingScope.Always.Name).IsEqualTo("playback2d");
        await Assert.That(Playback2DBindingScope.WhenToolActive.Name).IsEqualTo("playback2d.tool");
        await Assert.That(Playback2DKeymap.Default.All(b => b.Scope.IsCore)).IsTrue();
    }

    // A pack whose Commands getter throws is left out of the keymap and reported; the core table and the
    // other packs compose as before. With no fault callback the throw still escapes, as the loader expects.
    [Test]
    public async Task APackWhoseCommandsGetterThrows_IsLeftOut_AndReported()
    {
        List<string> reported = [];
        CommandRegistry registry = CommandRegistry.Build([new ThrowingCommands()], (pack, _) => reported.Add(pack.Id));

        using (Assert.Multiple())
        {
            await Assert.That(reported).IsEquivalentTo(["net.demoviewer.test.throwing"]);
            await Assert.That(registry.PackCommands).IsEmpty();
            await Assert.That(registry.EffectiveBindings.Count).IsGreaterThan(0);
        }

        Assert.Throws<InvalidOperationException>(() => CommandRegistry.Build([new ThrowingCommands()]));
    }

    private sealed class ThrowingCommands : IExtension
    {
        public string Id => "net.demoviewer.test.throwing";
        public string FeatureId => "pack.throwing";
        public IEnumerable<ExtensionFeature> Features => [];
        public IEnumerable<CommandDescriptor> Commands => throw new InvalidOperationException("commands");

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    private sealed class IdPack(string id) : IExtension
    {
        public string Id => id;
        public string FeatureId => "pack.id";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    private sealed class FakePack(string featureId, CommandDescriptor[] commands, CommandScope[]? scopes = null,
        Dictionary<string, string>? aliases = null) : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.test." + featureId;
        public string FeatureId => featureId;
        public ExtensionManifest Manifest => FakeManifests.For(Id);

        public IEnumerable<ExtensionFeature> Features =>
        [
            new(featureId, ExtensionFeatureKind.Extension, "Fake pack", "a test pack", null, AudienceDefaults.Everyone)
        ];

        public IEnumerable<CommandDescriptor> Commands => commands;

        public IEnumerable<CommandScope> CommandScopes => scopes ?? [];

        public IReadOnlyDictionary<string, string> Aliases => aliases ?? [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
