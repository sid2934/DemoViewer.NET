#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.RoundTagger;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Round Tagger module (tag-store.md §3.11): its persisted ids, the two feature rows with the right
///     scope and parent, and The Matrix's one Main-strip tab.
/// </summary>
public class RoundTaggerModuleTests
{
    [Test]
    public async Task TheModule_HasItsPersistedIds_AndTheMatrixTab()
    {
        RoundTaggerModule module = new(() => throw new InvalidOperationException("never built here"));
        List<WorkspaceTabDescriptor> tabs = [.. module.CreateTabs(null!)];

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.roundtagger");
            await Assert.That(tabs.Count).IsEqualTo(1);
            await Assert.That(tabs[0].TabId).IsEqualTo("tagger.matrix");
            await Assert.That(tabs[0].Placement).IsEqualTo(TabPlacement.Main);
            await Assert.That(tabs[0].Order).IsEqualTo(7).Because("after Review");
            await Assert.That(FeatureCatalog.ById("tab.tagger")).IsNotNull();
            await Assert.That(FeatureCatalog.ById("playback2d.tagger")).IsNotNull();
        }
    }

    [Test]
    public async Task TheTabFeature_IsATab_VisibleToEveryCategory()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById(RoundTaggerModule.TabFeatureId);

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsNull();
            await Assert.That(feature.GroupId).IsNull().Because("it must not disturb the leader-lock ordering");
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the tab");
            }

            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain(RoundTaggerModule.TabFeatureId)
                .Because("tags work in the browser for the session");
        }
    }

    [Test]
    public async Task ThePaletteFeature_IsASubFeatureOf2DPlayback_OnBothHosts()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById(RoundTaggerModule.PaletteFeatureId);

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.SubFeature);
            await Assert.That(feature.ParentId).IsEqualTo("tab.playback2d");
            await Assert.That(feature.GroupId).IsNull();
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain(RoundTaggerModule.PaletteFeatureId);
        }
    }
}
