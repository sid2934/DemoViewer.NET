#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Tab-level feature-gating ENFORCEMENT. Proves the shell FILTERS its workspace
///     tab strip to the gate-enabled set for the current user category, reconciles that set LIVE on a
///     settings write (add/remove by TabId, never a full rebuild), neighbor-selects when the selected tab is
///     removed, and migrates active-tab session persistence from a fragile int index to the durable TabId.
///     <para>
///         The gate is a real <see cref="FeatureGate" /> over a live <see cref="SettingsService" />, the
///         exact production wiring, built via its INTERNAL test ctor with UI-thread marshaling disabled so
///         <see cref="FeatureGate.Changed" /> raises inline on the writing (UI) thread and the reconcile is
///         synchronously observable. <see cref="NotInParallelAttribute" /> because these mutate process-global
///         session-path state and run heavy shell constructions on the memory-pressured machine.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class TabFeatureGatingTests
{
    // The full Main + Diagnostics tab set (developer sees all). It is static so CA1861 stays clean.
    private static readonly string[] _allTabIds =
    [
        "builtin.library", "builtin.matchoverview", "builtin.parser", "builtin.entity", "builtin.stats",
        "builtin.analysis", "playback2d.viewport", "ruleworkbench.editor", "builtin.diagnostics"
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────

    // Composes the shell the way App.axaml.cs BuildRegistry does: the 2D Playback pilot (tab.playback2d)
    // and the Rule Workbench (tab.authoring), so every gated Main-strip tab is exercised.
    private static MainViewModel NewShell(IFeatureGate? gate, SettingsService? settings = null)
    {
        ModuleRegistry registry = new();
        registry.Register(new Playback2DModule());
        registry.Register(new RuleWorkbenchModule());
        // settings: when supplied, session-restore round-trips through the Session section of the
        // single config file; null → session persistence no-ops (the default for the gating cases).
        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, null, settings);
        // Mirror the composition root: the ctor deliberately restores NOTHING (a ctor-time tab activation
        // can resolve the still-uncached shell singleton and recurse: see App.BuildShell). The host calls
        // RestoreSession once construction is complete; these fixtures must do the same to stay faithful.
        vm.RestoreSession();
        return vm;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvtabgate_" + Guid.NewGuid().ToString("N"));
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

    // Builds the SettingsService → IOptionsMonitor<AppSettings> → FeatureGate chain over a throwaway config
    // dir at the given starting category, constructs a gated shell, and runs the body, all on the headless
    // UI thread (the shell needs a dispatcher). A body svc.Write flips the live category/override and the
    // gate re-resolves + raises Changed inline. The VM is disposed (unsubscribing from the gate) before the
    // gate is.
    private static async Task WithGatedShell(UserCategory initial, Func<SettingsService, MainViewModel, Task> body)
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                svc.Write(s => s.UserCategory = initial);

                ServiceCollection services = new();
                services.Configure<AppSettings>(svc.Configuration);
                using ServiceProvider sp = services.BuildServiceProvider();
                IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
                using FeatureGate gate = new(monitor, false);

                MainViewModel vm = NewShell(gate);
                try
                {
                    await body(svc, vm);
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // The invariant every code path must preserve: exactly the selected tab has a realized View; every
    // inactive tab has dropped its (the inactive-content-unload contract).
    private static int RealizedViewCount(MainViewModel vm) => vm.Tabs.Count(t => t.ActiveContent != null);

    // ── Category filtering ────────────────────────────────────────────────────

    // A Consumer sees ONLY the three viewing tabs (Library + Stats + 2D Playback); every power/dev tab is
    // filtered out of the strip entirely.
    [Test]
    public async Task Consumer_ShowsOnlyConsumerTabs()
    {
        await WithGatedShell(UserCategory.Consumer, async (_, vm) =>
        {
            string[] ids = vm.Tabs.Select(t => t.TabId).ToArray();

            await Assert.That(ids).Contains("builtin.library");
            await Assert.That(ids).Contains("builtin.stats");
            await Assert.That(ids).Contains("playback2d.viewport");

            await Assert.That(ids).DoesNotContain("builtin.parser");
            await Assert.That(ids).DoesNotContain("builtin.entity");
            await Assert.That(ids).DoesNotContain("builtin.analysis");
            await Assert.That(ids).DoesNotContain("ruleworkbench.editor");
            await Assert.That(ids).DoesNotContain("builtin.diagnostics");

            // Library is the landing tab and stays selected on startup.
            await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library");
            await Assert.That(RealizedViewCount(vm)).IsEqualTo(1);
        });
    }

    // A Developer sees every tab (nothing filtered).
    [Test]
    public async Task Developer_ShowsAllTabs()
    {
        await WithGatedShell(UserCategory.Developer, async (_, vm) =>
        {
            string[] ids = vm.Tabs.Select(t => t.TabId).ToArray();

            foreach (string expected in _allTabIds)
            {
                await Assert.That(ids).Contains(expected);
            }

            await Assert.That(vm.Tabs.Count).IsEqualTo(9);
        });
    }

    // Null gate (every existing `new MainViewModel(...)` caller) → NO filtering: the full tab set shows,
    // exactly as before gating existed. The additive-change regression guard.
    [Test]
    public async Task NullGate_ShowsAllTabs()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell(null);
            try
            {
                string[] ids = vm.Tabs.Select(t => t.TabId).ToArray();
                await Assert.That(ids).Contains("builtin.parser");
                await Assert.That(ids).Contains("builtin.diagnostics");
                await Assert.That(ids).Contains("ruleworkbench.editor");
                await Assert.That(vm.Tabs.Count).IsEqualTo(9);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    // ── Live reconcile ────────────────────────────────────────────────────────

    // Consumer → Developer while Stats is selected: the power/dev tabs appear at their sorted positions and
    // Stats stays selected (no full rebuild). Then Developer → Consumer while a now-disabled tab (Parser) is
    // selected: the selection moves to a surviving neighbor, Parser is removed, and the one-realized-View
    // invariant holds throughout.
    [Test]
    public async Task LiveReconcile_AddsAndRemovesTabs_AndNeighborSelectsOnRemoval()
    {
        await WithGatedShell(UserCategory.Consumer, async (svc, vm) =>
        {
            // Select Stats (a tab enabled in BOTH categories) so it survives the flip up.
            vm.SelectedTab = vm.Tabs.First(t => t.TabId == "builtin.stats");
            await Assert.That(RealizedViewCount(vm)).IsEqualTo(1);

            // Flip up to Developer. The write raises Changed inline → reconcile runs synchronously.
            svc.Write(s => s.UserCategory = UserCategory.Developer);

            string[] ids = vm.Tabs.Select(t => t.TabId).ToArray();
            await Assert.That(ids).Contains("builtin.parser").Because("a re-enabled tab is inserted live");
            await Assert.That(ids).Contains("builtin.analysis");
            await Assert.That(ids).Contains("builtin.diagnostics");
            await Assert.That(vm.Tabs.Count).IsEqualTo(9);

            // Parser inserted at its sorted (Order 0) slot, after Library and the always-present Match Overview
            // (also Order 0, yielded first, so it keeps the earlier slot under the stable ThenBy(Order)).
            await Assert.That(vm.Tabs[0].TabId).IsEqualTo("builtin.library");
            await Assert.That(vm.Tabs[1].TabId).IsEqualTo("builtin.matchoverview");
            await Assert.That(vm.Tabs[2].TabId).IsEqualTo("builtin.parser");

            // Stats stayed selected across the reconcile and remains the sole realized View.
            await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.stats");
            await Assert.That(RealizedViewCount(vm)).IsEqualTo(1);
            await Assert.That(vm.SelectedTab.ActiveContent).IsNotNull();

            // Now select a DEV-only tab and flip back DOWN to Consumer while it is selected.
            vm.SelectedTab = vm.Tabs.First(t => t.TabId == "builtin.parser");
            await Assert.That(RealizedViewCount(vm)).IsEqualTo(1);

            svc.Write(s => s.UserCategory = UserCategory.Consumer);

            string[] after = vm.Tabs.Select(t => t.TabId).ToArray();
            await Assert.That(after).DoesNotContain("builtin.parser").Because("the disabled selected tab is removed");

            // Selection moved to the nearest lower-Order surviving tab. Parser (Order 0)'s nearest lower in
            // the Consumer set {library(-1), stats(2), playback2d(4)} is Library, assert the concrete
            // neighbor AND the generic contract (a still-enabled, non-removed tab).
            await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library");
            await Assert.That(vm.SelectedTab.TabId).IsNotEqualTo("builtin.parser");
            await Assert.That(RealizedViewCount(vm)).IsEqualTo(1);

            // Selection survives reconciliation as an IDENTITY, not a position: the selected descriptor is
            // still an element of the live collection. (This replaces the old int-mirror sync assertion.
            // The mirror is gone; tab selection is name-based end to end.)
            await Assert.That(vm.Tabs.Contains(vm.SelectedTab)).IsTrue()
                .Because("the surviving selection must be a live descriptor, not a dangling one");
        });
    }

    // The raison d'être of reconcile-by-TabId: a ViewModelFactory tab's cached VM must SURVIVE a
    // disable → re-enable cycle (a full rebuild would mint a fresh descriptor and tear that state down).
    // Authoring uses a lazy+retained ViewModelFactory, so it exercises the cached-VM path (the built-in
    // shell-DataContext tabs and the both-categories 2D tab do not).
    [Test]
    public async Task LiveReconcile_PreservesModuleTabVmAcrossDisableReEnable()
    {
        await WithGatedShell(UserCategory.Developer, async (svc, vm) =>
        {
            WorkspaceTabDescriptor authoring = vm.Tabs.First(t => t.TabId == "ruleworkbench.editor");
            vm.SelectedTab = authoring; // Activate() builds + caches the module-tab VM.
            IWorkspaceTabViewModel? cachedVm = authoring.TabViewModel;
            await Assert.That(cachedVm).IsNotNull();

            // Move selection off Authoring so the down-flip removal doesn't route through neighbor-select
            // (keeps this case focused on the cached-VM invariant).
            vm.SelectedTab = vm.Tabs.First(t => t.TabId == "builtin.stats");

            // Disable (Consumer drops Authoring) then re-enable (Developer re-adds it).
            svc.Write(s => s.UserCategory = UserCategory.Consumer);
            await Assert.That(vm.Tabs.Select(t => t.TabId).ToArray()).DoesNotContain("ruleworkbench.editor");

            svc.Write(s => s.UserCategory = UserCategory.Developer);
            WorkspaceTabDescriptor reAdded = vm.Tabs.First(t => t.TabId == "ruleworkbench.editor");

            // Same descriptor object came back from the cache → its VM state is intact. A rebuild would fail
            // both of these.
            await Assert.That(ReferenceEquals(reAdded, authoring)).IsTrue()
                .Because("reconcile re-adds the SAME cached descriptor, never a rebuilt one");
            await Assert.That(ReferenceEquals(reAdded.TabViewModel, cachedVm)).IsTrue()
                .Because("the module-tab VM survives disable → re-enable");
        });
    }

    // ── Session persistence by TabId ──────────────────────────────────────────

    // Save with a non-default tab selected → a fresh shell restores the SAME TabId. A stale/removed TabId
    // falls back to Library. The session round-trips through the Session section of the single
    // config file (a throwaway config-dir SettingsService), not a standalone session.json.
    [Test]
    public async Task Session_RoundTripsByTabId_AndStaleFallsBackToLibrary()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);

                // Save: select Entity (a non-default tab), persist, tear down.
                MainViewModel vm1 = NewShell(null, svc);
                vm1.SelectedTab = vm1.Tabs.First(t => t.TabId == "builtin.entity");
                vm1.SaveSession();
                vm1.Dispose();

                // The persisted Session section carries the durable TabId.
                SessionPayload? saved = svc.LoadSession();
                await Assert.That(saved!.ActiveTabId).IsEqualTo("builtin.entity");

                // Restore: a fresh shell over the same config dir selects the same TabId.
                MainViewModel vm2 = NewShell(null, svc);
                await Assert.That(vm2.SelectedTab!.TabId).IsEqualTo("builtin.entity");
                vm2.Dispose();

                // Stale TabId → Library fallback.
                svc.SaveSession(new SessionPayload(null, null, null, false, false, "does.not.exist"));
                MainViewModel vm3 = NewShell(null, svc);
                await Assert.That(vm3.SelectedTab!.TabId).IsEqualTo("builtin.library");
                vm3.Dispose();
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Tab restore is NAME-BASED, with no positional fallback. A session that predates TabId persistence
    // (ActiveTabId omitted → null) therefore lands on the first tab, Library. That is deliberate: the tab
    // set is dynamic (feature gating, new built-ins landing mid-strip), so an index restored from an older
    // build silently selects a DIFFERENT tab, which is exactly what happened when Match Overview was
    // inserted at position 1. A one-time, self-healing loss of the remembered tab beats confidently
    // restoring the wrong one.
    [Test]
    public async Task Session_WithoutTabId_FallsBackToLibrary_NotAPosition()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            // A payload with NO ActiveTabId (the 5-arg ctor defaults it to null, the omitted-in-JSON case).
            svc.SaveSession(new SessionPayload(null, null, null, false, false));

            await HeadlessSession.RunOnUi(async () =>
            {
                MainViewModel vm = NewShell(null, svc);
                try
                {
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                        .Because("with no durable id there is nothing to restore — land on the landing tab");
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Shell navigation must resolve tabs BY NAME, never by position. "Reveal this entity class" is the one
    // in-shell jump that used a hardcoded index (`SelectedMainTab = 1`, commented for a v2 tab order of
    // "0 Parser · 1 Entity Tracking · 2 Analysis"). Every tab added since shifted it silently. By the time
    // Match Overview landed at position 1 the jump was two tabs off target and opened the landing page. This
    // pins the destination by id so the next inserted tab cannot move it again.
    [Test]
    public async Task RevealEntityClass_NavigatesByTabId_NotByPosition()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell(null);
            try
            {
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library");

                vm.Navigation.RevealClass("CCSPlayerPawn");

                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.entity")
                    .Because("revealing an entity class must land on Entity Tracking whatever its index is");
                await Assert.That(vm.EntityTab.ClassBrowser.Filter).IsEqualTo("CCSPlayerPawn");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    // Headless render smoke: the full MainView under a Consumer gate (with a file loaded) instantiates and
    // lays out without throwing: runtime-exercising the toolbar MultiBinding (HasFile AND parse-chain gate),
    // the gated Debugger/Output toggles, the NavStrip breakpoint-cluster gate, and the StatusStrip
    // "N features hidden" note. Consumer lands on the Library tab (no MSAGL) so the render stays cheap. This
    // catches a malformed-binding/layout throw that compiled-binding TYPE-checking alone would not; the
    // gated-OFF decisions themselves are proven by the VM shim asserts above.
    [Test]
    public async Task MainView_RendersUnderConsumerGate_WithHiddenAffordance()
    {
        await WithGatedShell(UserCategory.Consumer, async (_, vm) =>
        {
            vm.HasFile = true; // surfaces the toolbar + NavStrip chrome rows so their gated bindings evaluate.

            // Sanity: the affordance note is populated (a Consumer hides gated features).
            await Assert.That(vm.HiddenFeatureNote).IsNotEqualTo("");

            MainView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "mainview-consumer-gate.png");
            frame!.Save(outPath);
            int nonBg = ScanNonBackground(frame);
            Console.WriteLine($"[mainview-gate] {outPath} nonBg={nonBg}");

            // The shell draws its toolbar / tab strip / status strip, far more than an empty background.
            await Assert.That(nonBg).IsGreaterThan(200);
        });
    }

    private static int ScanNonBackground(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (r > 60 || g > 60 || b > 60)
            {
                nonBg++;
            }
        }

        return nonBg;
    }

    // ── Sub-feature + chrome gate shims ───────────────────────────────────────
    // The shell exposes read-only bool shims the XAML binds IsVisible to. These prove the shims resolve
    // per-category off the SAME gate the tab strip uses, force owned panels closed on a live downgrade, and
    // fail open (all true) with no gate, the enforcement counterpart to the tab-filtering cases above.

    // A Consumer sees none of the deep-dive / developer chrome: every sub-feature + chrome shim is off, and
    // the "N features hidden" count is positive so the status-strip affordance shows.
    [Test]
    public async Task Consumer_SubFeatureAndChromeShims_AllGatedOff()
    {
        await WithGatedShell(UserCategory.Consumer, async (_, vm) =>
        {
            await Assert.That(vm.IsHexPaneEnabled).IsFalse();
            await Assert.That(vm.IsParseChainEnabled).IsFalse();
            await Assert.That(vm.IsDebuggerChromeEnabled).IsFalse();
            await Assert.That(vm.IsOutputChromeEnabled).IsFalse().Because("Output chrome is power-user+, off for consumer");
            await Assert.That(vm.IsBreakpointNavEnabled).IsFalse();
            await Assert.That(vm.IsSerializerSchemaEnabled).IsFalse();
            await Assert.That(vm.IsAnalysisBreakpointsEnabled).IsFalse();

            await Assert.That(vm.HiddenFeatureCount).IsGreaterThan(0);
            await Assert.That(vm.HiddenFeatureNote).IsNotEqualTo("").Because("a positive hidden count surfaces the affordance");
        });
    }

    // A Developer sees everything: every shim is on and nothing is reported hidden (empty note → no affordance).
    [Test]
    public async Task Developer_SubFeatureAndChromeShims_AllVisible()
    {
        await WithGatedShell(UserCategory.Developer, async (_, vm) =>
        {
            await Assert.That(vm.IsHexPaneEnabled).IsTrue();
            await Assert.That(vm.IsParseChainEnabled).IsTrue();
            await Assert.That(vm.IsDebuggerChromeEnabled).IsTrue();
            await Assert.That(vm.IsOutputChromeEnabled).IsTrue();
            await Assert.That(vm.IsBreakpointNavEnabled).IsTrue();
            await Assert.That(vm.IsSerializerSchemaEnabled).IsTrue();
            await Assert.That(vm.IsAnalysisBreakpointsEnabled).IsTrue();

            await Assert.That(vm.HiddenFeatureCount).IsEqualTo(0);
            await Assert.That(vm.HiddenFeatureNote).IsEqualTo("");
        });
    }

    // Spot-check the middle of the matrix: a Power-User gets the Output drawer (power-user+) but NOT the
    // developer deep-dive chrome (debugger rail, breakpoint nav, hex pane).
    [Test]
    public async Task PowerUser_ChromeShims_MatrixSpotCheck()
    {
        await WithGatedShell(UserCategory.PowerUser, async (_, vm) =>
        {
            await Assert.That(vm.IsOutputChromeEnabled).IsTrue().Because("the Output drawer is power-user+");
            await Assert.That(vm.IsDebuggerChromeEnabled).IsFalse().Because("the debugger rail is developer-only");
            await Assert.That(vm.IsBreakpointNavEnabled).IsFalse().Because("breakpoint nav is developer-only");
            await Assert.That(vm.IsHexPaneEnabled).IsFalse().Because("the hex pane is a developer deep-dive default");
        });
    }

    // Live downgrade: with the debugger panel open under Developer, flipping to Consumer must gate the chrome
    // off AND force the open panel closed (so it doesn't linger without its now-hidden toggle), and the shim's
    // PropertyChanged must fire so the bound toolbar button reflows.
    [Test]
    public async Task LiveDowngrade_ForcesDebuggerPanelClosed_AndRaisesShim()
    {
        await WithGatedShell(UserCategory.Developer, async (svc, vm) =>
        {
            vm.IsDebuggerPanelVisible = true;
            await Assert.That(vm.IsDebuggerChromeEnabled).IsTrue();

            bool shimRaised = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsDebuggerChromeEnabled))
                {
                    shimRaised = true;
                }
            };

            svc.Write(s => s.UserCategory = UserCategory.Consumer);

            await Assert.That(vm.IsDebuggerChromeEnabled).IsFalse().Because("the chrome gate re-resolved off");
            await Assert.That(vm.IsDebuggerPanelVisible).IsFalse().Because("the open panel is force-closed when its chrome hides");
            await Assert.That(shimRaised).IsTrue().Because("the shim re-raises PropertyChanged so bound chrome reflows");
        });
    }

    // Null gate (every existing no-gate `new MainViewModel(...)`) → every shim fails open (true) and nothing is
    // reported hidden, the additive-change regression guard for the shims.
    [Test]
    public async Task NullGate_SubFeatureAndChromeShims_AllVisible()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell(null);
            try
            {
                await Assert.That(vm.IsHexPaneEnabled).IsTrue();
                await Assert.That(vm.IsParseChainEnabled).IsTrue();
                await Assert.That(vm.IsDebuggerChromeEnabled).IsTrue();
                await Assert.That(vm.IsOutputChromeEnabled).IsTrue();
                await Assert.That(vm.IsBreakpointNavEnabled).IsTrue();
                await Assert.That(vm.IsSerializerSchemaEnabled).IsTrue();
                await Assert.That(vm.IsAnalysisBreakpointsEnabled).IsTrue();
                await Assert.That(vm.HiddenFeatureCount).IsEqualTo(0);
                await Assert.That(vm.HiddenFeatureNote).IsEqualTo("");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }
}
