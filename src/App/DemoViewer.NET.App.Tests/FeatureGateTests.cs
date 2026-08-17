#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Pure-logic coverage of the feature-gating resolution layer: <see cref="FeatureCatalog" />
///     leader stability + <see cref="FeatureGate" /> resolution order (Required → override → category
///     default → group-leader → parent-tab cascade), <see cref="FeatureGate.HiddenCount" />, and live
///     re-resolution on a settings write. Each case drives a real <see cref="SettingsService" /> over a
///     temp dir bound through an <c>IOptionsMonitor&lt;AppSettings&gt;</c> — the exact wiring the app uses —
///     and constructs the gate via its INTERNAL test ctor with UI-thread marshaling disabled so
///     <see cref="FeatureGate.Changed" /> is observable inline without an Avalonia dispatcher (the App.Tests
///     process is shared, so a sibling headless test can otherwise leave a dispatcher installed process-wide).
///     No Avalonia is required. <see cref="NotInParallelAttribute" /> keeps these settings/IO cases off the
///     memory-pressured machine's parallel path and clear of the env-mutating settings suites.
/// </summary>
[NotInParallel]
public class FeatureGateTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvfeaturegate_" + Guid.NewGuid().ToString("N"));
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

    // Builds the live SettingsService → IOptionsMonitor<AppSettings> → FeatureGate chain over a throwaway
    // config dir and runs the body. The gate reads the monitor live, so the body can svc.Write to change
    // category/overrides and immediately re-query the gate.
    private static async Task WithGate(Func<SettingsService, FeatureGate, Task> body)
    {
        string dir = NewTempDir();
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
            Cleanup(dir);
        }
    }

    // The group leaders are fixed by catalog order and the whole group semantics ride on them — lock them so
    // an All-reorder that silently swaps a leader fails loudly here.
    [Test]
    public async Task GroupLeaders_AreStable()
    {
        await Assert.That(FeatureCatalog.GroupLeader(FeatureCatalog.GroupParserDeepDive)!.Id)
            .IsEqualTo("parser.hex");
        await Assert.That(FeatureCatalog.GroupLeader(FeatureCatalog.GroupGraphDebug)!.Id)
            .IsEqualTo("analysis.breakpoints");
    }

    // (a) Required beats an explicit override=false in every category.
    [Test]
    public async Task Required_StaysEnabled_EvenWithOverrideFalse()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s =>
            {
                s.UserCategory = UserCategory.Consumer;
                s.Features.Overrides["tab.library"] = false;
            });

            await Assert.That(gate.IsEnabled("tab.library")).IsTrue()
                .Because("a Required feature can never be disabled by an override");
        });
    }

    // (b) An explicit override enables a category-default-off tab and disables a category-default-on tab.
    // Tabs (no parent) isolate the override path from cascade.
    [Test]
    public async Task Override_EnablesDefaultOff_AndDisablesDefaultOn()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s => s.UserCategory = UserCategory.Consumer);

            await Assert.That(gate.IsEnabled("tab.parser")).IsFalse().Because("Parser is consumer-default-off");
            svc.Write(s => s.Features.Overrides["tab.parser"] = true);
            await Assert.That(gate.IsEnabled("tab.parser")).IsTrue().Because("the override enables it");

            await Assert.That(gate.IsEnabled("tab.stats")).IsTrue().Because("Stats is consumer-default-on");
            svc.Write(s => s.Features.Overrides["tab.stats"] = false);
            await Assert.That(gate.IsEnabled("tab.stats")).IsFalse().Because("the override disables it");
        });
    }

    // (c) Category defaults resolve per the matrix for all three categories.
    [Test]
    public async Task CategoryDefaults_ResolvePerMatrix()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s => s.UserCategory = UserCategory.Consumer);
            await Assert.That(gate.IsEnabled("tab.parser")).IsFalse();
            await Assert.That(gate.IsEnabled("tab.diagnostics")).IsFalse();
            await Assert.That(gate.IsEnabled("chrome.debugger")).IsFalse();
            await Assert.That(gate.IsEnabled("tab.stats")).IsTrue();
            await Assert.That(gate.IsEnabled("chrome.processingQueue")).IsTrue()
                .Because("every category sees the background-processing queue so all users stay aware of it");

            svc.Write(s => s.UserCategory = UserCategory.PowerUser);
            await Assert.That(gate.IsEnabled("tab.parser")).IsTrue();
            await Assert.That(gate.IsEnabled("tab.diagnostics")).IsFalse().Because("Diagnostics is dev-only");
            await Assert.That(gate.IsEnabled("chrome.debugger")).IsFalse().Because("the debugger rail is dev-only");

            svc.Write(s => s.UserCategory = UserCategory.Developer);
            await Assert.That(gate.IsEnabled("tab.parser")).IsTrue();
            await Assert.That(gate.IsEnabled("tab.diagnostics")).IsTrue();
            await Assert.That(gate.IsEnabled("chrome.debugger")).IsTrue();
        });
    }

    // (d) Toggling the group LEADER's override flips every member atomically (graphDebug: leader
    // analysis.breakpoints → chrome.debugger + chrome.breakpointNav).
    [Test]
    public async Task Group_LeaderOverride_FlipsAllMembers()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s => s.UserCategory = UserCategory.PowerUser); // graphDebug all default-off here

            await Assert.That(gate.IsEnabled("analysis.breakpoints")).IsFalse();
            await Assert.That(gate.IsEnabled("chrome.debugger")).IsFalse();
            await Assert.That(gate.IsEnabled("chrome.breakpointNav")).IsFalse();

            svc.Write(s => s.Features.Overrides["analysis.breakpoints"] = true); // flip the leader

            await Assert.That(gate.IsEnabled("analysis.breakpoints")).IsTrue()
                .Because("the leader's own override wins (tab.analysis is on for power → no cascade)");
            await Assert.That(gate.IsEnabled("chrome.debugger")).IsTrue()
                .Because("a group member adopts the leader's resolved own-state");
            await Assert.That(gate.IsEnabled("chrome.breakpointNav")).IsTrue();
        });
    }

    // (e) CASCADE: a sub-feature is off when its parent tab is off — even a default-ON sub-feature, and even
    // one with an explicit override=true (cascade beats the override). Re-enabling the parent lets it through.
    [Test]
    public async Task Cascade_ParentTabOff_ForcesSubFeatureOff()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s => s.UserCategory = UserCategory.Consumer); // tab.parser off for consumer

            await Assert.That(gate.IsEnabled("parser.cards")).IsFalse()
                .Because("parser.cards is default-ON but its parent tab.parser is off → cascade hides it");

            svc.Write(s => s.Features.Overrides["parser.hex"] = true);
            await Assert.That(gate.IsEnabled("parser.hex")).IsFalse()
                .Because("cascade from the disabled parent tab beats even an explicit override=true");

            svc.Write(s => s.Features.Overrides["tab.parser"] = true); // open the parent tab
            await Assert.That(gate.IsEnabled("parser.cards")).IsTrue().Because("parent on → default-on child shows");
            await Assert.That(gate.IsEnabled("parser.hex")).IsTrue().Because("parent on + override → child shows");
        });
    }

    // (f) HiddenCount is 0 for a developer (sees everything) and >0 for a consumer; DeveloperMode escalates
    // any category to Developer, so it also zeroes the count.
    [Test]
    public async Task HiddenCount_IsZeroForDeveloper_AndPositiveForConsumer()
    {
        await WithGate(async (svc, gate) =>
        {
            svc.Write(s => s.UserCategory = UserCategory.Developer);
            await Assert.That(gate.HiddenCount).IsEqualTo(0).Because("a developer sees every gated feature");

            svc.Write(s => s.UserCategory = UserCategory.Consumer);
            await Assert.That(gate.HiddenCount).IsGreaterThan(0).Because("a consumer has developer-visible features hidden");

            // DeveloperMode is the master unlock: category stays Consumer on disk but resolves to Developer.
            svc.Write(s => s.Features.DeveloperMode = true);
            await Assert.That(gate.Category).IsEqualTo(UserCategory.Developer);
            await Assert.That(gate.HiddenCount).IsEqualTo(0).Because("DeveloperMode escalates to the full set");
        });
    }

    // (g) A live settings write raises Changed AND flips IsEnabled without reconstructing the gate.
    [Test]
    public async Task LiveWrite_RaisesChanged_AndReResolves()
    {
        await WithGate(async (svc, gate) =>
        {
            int changed = 0;
            gate.Changed += (_, _) => changed++;

            // A category change: PowerUser (default) → Consumer flips tab.parser off.
            await Assert.That(gate.IsEnabled("tab.parser")).IsTrue();
            svc.Write(s => s.UserCategory = UserCategory.Consumer);
            await Assert.That(changed).IsGreaterThanOrEqualTo(1).Because("the write raised Changed inline");
            await Assert.That(gate.IsEnabled("tab.parser")).IsFalse().Because("the gate re-resolved live");

            // An override change on the same live gate.
            int before = changed;
            svc.Write(s => s.Features.Overrides["tab.parser"] = true);
            await Assert.That(changed).IsGreaterThan(before).Because("the override write raised Changed again");
            await Assert.That(gate.IsEnabled("tab.parser")).IsTrue();
        });
    }

    // An id not in the catalog is not gated → visible (fail-open). Chunk B relies on this default.
    [Test]
    public async Task UnknownId_FailsOpen()
    {
        await WithGate(async (_, gate) => { await Assert.That(gate.IsEnabled("does.not.exist")).IsTrue(); });
    }
}
