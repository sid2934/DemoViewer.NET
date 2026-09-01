#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Update;
using DemoViewer.NET.Views;
using DemoViewer.NET.Views.Settings;
using DemoViewer.NET.Views.Setup;
using DemoViewer.NET.Views.Update;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Abstracts OS-window spawning so view-models no longer reach into
///     code-behind. Desktop opens a real <see cref="ParseChainWindow" />; the browser host has no
///     OS windows, so its implementation no-ops (parse chain) or degrades to an in-app overlay (Settings).
/// </summary>
public interface IWindowService
{
    /// <summary>Opens the parse-chain inspector (separate window on desktop, no-op on WASM).</summary>
    void OpenParseChainInspector(object dataContext);

    /// <summary>
    ///     Opens the Settings screen for <paramref name="viewModel" />: a separate
    ///     <see cref="SettingsWindow" /> on desktop, an in-app overlay on WASM. The service takes ownership
    ///     of <paramref name="viewModel" />'s disposal (it is a fresh VM per open).
    /// </summary>
    void OpenSettings(SettingsViewModel viewModel);

    /// <summary>
    ///     Shows the first-run setup wizard for <paramref name="viewModel" />: a MODAL
    ///     <see cref="FirstRunWizardWindow" /> owned by the main window on desktop (setup completes before
    ///     the app is used), an in-app overlay on WASM. The wizard closes itself on its
    ///     <c>Completed</c> event (Finish / Skip).
    /// </summary>
    void ShowFirstRunWizard(FirstRunWizardViewModel viewModel);

    /// <summary>
    ///     Shows the update-notice pop-up (version, release notes, Update &amp; Restart / Later):
    ///     a non-modal <see cref="UpdateNoticeWindow" /> on desktop. No-op on WASM: the browser
    ///     head has no installed build to update, so the notice can never be reached there.
    /// </summary>
    void ShowUpdateNotice(UpdateNoticeViewModel viewModel);

    /// <summary>
    ///     Shows the post-update "What's new" window: a non-modal <see cref="WhatsNewWindow" />
    ///     on desktop. No-op on WASM (version is per-deploy there; there is no "first launch after
    ///     an update" moment to gate on).
    /// </summary>
    void ShowWhatsNew(WhatsNewViewModel viewModel);
}

/// <summary>
///     Desktop <see cref="IWindowService" />: spawns a single re-used <see cref="ParseChainWindow" />.
///     <para>
///         Lives in the shared <c>DemoViewer.NET</c> project (not the Desktop host) because
///         <see cref="ParseChainWindow" /> is a shared <see cref="Window" /> and <c>App.axaml.cs</c>, which
///         constructs the service per lifetime, is itself in the shared assembly and cannot reference the
///         Desktop host project. The lifetime branch in <c>App</c> selects this only under the classic
///         desktop lifetime.
///     </para>
/// </summary>
public sealed class DesktopWindowService(Func<Window?> ownerLookup) : IWindowService
{
    private ParseChainWindow? _parseChainWindow;
    private SettingsWindow? _settingsWindow;
    private UpdateNoticeWindow? _updateNoticeWindow;
    private WhatsNewWindow? _whatsNewWindow;

    /// <summary>Open parse chain inspector.</summary>
    public void OpenParseChainInspector(object dataContext)
    {
        if (_parseChainWindow is not null)
        {
            _parseChainWindow.Activate();
            return;
        }

        _parseChainWindow = new ParseChainWindow
        {
            DataContext = dataContext
        };
        _parseChainWindow.Closed += (_, _) => _parseChainWindow = null;

        ShowOwned(_parseChainWindow);
    }

    /// <summary>Open the Settings screen as a non-modal window owned by the main window.</summary>
    public void OpenSettings(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // Single re-used window (mirrors the parse-chain pattern). Already open → the live window's VM still
        // tracks settings, so the freshly-built VM is redundant: dispose it (release its OnChange
        // subscription) and re-focus the existing window.
        if (_settingsWindow is not null)
        {
            viewModel.Dispose();
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow
        {
            DataContext = viewModel
        };

        void OnCloseRequested(object? sender, EventArgs e)
        {
            _settingsWindow?.Close();
        }

        viewModel.CloseRequested += OnCloseRequested;
        _settingsWindow.Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Dispose();
            _settingsWindow = null;
        };

        ShowOwned(_settingsWindow);
    }

    /// <summary>
    ///     Shows the update-notice pop-up, single re-used window (the parse-chain pattern): the shell
    ///     keeps ONE <see cref="UpdateNoticeViewModel" /> per run, so re-showing an open window just
    ///     re-activates it. Non-modal on purpose: an update is never urgent enough to block work.
    /// </summary>
    public void ShowUpdateNotice(UpdateNoticeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (_updateNoticeWindow is not null)
        {
            _updateNoticeWindow.Activate();
            return;
        }

        _updateNoticeWindow = new UpdateNoticeWindow
        {
            DataContext = viewModel
        };

        void OnCloseRequested(object? sender, EventArgs e)
        {
            _updateNoticeWindow?.Close();
        }

        viewModel.CloseRequested += OnCloseRequested;
        _updateNoticeWindow.Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            _updateNoticeWindow = null;
        };

