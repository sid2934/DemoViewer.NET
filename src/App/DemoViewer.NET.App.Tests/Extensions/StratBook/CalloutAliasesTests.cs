#region

using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.ViewModels.StratBook;
using static DemoViewer.NET.AppTests.RoundIndexTestData;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Callout Aliases (plan.md, strat-model.md §3.7): a team's word resolves to a nav place through
///     <see cref="CalloutResolver" /> wherever a place is typed, edited or shown - the callouts editor
///     in the Strat Book, the Query Canvas's place names, and a <see cref="PositionPredicate" /> a
///     TagQuery place filter would build.
/// </summary>
[NotInParallel]
public class CalloutAliasesTests
{
    private static CalloutResolverSource NoZones(StratStore store) => new(store, _ => null, () => null);

    [Test]
    public async Task FilterFor_ResolvesATeamsWord_IntoAPositionPredicate_ThatTagQueryMatchesOn()
    {
        CalloutResolver resolver = new(["TRamp", "Jungle"], new CalloutTable
        {
            Map = "de_mirage",
            Aliases = [new CalloutAlias { Alias = "popdog", Place = "TRamp" }]
        });

        TagInstance atRamp = Instance("kill", 100, 200);
        atRamp.Positions.Add(new TagPosition { X = 1, Y = 1, Place = "TRamp" });
        TagInstance elsewhere = Instance("kill", 300, 400);
        elsewhere.Positions.Add(new TagPosition { X = 2, Y = 2, Place = "Jungle" });
        List<TagDocument> docs = [Document(instances: [atRamp, elsewhere])];

        IReadOnlyList<TagInstanceRef> byAlias = TagQuery.Find(docs, TagSlice.Everything with { Positions = [resolver.FilterFor("popdog")] });
        IReadOnlyList<TagInstanceRef> byCase = TagQuery.Find(docs, TagSlice.Everything with { Positions = [resolver.FilterFor("  POP DOG ")] });
        IReadOnlyList<TagInstanceRef> byCanonical = TagQuery.Find(docs, TagSlice.Everything with { Positions = [resolver.FilterFor("TRamp")] });
        IReadOnlyList<TagInstanceRef> byUnknown = TagQuery.Find(docs, TagSlice.Everything with { Positions = [resolver.FilterFor("nonsense")] });

        using (Assert.Multiple())
        {
            await Assert.That(byAlias.Select(r => r.Id)).IsEquivalentTo(new[] { atRamp.Id });
            await Assert.That(byCase.Select(r => r.Id)).IsEquivalentTo(new[] { atRamp.Id }).Because("case and spacing fold the same as any other alias lookup");
            await Assert.That(byCanonical.Select(r => r.Id)).IsEquivalentTo(new[] { atRamp.Id }).Because("the canonical spelling still matches its own place");
            await Assert.That(byUnknown).IsEmpty().Because("a word with no place resolves to a filter that matches nothing, not to no filter at all");
        }
    }

    [Test]
    public async Task CalloutResolverSource_MergesTheOwnersAliases_OverTheEmbeddedCanonicalList()
    {
        StratStore store = new(null);
        CalloutResolverSource source = NoZones(store);
        StratOwner owner = StratOwner.Team(Guid.NewGuid());

        CalloutTable table = store.LoadCallouts(owner, "de_mirage");
        table.Aliases.Add(new CalloutAlias { Alias = "popdog", Place = "TRamp", Primary = true });
        await Assert.That(store.SaveCallouts(owner, table)).IsTrue();

        CalloutResolver resolver = source.For(owner, "de_mirage");
        using (Assert.Multiple())
        {
            await Assert.That(resolver.Resolve("popdog")).IsEqualTo("TRamp");
            await Assert.That(resolver.Display("TRamp")).IsEqualTo("popdog");
            await Assert.That(resolver.Source).IsEqualTo(CanonicalPlaces.EmbeddedSource).Because("no bundle directory in this harness, so the embedded list stands in for the map's zones");
        }
    }

    [Test]
    public async Task CalloutResolverSource_ForDefaultOwner_FallsBackToMe_WithNoTeamMarked()
    {
        StratStore store = new(null);
        CalloutResolverSource source = NoZones(store);

        CalloutTable table = store.LoadCallouts(StratOwner.Me(), "de_mirage");
        table.Aliases.Add(new CalloutAlias { Alias = "popdog", Place = "TRamp" });
        await Assert.That(store.SaveCallouts(StratOwner.Me(), table)).IsTrue();

        await Assert.That(source.ForDefaultOwner(null, "de_mirage").Resolve("popdog")).IsEqualTo("TRamp");
    }

