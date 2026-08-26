#region

using Avalonia.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     D1's persistence and its Settings surface. The array half is proved on the FILELESS path as well as
///     the file one: on WASM there is no <c>settings.json</c>, only the in-memory provider
///     <c>SettingsService.WriteInMemory</c> populates by hand, and a property missing from that method binds
///     fine, writes fine, and forgets itself on the next read with nothing to see anywhere.
///     <para>
///         The Settings-screen cases are the other half of the contract: this screen VALIDATES before it
///         writes, so nothing it persists can ever be dropped on load — which is why a non-empty rejection
///         note always means a hand-edited file.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DKeybindSettingsTests
{
    [Test]
    public async Task Overrides_SurviveAFilelessWrite_AndAShrinkDropsStaleIndices()
    {
        SettingsService svc = new(null); // the WASM branch — no file, only the in-memory provider

        svc.Write(s => s.Playback2D.KeybindOverrides = ["NextRound=Shift+R", "PrevRound=Shift+T"]);
        await Assert.That(svc.Current.Playback2D.KeybindOverrides.Length).IsEqualTo(2);
        await Assert.That(svc.Current.Playback2D.KeybindOverrides[1]).IsEqualTo("PrevRound=Shift+T");

        svc.Write(s => s.Playback2D.KeybindOverrides = ["NextRound=Shift+R"]);
        await Assert.That(svc.Current.Playback2D.KeybindOverrides.Length).IsEqualTo(1)
            .Because("the ReplaceAll rebuild must drop Playback2D:KeybindOverrides:1");

        svc.Write(s => s.Playback2D.KeybindOverrides = []);
        await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();
    }

    /// <summary>A rebind has to outlive the process, which on desktop means the file and a fresh service.</summary>
    [Test]
    public async Task Overrides_SurviveARestart_OnTheFilePath()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService first = new(dir);
            first.Write(s => s.Playback2D.KeybindOverrides = ["NextRound=Shift+R"]);

            SettingsService reopened = new(dir); // the "next launch"
            string[] rows = reopened.Current.Playback2D.KeybindOverrides;
            await Assert.That(rows.Length).IsEqualTo(1);

            Playback2DKeymapProfile profile = Playback2DKeymapProfile.FromOverrides(rows, out _);
            await Assert.That(profile.GestureText(Playback2DAction.NextRound)).IsEqualTo("Shift+R");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Rebind_PersistsOneRow_AndTheRowReportsIt()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                KeybindRow row = Row(vm, Playback2DAction.NextRound);
                await Assert.That(row.Gesture).IsEqualTo("E");

                Capture(vm, row, Key.R, KeyModifiers.Shift);

                string[] expected = ["NextRound=Shift+R"];
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEquivalentTo(expected);
                await Assert.That(row.Gesture).IsEqualTo("Shift+R");
                await Assert.That(row.IsOverridden).IsTrue();
                await Assert.That(row.Conflict).IsEqualTo("");
                await Assert.That(vm.CustomKeybindCount).IsEqualTo(1);
                await Assert.That(vm.HasCustomKeybinds).IsTrue();
                await Assert.That(vm.CapturingKeybind).IsNull();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A refused rebind must say WHY and leave the file alone. Silently writing a row the loader will
    ///     drop is the failure this whole validate-first path exists to prevent.
    /// </summary>
    [Test]
    public async Task ConflictingRebind_IsRefusedWithAReason_AndPersistsNothing()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                KeybindRow row = Row(vm, Playback2DAction.NextRound);

                // Ctrl+O is MainView.axaml's Open — a shell accelerator the tab must never shadow.
                Capture(vm, row, Key.O, KeyModifiers.Control);
                Console.WriteLine($"[keybind-ui] shell refusal: {row.Conflict}");
                await Assert.That(row.Conflict).Contains("app-wide");
                await Assert.That(row.HasConflict).IsTrue();
                await Assert.That(row.Gesture).IsEqualTo("E");
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();

                // …and a key another action already owns inside the tab.
                Capture(vm, row, Key.D, KeyModifiers.None);
                Console.WriteLine($"[keybind-ui] duplicate refusal: {row.Conflict}");
                await Assert.That(row.Conflict).Contains("ToolDraw");
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();

                // A good one afterwards clears the reason rather than stacking on it.
                Capture(vm, row, Key.R, KeyModifiers.Shift);
                await Assert.That(row.Conflict).IsEqualTo("");
                await Assert.That(row.HasConflict).IsFalse();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     Modifiers arrive as their own key events a moment before the gesture does, so capturing on one
    ///     would make every modified binding impossible to enter. Esc is the way out of the mode.
    /// </summary>
    [Test]
    public async Task Capture_IgnoresBareModifiers_AndEscapeBacksOut()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                KeybindRow row = Row(vm, Playback2DAction.NextRound);
                vm.BeginKeybindCapture(row);
                await Assert.That(row.IsCapturing).IsTrue();
                await Assert.That(row.CaptureLabel).IsEqualTo("press a key…");

                await Assert.That(vm.HandleKeybindCapture(Key.LeftCtrl, KeyModifiers.Control)).IsTrue();
                await Assert.That(vm.CapturingKeybind).IsSameReferenceAs(row)
                    .Because("Ctrl is the first half of Ctrl+Y, not a binding on its own");

                await Assert.That(vm.HandleKeybindCapture(Key.Escape, KeyModifiers.None)).IsTrue();
                await Assert.That(vm.CapturingKeybind).IsNull();
                await Assert.That(row.IsCapturing).IsFalse();
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();

                // Un-armed, the handler consumes nothing — the search box has to keep working.
                await Assert.That(vm.HandleKeybindCapture(Key.R, KeyModifiers.None)).IsFalse();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task ResetRow_AndResetAll_ReturnToTheShippedGestures()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                KeybindRow next = Row(vm, Playback2DAction.NextRound);
                KeybindRow prev = Row(vm, Playback2DAction.PrevRound);
                Capture(vm, next, Key.R, KeyModifiers.Shift);
                Capture(vm, prev, Key.T, KeyModifiers.Shift);
                await Assert.That(vm.CustomKeybindCount).IsEqualTo(2);

                vm.ResetKeybind(next);
                await Assert.That(next.Gesture).IsEqualTo("E");
                await Assert.That(next.IsOverridden).IsFalse();
                string[] remaining = ["PrevRound=Shift+T"];
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEquivalentTo(remaining);

                vm.ResetAllKeybindsCommand.Execute(null);
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();
                await Assert.That(prev.Gesture).IsEqualTo("Q");
                await Assert.That(vm.CustomKeybindCount).IsEqualTo(0);

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     An override is a promise to keep that key even if the shipped default moves later. Pressing the
    ///     key that was already there is not that promise, so it clears the row instead of storing it.
    /// </summary>
    [Test]
    public async Task RebindingBackToTheShippedGesture_DropsTheRow()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                KeybindRow row = Row(vm, Playback2DAction.NextRound);
                Capture(vm, row, Key.R, KeyModifiers.Shift);
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsNotEmpty();

                Capture(vm, row, Key.E, KeyModifiers.None);
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();
                await Assert.That(row.IsOverridden).IsFalse();
                await Assert.That(row.Gesture).IsEqualTo("E");

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task ReservedAction_IsListedButNotBindable()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                KeybindRow fit = Row(vm, Playback2DAction.FitCamera);
                await Assert.That(fit.IsReserved).IsTrue();
                await Assert.That(fit.IsBindable).IsFalse()
                    .Because("hiding it would make Home look free, which is the opposite of reserved");

                vm.BeginKeybindCapture(fit);
                await Assert.That(vm.CapturingKeybind).IsNull();
                await Assert.That(svc.Current.Playback2D.KeybindOverrides).IsEmpty();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The only way the rejection note is ever non-empty: somebody edited the file by hand. The rows
    ///     still show the shipped gestures, and the screen still works.
    /// </summary>
    [Test]
    public async Task AHandEditedBadRow_IsReportedInTheSection_AndTheRowsStillResolve()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService seed = new(dir);
            seed.Write(s => s.Playback2D.KeybindOverrides = ["NextRound=Ctrl+O", "Teleport=Y"]);

            (SettingsViewModel vm, SettingsService _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                Console.WriteLine($"[keybind-ui] note: {vm.KeybindRejectionNote}");
                await Assert.That(vm.HasKeybindRejections).IsTrue();
                await Assert.That(vm.KeybindRejectionNote).Contains("NextRound=Ctrl+O");
                await Assert.That(vm.KeybindRejectionNote).Contains("Teleport=Y");
                await Assert.That(Row(vm, Playback2DAction.NextRound).Gesture).IsEqualTo("E");
                await Assert.That(vm.CustomKeybindCount).IsEqualTo(0);

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     Another writer's rebind (a second Settings surface, a hand-edited file) reflects back into the
    ///     rows. Runs on the UI thread so the marshalled reflect lands inline.
    /// </summary>
    [Test]
    public async Task ExternalWrite_RefreshesTheKeybindRows()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                (SettingsViewModel vm, SettingsService svc, ServiceProvider sp) = NewVm(dir);
                using (sp)
                {
                    svc.Write(s => s.Playback2D.KeybindOverrides = ["NextRound=Shift+R"]);

                    await Assert.That(Row(vm, Playback2DAction.NextRound).Gesture).IsEqualTo("Shift+R");
                    await Assert.That(vm.CustomKeybindCount).IsEqualTo(1);

                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>The section joins the fuzzy filter and the GENERAL group like every other one.</summary>
    [Test]
    public async Task TheSection_IsFindableByTheSettingsFilter()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel vm, SettingsService _, ServiceProvider sp) = NewVm(dir);
            using (sp)
            {
                vm.SettingsFilterText = "keybind";
                await Assert.That(vm.ShowSectionPlayback2DKeys).IsTrue();
                await Assert.That(vm.ShowGroupGeneral).IsTrue()
                    .Because("a section whose group never shows is a section nobody can reach");
                await Assert.That(vm.ShowSectionFolders).IsFalse();

                vm.SettingsFilterText = "";
                await Assert.That(vm.ShowSectionPlayback2DKeys).IsTrue();

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Arms the row and feeds it one keypress — the exact path SettingsView's tunnelling KeyDown handler
    // drives, minus the visual tree.
    private static void Capture(SettingsViewModel vm, KeybindRow row, Key key, KeyModifiers modifiers)
    {
        vm.BeginKeybindCapture(row);
        vm.HandleKeybindCapture(key, modifiers);
    }

    private static KeybindRow Row(SettingsViewModel vm, Playback2DAction action) =>
        vm.Playback2DKeybindRows.First(r => r.Action == action);

    // Mirrors SettingsViewModelTests.NewVm: a real SettingsService over a throwaway dir, an options
    // monitor over its live configuration, and a gate with UI-thread marshaling off so it stays inline.
    private static (SettingsViewModel Vm, SettingsService Svc, ServiceProvider Sp) NewVm(string dir)
    {
        SettingsService svc = new(dir);
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        services.AddSingleton<IFeatureGate>(s =>
            new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
        ServiceProvider sp = services.BuildServiceProvider();

        SettingsViewModel vm = new(svc, sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
            sp.GetRequiredService<IFeatureGate>(), new ThemeRegistry());
        return (vm, svc, sp);
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvkeybinds_" + Guid.NewGuid().ToString("N"));
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