        ShowOwned(_updateNoticeWindow);
    }

    /// <summary>
    ///     Shows the "What's new" window (non-modal, owned). Shown at most once per launch by the
    ///     shell's version gate, so no re-activate path is needed, but it keeps the single-window
    ///     guard anyway for symmetry with every other window here.
    /// </summary>
    public void ShowWhatsNew(WhatsNewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (_whatsNewWindow is not null)
        {
            _whatsNewWindow.Activate();
            return;
        }

        _whatsNewWindow = new WhatsNewWindow
        {
            DataContext = viewModel
        };

        void OnCloseRequested(object? sender, EventArgs e)
        {
            _whatsNewWindow?.Close();
        }

        viewModel.CloseRequested += OnCloseRequested;
        _whatsNewWindow.Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            _whatsNewWindow = null;
        };

        ShowOwned(_whatsNewWindow);
    }

    /// <summary>
    ///     Shows the first-run wizard as a MODAL dialog owned by the main window, so the user completes
    ///     setup before returning to the app. The window closes on the wizard's Completed event.
    /// </summary>
    public void ShowFirstRunWizard(FirstRunWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        FirstRunWizardWindow window = new()
        {
            DataContext = viewModel
        };

        void OnCompleted(object? sender, EventArgs e)
        {
            window.Close();
        }

        viewModel.Completed += OnCompleted;
        window.Closed += (_, _) => viewModel.Completed -= OnCompleted;

        Window? owner = Owner();
        if (owner is not null)
        {
            // Modal: the owner is disabled until setup is finished / skipped. Fire-and-forget the dialog
            // task: completion is driven by the Completed → Close wiring above.
            _ = window.ShowDialog(owner);
        }
        else
        {
            // No showable owner (should not happen at the launch trigger, which waits for
            // MainWindow.Opened): fall back to a non-modal show rather than throwing.
            window.Show();
        }
    }

    // Shared non-modal show: owned by the main window when it is available (keeps the pop-up above
    // the shell and closing with it), free-standing otherwise.
    private void ShowOwned(Window window)
    {
        Window? owner = Owner();
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    ///     The main window, but ONLY when it can legally act as an owner. Avalonia throws
    ///     <see cref="InvalidOperationException" /> ("Cannot show window with non-visible owner") from
    ///     both <c>Show(owner)</c> and <c>ShowDialog(owner)</c> if the owner has not been shown yet or
    ///     has already closed, and the lookup hands back the main window from the moment it is
    ///     constructed, well before it is shown. Callers that reach this during framework-init (or
    ///     after the shell closes) therefore get <c>null</c> and fall back to a free-standing show,
    ///     which is a cosmetically worse pop-up but never a crash.
    /// </summary>
    private Window? Owner() => ownerLookup() is { IsVisible: true } owner ? owner : null;
}

/// <summary>
///     Browser <see cref="IWindowService" />: the WASM host has no OS windows, so opening a separate
///     parse-chain window is unsupported. No-ops for now; an in-app overlay can replace this later
///     (the design doc sketched a <c>ShellOverlayService</c>, which does not yet exist).
/// </summary>
public sealed class BrowserWindowService : IWindowService
{
    /// <summary>
    ///     Wired by the WASM shell bootstrap (<c>App.axaml.cs</c>) to surface the Settings screen as an
    ///     in-app overlay on the shell. Null until wired.
    /// </summary>
    public Action<SettingsViewModel>? OnOpenSettings { get; set; }

    /// <summary>
    ///     Wired by the WASM shell bootstrap (<c>App.axaml.cs</c>) to surface the first-run wizard as an
    ///     in-app overlay on the shell. Null until wired. Note: the wizard is NOT auto-triggered on WASM
    ///     (no persisted file means <c>NeedsFirstRun</c> is always true, which would loop every page load);
    ///     it is reachable only via the "Re-run first-time setup" affordance in Settings.
    /// </summary>
    public Action<FirstRunWizardViewModel>? OnShowFirstRun { get; set; }

    /// <summary>Open parse chain inspector.</summary>
    public void OpenParseChainInspector(object dataContext)
    {
        // No OS windows on WASM. Intentionally no-op: the parse-chain strip remains visible
        // inline in the shell, so no functionality is lost on the browser host.
    }

    /// <summary>Surface the Settings screen as an in-app overlay (no OS windows on WASM).</summary>
    public void OpenSettings(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (OnOpenSettings is not null)
        {
            OnOpenSettings(viewModel);
        }
        else
        {
            // No shell wired (should not happen in the real host): release the VM's subscription.
            viewModel.Dispose();
        }
    }

    /// <summary>Surface the first-run wizard as an in-app overlay (no OS windows on WASM).</summary>
    public void ShowFirstRunWizard(FirstRunWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // No shell wired (should not happen in the real host): nothing to surface it on. The wizard VM is
        // not IDisposable, so there is nothing to release.
        OnShowFirstRun?.Invoke(viewModel);
    }

    /// <summary>No-op: the browser head has no installed build, so updates never surface there.</summary>
    public void ShowUpdateNotice(UpdateNoticeViewModel viewModel)
    {
        // Intentionally empty: UpdateViewModel.IsSupported is false on WASM, so this is unreachable
        // in practice; the no-op keeps the interface total rather than throwing.
    }

    /// <summary>No-op: WASM deploys have no "first launch after an update" moment to gate on.</summary>
    public void ShowWhatsNew(WhatsNewViewModel viewModel)
    {
        // Intentionally empty; see ShowUpdateNotice.
    }
}
