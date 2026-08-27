#region

using Avalonia.Controls;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.Views.Playback2D;
using DemoViewer.NET.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>The browser head, and telling the user the truth about it.</b>
///     <para>
///         The WASM gate itself is complete: a sweep found no ungated desktop-only capability. Every
///         defect here is about a surface that behaves differently in the browser and says nothing. A
///         grep of every Settings view for <c>session only|not saved|forgets|reload</c> returned <b>zero
///         hits</b> at one point, even though the same sentence already existed for annotations.
///     </para>
///     <para>
///         <c>OperatingSystem.IsBrowser()</c> is a JIT-folded intrinsic that cannot be faked from
///         outside, so every surface under test takes an injected host predicate — the same seam
///         <c>ShellModuleFeatureGate</c> and <c>AnnotationSessionController</c> use. Without it, none of
///         these sentences are exercised by anything.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class Playback2DBrowserHonestyTests
{
    /// <summary>
    ///     <b>Keybinding overrides are memory-only in the browser.</b> <c>SettingsService</c> takes
    ///     its fileless in-memory provider on the browser head, so a user can rebind twenty gestures,
    ///     watch every one apply live, and lose the lot on refresh. The same fix already shipped for
    ///     annotations; keybindings shipped a new persisted surface without repeating it.
    /// </summary>
    [Test]
    public async Task Keybindings_SayTheyAreSessionOnly_OnTheBrowserHeadAndNowhereElse()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel browser, ServiceProvider spB) = NewVm(dir, isBrowser: true);
            using (spB)
            {
                Console.WriteLine($"[wasm-honesty] browser note='{browser.KeybindPersistenceNote}'");

                await Assert.That(browser.KeybindsPersist).IsFalse();
                await Assert.That(browser.KeybindPersistenceNote).IsNotEmpty()
                    .Because("a rebind that dies on refresh with nothing said is exactly the defect this "
                             + "guards against");
                await Assert.That(browser.KeybindPersistenceNote).Contains("Session only");
                await Assert.That(browser.KeybindPersistenceNote).Contains("reload")
                    .Because("the annotation sentence names the reload, because that is the moment the "
                             + "user loses the work");
            }

            (SettingsViewModel desktop, ServiceProvider spD) = NewVm(dir, isBrowser: false);
            using (spD)
            {
                await Assert.That(desktop.KeybindsPersist).IsTrue();
                await Assert.That(desktop.KeybindPersistenceNote).IsEmpty()
                    .Because("on desktop it is not a caveat, and a permanent warning is noise that "
                             + "teaches people to stop reading warnings");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     <b>The rejection banner named a file the browser does not have.</b> It read "Some keybinding
    ///     overrides in settings.json were ignored" on a head where no such file exists anywhere.
    /// </summary>
    [Test]
    public async Task TheRejectionBanner_NamesSettingsJson_OnlyWhereThereIsOne()
    {
        string dir = NewTempDir();
        try
        {
            (SettingsViewModel browser, ServiceProvider spB) = NewVm(dir, isBrowser: true);
            using (spB)
            {
                Console.WriteLine($"[wasm-honesty] browser rejection='{browser.KeybindRejectionSource}'");
                await Assert.That(browser.KeybindRejectionSource).DoesNotContain("settings.json");
                await Assert.That(browser.KeybindRejectionSource).Contains("ignored")
                    .Because("the message still has to say what happened, only not where from");
            }

            (SettingsViewModel desktop, ServiceProvider spD) = NewVm(dir, isBrowser: false);
            using (spD)
            {
                await Assert.That(desktop.KeybindRejectionSource).Contains("settings.json")
                    .Because("on desktop the file IS the answer to 'where do I fix this'");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     <b>The Settings feature list binds the raw <c>IFeatureGate</c>, which knows nothing about the
    ///     platform.</b> Modules read the same ids through <c>ShellModuleFeatureGate</c>, whose
    ///     <c>DesktopOnlyIds</c> forces a set of them off on the browser — so the browser showed a live,
    ///     ON "Video export" toggle for a capability refused one layer out, and flipping it persisted an
    ///     override nothing would ever honour. This was a known gap that shipped unfixed.
    /// </summary>
    [Test]
    public async Task TheFeatureList_ShowsADesktopOnlyFeatureAsUnavailable_OnTheBrowserHead()
    {
        string dir = NewTempDir();
        try
        {
            const string exportId = "playback2d.export";
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds.Contains(exportId)).IsTrue()
                .Because("this suite is asserting about THE desktop-only id, not a spelling of it");

            (SettingsViewModel browser, ServiceProvider spB, SettingsService svc) =
                NewVmWithService(dir, isBrowser: true);
            using (spB)
            {
                FeatureToggleRow row = Row(browser, exportId);
                Console.WriteLine($"[wasm-honesty] browser row: enabled={row.IsEnabled} "
                                  + $"interactive={row.IsInteractive} hint='{row.LockHint}'");

                await Assert.That(row.IsPlatformUnavailable).IsTrue();
                await Assert.That(row.IsEnabled).IsFalse()
                    .Because("the module's own gate answers false, and a list that disagrees with it is "
                             + "showing the user a capability they do not have");
                await Assert.That(row.IsInteractive).IsFalse();
                await Assert.That(row.LockHint).Contains("browser");

                // And the toggle refuses a programmatic flip rather than persisting a phantom override
                // that would follow the user to a desktop head they never asked it on.
                row.IsEnabled = true;
                await Assert.That(row.IsEnabled).IsFalse();
                await Assert.That(svc.Current.Features.Overrides.ContainsKey(exportId)).IsFalse()
                    .Because("an override that can never take effect is a preference nobody expressed");
            }

            (SettingsViewModel desktop, ServiceProvider spD) = NewVm(dir, isBrowser: false);
            using (spD)
            {
                FeatureToggleRow row = Row(desktop, exportId);
                await Assert.That(row.IsPlatformUnavailable).IsFalse();
                await Assert.That(row.IsInteractive).IsTrue()
                    .Because("on desktop it is an ordinary, switchable feature");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     <b>The export button vanished with no explanation</b>, and the SAME binding hides it on desktop
    ///     when no demo is open — so a browser user could not tell "not available here" from "open a demo
    ///     first". The codebase does this correctly elsewhere: the Settings folder picker says
    ///     "(unavailable in the browser)" rather than disappearing.
    /// </summary>
    [Test]
    public async Task TheExportButtonsAbsence_SaysWhichAbsenceItIs()
    {
        (Playback2DTabViewModel browser, Playback2DFakeContext browserCtx) =
            Playback2DActionDispatchTests.Activated();
        browser.IsBrowserHost = static () => true;
        browserCtx.Gate!.SetEnabled("playback2d.export", true);
        browser.OnActivated(browserCtx); // re-run the gate sweep under the browser predicate

        Console.WriteLine($"[wasm-honesty] browser export note='{browser.ExportUnavailableNote}'");
        await Assert.That(browser.CanExport).IsFalse();
        await Assert.That(browser.HasExportUnavailableNote).IsTrue();
        await Assert.That(browser.ExportUnavailableNote).Contains("browser");

        // Desktop, feature on, no demo yet: a DIFFERENT sentence, which is the whole point — the two
        // absences were indistinguishable, and only one of them is something the user can act on.
        (Playback2DTabViewModel desktop, Playback2DFakeContext desktopCtx) =
            Playback2DActionDispatchTests.Activated();
        desktop.IsBrowserHost = static () => false;
        desktopCtx.HasDemo = false;
        desktop.OnActivated(desktopCtx);

        Console.WriteLine($"[wasm-honesty] desktop-no-demo note='{desktop.ExportUnavailableNote}'");
        await Assert.That(desktop.ExportUnavailableNote).DoesNotContain("browser");
        await Assert.That(desktop.ExportUnavailableNote).Contains("open a demo");
        await Assert.That(desktop.ExportUnavailableNote).IsNotEqualTo(browser.ExportUnavailableNote)
            .Because("one note for both is the silence this replaced, spelled differently");

        // And the note goes away the moment export becomes possible, or it is a permanent lie.
        desktopCtx.HasDemo = true;
        desktop.OnActivated(desktopCtx);
        await Assert.That(desktop.HasExportUnavailableNote).IsFalse();
    }

    /// <summary>
    ///     The view has somewhere to render it. A string property nothing binds is the same defect as the
    ///     silence it replaced, one indirection further out.
    /// </summary>
    [Test]
    public async Task TheExportViewCarriesTheLabel_InTheButtonsOwnSlot()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            vm.IsBrowserHost = static () => true;
            vm.OnActivated(ctx);

            (Window _, Playback2DView view) =
                Playback2DTimelineHarness.Show(vm, renderer: Playback2DRendererKind.Scene);
            Playback2DTimelineHarness.Pump();

            TextBlock label = view.FindControl<TextBlock>("ExportUnavailableLabel")
                              ?? throw new InvalidOperationException(
                                  "ExportUnavailableLabel is not in the view — the note has no surface.");
            Button button = view.FindControl<Button>("ExportButton")
                            ?? throw new InvalidOperationException("ExportButton is gone.");

            Console.WriteLine($"[wasm-honesty] label visible={label.IsEffectivelyVisible} "
                              + $"text='{label.Text}' button visible={button.IsEffectivelyVisible}");

            await Assert.That(button.IsEffectivelyVisible).IsFalse();
            await Assert.That(label.IsEffectivelyVisible).IsTrue()
                .Because("exactly one of the two is on screen, and the user is never shown neither");
            await Assert.That(label.Text).Contains("browser");
        });
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static (SettingsViewModel Vm, ServiceProvider Sp) NewVm(string dir, bool isBrowser)
    {
        (SettingsViewModel vm, ServiceProvider sp, SettingsService _) = NewVmWithService(dir, isBrowser);
        return (vm, sp);
    }

    // The gate is marshal-DISABLED so its Changed fires inline off the UI thread, exactly as
    // SettingsViewModelTests builds one; the service comes back because a persisted override has to be
    // asserted against the file, not against the row that would have written it.
    private static (SettingsViewModel Vm, ServiceProvider Sp, SettingsService Svc) NewVmWithService(
        string dir, bool isBrowser)
    {
        SettingsService svc = new(dir);
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        services.AddSingleton<IFeatureGate>(s =>
            new FeatureGate(s.GetRequiredService<IOptionsMonitor<AppSettings>>(), false));
        ServiceProvider sp = services.BuildServiceProvider();

        SettingsViewModel vm = new(svc, sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
            sp.GetRequiredService<IFeatureGate>(), new ThemeRegistry(),
            isBrowser ? static () => true : static () => false);
        return (vm, sp, svc);
    }

    private static FeatureToggleRow Row(SettingsViewModel vm, string featureId) =>
        vm.TabFeatureRows.Concat(vm.ChromeFeatureRows).First(r => r.FeatureId == featureId);

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-wasm-honesty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir that outlives the test is noise, not a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
