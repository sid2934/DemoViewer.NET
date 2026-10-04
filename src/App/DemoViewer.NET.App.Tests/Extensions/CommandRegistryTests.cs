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

    [Test]
    public async Task Build_WithAPack_AddsItsChordToEffectiveBindings_AtTheDeclaredScope()
    {
        CommandDescriptor command = new("AddStep", "insert a step", "playback2d",
            new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        FakePack pack = new("pack.fake", [command]);

        CommandRegistry registry = CommandRegistry.Build([pack]);

        await Assert.That(registry.Conflicts).IsEmpty();
        Playback2DBinding added = registry.EffectiveBindings.Single(b => b.Action == Playback2DAction.AddStep);
        await Assert.That(added.Key).IsEqualTo(Key.F9);
        await Assert.That(added.Scope).IsEqualTo(Playback2DBindingScope.Always);

        PackCommand owner = registry.PackOwnerByAction[Playback2DAction.AddStep];
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
        CommandDescriptor command = new("AddStep", "insert a step", "playback2d",
            new KeyGesture(Key.E, KeyModifiers.None), _ => true);
        FakePack pack = new("pack.fake", [command]);

        CommandRegistry registry = CommandRegistry.Build([pack]);

        await Assert.That(registry.Conflicts).IsNotEmpty();
        await Assert.That(registry.Conflicts.Single()).Contains("pack.fake");
        await Assert.That(registry.EffectiveBindings.Single(b => b.Key == Key.E && b.Modifiers == KeyModifiers.None
            && b.Scope == Playback2DBindingScope.Always).Action).IsEqualTo(Playback2DAction.NextRound);
        await Assert.That(registry.EffectiveBindings.Any(b => b.Action == Playback2DAction.AddStep)).IsFalse();

        // The dropped row is still a known command: PackOwnerByAction does not forget it just because its
        // default chord lost.
        await Assert.That(registry.PackOwnerByAction.ContainsKey(Playback2DAction.AddStep)).IsTrue();
    }

    [Test]
    public async Task Build_TwoPacksCollidingWithEachOther_ReportsAgainstTheFirst()
    {
        CommandDescriptor first = new("AddStep", "a", "playback2d", new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        CommandDescriptor second = new("DeleteStep", "b", "playback2d", new KeyGesture(Key.F9, KeyModifiers.None), _ => true);
        FakePack packA = new("pack.a", [first]);
        FakePack packB = new("pack.b", [second]);

        CommandRegistry registry = CommandRegistry.Build([packA, packB]);

        await Assert.That(registry.Conflicts).IsNotEmpty();
        await Assert.That(registry.EffectiveBindings.Single(b => b.Key == Key.F9).Action)
            .IsEqualTo(Playback2DAction.AddStep).Because("the earlier pack's row already claimed the chord");
    }

    [Test]
    public void Build_CommandIdNotAnAction_Throws()
    {
        CommandDescriptor bad = new("NotARealAction", "x", "playback2d", null, _ => true);
        FakePack pack = new("pack.fake", [bad]);

        Assert.Throws<InvalidOperationException>(() => CommandRegistry.Build([pack]));
    }

    [Test]
    public async Task TryResolve_PackOff_ResolvesToNothing_PackOn_ResolvesTheCommand()
    {
        CommandDescriptor command = new("AddStep", "insert a step", "playback2d",
            new KeyGesture(Key.F9, KeyModifiers.None), ctx => ctx.Target is string);
        FakePack pack = new("pack.fake", [command]);
        CommandRegistry registry = CommandRegistry.Build([pack]);

        bool off = registry.TryResolve(Key.F9, KeyModifiers.None, "playback2d", _ => false, out CommandDescriptor? whenOff);
        await Assert.That(off).IsFalse();
        await Assert.That(whenOff).IsNull();

        bool on = registry.TryResolve(Key.F9, KeyModifiers.None, "playback2d", _ => true, out CommandDescriptor? whenOn);
        await Assert.That(on).IsTrue();
        await Assert.That(whenOn!.Id).IsEqualTo("AddStep");
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
    public async Task ScopeRoundTrips_ThroughEveryDeclaredValue()
    {
        foreach (Playback2DBindingScope scope in Enum.GetValues<Playback2DBindingScope>())
        {
            string name = CommandRegistry.ScopeName(scope);
            await Assert.That(CommandRegistry.ParseScope(name)).IsEqualTo(scope);
        }
    }

    private sealed class FakePack(string featureId, CommandDescriptor[] commands) : IFeaturePack
    {
        public string Id => "net.demoviewer.test." + featureId;
        public string FeatureId => featureId;
        public ExtensionManifest Manifest => FakeManifests.For(Id);

        public IEnumerable<FeatureDescriptor> Features =>
        [
            new(featureId, FeatureScope.Pack, "Fake pack", "a test pack", null, null, false,
                FeatureCatalog.Defaults(true, true, true))
        ];

        public IEnumerable<CommandDescriptor> Commands => commands;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }
}
