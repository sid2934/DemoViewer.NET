#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     B5-1's audit, as a test: the five Playback2D v2 sub-feature rows are present, shaped as registry
///     §3.10 pins them, ordered as one contiguous block, and cascade correctly with their parent tab.
///     <para>
///         The ids are <b>persisted override keys</b> (settings write <c>Features:Overrides:{id}</c>), so a
///         rename is a silent reset of every user's choice. This class is what makes that rename fail.
///     </para>
///     <para>
///         Direct execution: the gate is built through its internal test ctor with UI-thread marshaling
///         off, exactly as <see cref="FeatureGateTests" /> does — no Avalonia dispatcher required.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DFeatureCatalogTests
{
    /// <summary>The five ids, in the order registry §3.10 fixes for the contiguous catalog block.</summary>
    internal static readonly string[] Ids =
    [
        "playback2d.annotations",
        "playback2d.timeline",
        "playback2d.levels.auto",
        "playback2d.follow",
        "playback2d.export"
    ];

    [Test]
    public async Task AllFiveIds_Present_AsSubFeaturesOfPlayback2dTab()
    {
        FeatureDescriptor[] children = FeatureCatalog.Children("tab.playback2d").ToArray();

        await Assert.That(children.Select(c => c.Id).ToArray()).IsEquivalentTo(Ids)
            .Because("registry §3.10 fixes both the set and its order");

        foreach (FeatureDescriptor child in children)
        {
            await Assert.That(child.Scope).IsEqualTo(FeatureScope.SubFeature);
            await Assert.That(child.ParentId).IsEqualTo("tab.playback2d");
            await Assert.That(child.Required).IsFalse();
        }
    }

    /// <summary>
    ///     The leader-lock precondition. A group's LEADER is its FIRST member in <c>All</c>, so a new row
    ///     carrying a GroupId would re-parent an existing group depending on where it was inserted.
    /// </summary>
    [Test]
    public async Task Playback2dIds_HaveNoGroupId()
    {
        foreach (string id in Ids)
        {
            FeatureDescriptor? descriptor = FeatureCatalog.ById(id);
            await Assert.That(descriptor).IsNotNull();
            await Assert.That(descriptor!.GroupId).IsNull();
        }
    }

    /// <summary>
    ///     Deliberately duplicates <c>FeatureGateTests.GroupLeaders_AreStable</c>: this one is the
    ///     insert-POSITION regression net for the v2 block, and it should fail here, next to the block that
    ///     moved, rather than in a file about something else.
    /// </summary>
    [Test]
    public async Task GroupLeaders_Unchanged_AfterInsert()
    {
        await Assert.That(FeatureCatalog.GroupLeader(FeatureCatalog.GroupParserDeepDive)!.Id)
            .IsEqualTo("parser.hex");
        await Assert.That(FeatureCatalog.GroupLeader(FeatureCatalog.GroupGraphDebug)!.Id)
            .IsEqualTo("analysis.breakpoints");
    }

    /// <summary>
    ///     B5 D6: these are the release's headline consumer features, so every category gets them on by
    ///     default — the same call the design-system matrix records for <c>tab.highlights</c>.
    /// </summary>
    [Test]
    public async Task Defaults_OnForEveryCategory()
    {
        foreach (UserCategory category in new[]
                 {
                     UserCategory.Consumer, UserCategory.PowerUser, UserCategory.Developer
                 })
        {
            await WithGate(async (svc, gate) =>
            {
                svc.Write(s => s.UserCategory = category);
                foreach (string id in Ids)
                {
                    await Assert.That(gate.IsEnabled(id)).IsTrue()
                        .Because($"{id} must default on for {category}");
                }
            });
        }
    }

    [Test]
    public async Task Cascade_TabOff_ForcesAllFiveOff() =>
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s =>
            {
                s.UserCategory = UserCategory.Developer;
                s.Features.Overrides["tab.playback2d"] = false;
            });

            foreach (string id in Ids)
            {
                await Assert.That(gate.IsEnabled(id)).IsFalse()
                    .Because("a sub-feature cannot outlive the tab it lives in");
            }
        });

    [Test]
    public async Task Override_TurnsOneOff_WithoutTouchingSiblings() =>
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s =>
            {
                s.UserCategory = UserCategory.Consumer;
                s.Features.Overrides["playback2d.export"] = false;
            });

            await Assert.That(gate.IsEnabled("playback2d.export")).IsFalse();
            foreach (string id in Ids.Where(i => i != "playback2d.export"))
            {
                await Assert.That(gate.IsEnabled(id)).IsTrue();
            }
        });

    // The live SettingsService → IOptionsMonitor<AppSettings> → FeatureGate chain over a throwaway config
    // dir: the same wiring the app uses, so an override written here is re-resolved immediately.
    private static async Task WithGate(Func<SettingsService, FeatureGate, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvpb2dcatalog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SettingsService svc = new(dir);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            using ServiceProvider sp = services.BuildServiceProvider();
            IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();

            using FeatureGate gate = new(monitor, false);
            await body(svc, gate);
        }
        finally
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
}