    [Test]
    public async Task Editor_AddsAnAlias_AndItResolvesThroughTheStore()
    {
        StratStore store = new(null);
        StratOwner owner = StratOwner.Team(Guid.NewGuid());
        CalloutsEditorViewModel editor = new(store, NoZones(store));
        editor.Configure(owner, "de_mirage", []);

        editor.NewAliasText = "popdog";
        editor.NewPlaceText = "TRamp";
        editor.AddAliasCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(editor.IssueLine).IsEqualTo("");
            await Assert.That(editor.Aliases.Select(a => a.Alias)).IsEquivalentTo(["popdog"]);
            await Assert.That(store.LoadCallouts(owner, "de_mirage").Aliases.Single().Place).IsEqualTo("TRamp");
        }
    }

    [Test]
    public async Task Editor_RefusesADuplicateAlias_AndLeavesTheTableUnchanged()
    {
        StratStore store = new(null);
        StratOwner owner = StratOwner.Team(Guid.NewGuid());
        CalloutsEditorViewModel editor = new(store, NoZones(store));
        editor.Configure(owner, "de_mirage", []);

        editor.NewAliasText = "popdog";
        editor.NewPlaceText = "TRamp";
        editor.AddAliasCommand.Execute(null);

        editor.NewAliasText = "PopDog"; // folds to the same key as the existing alias
        editor.NewPlaceText = "Jungle";
        editor.AddAliasCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(editor.IssueLine).IsNotEqualTo("");
            await Assert.That(editor.Aliases.Select(a => a.Alias)).IsEquivalentTo(["popdog"]);
            await Assert.That(store.LoadCallouts(owner, "de_mirage").Aliases.Single().Place).IsEqualTo("TRamp");
        }
    }

    [Test]
    public async Task Editor_RemovesAnAlias()
    {
        StratStore store = new(null);
        StratOwner owner = StratOwner.Team(Guid.NewGuid());
        CalloutTable table = store.LoadCallouts(owner, "de_mirage");
        table.Aliases.Add(new CalloutAlias { Alias = "popdog", Place = "TRamp" });
        store.SaveCallouts(owner, table);

        CalloutsEditorViewModel editor = new(store, NoZones(store));
        editor.Configure(owner, "de_mirage", []);
        editor.Aliases.Single().RemoveCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(editor.Aliases).IsEmpty();
            await Assert.That(store.LoadCallouts(owner, "de_mirage").Aliases).IsEmpty();
        }
    }

    [Test]
    public async Task Editor_CopiesAliases_FromAnotherOwner_OnTheSameMap()
    {
        StratStore store = new(null);
        StratOwner source = StratOwner.Team(Guid.NewGuid());
        StratOwner target = StratOwner.Me();
        CalloutTable sourceTable = store.LoadCallouts(source, "de_mirage");
        sourceTable.Aliases.Add(new CalloutAlias { Alias = "popdog", Place = "TRamp", Primary = true });
        store.SaveCallouts(source, sourceTable);

        CalloutsEditorViewModel editor = new(store, NoZones(store));
        StratOwnerOption sourceOption = new(source, "their book");
        editor.Configure(target, "de_mirage", [sourceOption, new StratOwnerOption(target, "me")]);
        editor.CopyFrom = sourceOption;
        editor.CopyAliasesCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(editor.Aliases.Select(a => a.Alias)).IsEquivalentTo(["popdog"]);
            await Assert.That(store.LoadCallouts(target, "de_mirage").Aliases.Single().Place).IsEqualTo("TRamp");
        }
    }

    [Test]
    public async Task QueryCanvas_ShowsTheDefaultOwnersWord_ForAResolvedPlace()
    {
        CalloutResolver resolver = new(["Ramp"], new CalloutTable
        {
            Map = "de_nuke",
            Aliases = [new CalloutAlias { Alias = "popdog", Place = "Ramp", Primary = true }]
        });

        DemoCacheStore cache = new(null);
        RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        using SituationIndex index = new(cache, sidecars, sources);

        using QueryCanvasViewModel vm = new(index, new QueryPlaceResolver(index, sources.Zones), cache, _ => null,
            dispose => dispose(), calloutResolverFor: _ => resolver);
        vm.Map = "de_nuke";

        // Bypassing the pointer tool: what a resolved drop leaves behind is a placed token, which is
        // exactly what QueryRailSlotViewModel.Status and the resolver's Display both read from.
        vm.Document.Place(new QueryToken(QuerySide.Ct, 0, 1300, -1000, -416, "Ramp"));
        QueryRailSlotViewModel slot = vm.Slots.First(s => s.Side == QuerySide.Ct && s.Slot == 0);
        slot.Refresh();

        using (Assert.Multiple())
        {
            await Assert.That(slot.Status).IsEqualTo("popdog").Because("the default owner's word for the resolved canonical place");
            await Assert.That(vm.DisplayPlace("Ramp")).IsEqualTo("popdog");
        }

        sidecars.Dispose();
    }
}
