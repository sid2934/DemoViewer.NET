#region

using System.ComponentModel;
using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>Where a tab sits in the shell.</summary>
public enum TabPlacement
{
    /// <summary>The main tab strip.</summary>
    Main,

    /// <summary>The diagnostics group (rendered after Main tabs).</summary>
    Diagnostics
}

/// <summary>
///     The unit of placement and lifecycle (reconciled with the verified codebase
///     constraint that the three shell-routed built-in views bind against the shell, not a per-tab VM).
///     <para>
///         <b>DataContext model.</b> The realized View's DataContext is the descriptor's
///         <see cref="DataContext" /> object — for the shell-routed built-ins this is the shell
///         (so <c>{Binding EntityTab.X}</c> resolves exactly as before); for Diagnostics it is the
///         Diagnostics VM; for a future third-party module it is that module's per-tab VM. The
///         descriptor's own header binds in the ItemTemplate (kept separate).
///     </para>
///     <para>
///         <b>Lifecycle.</b> <see cref="ActiveContent" /> (the realized View) is built on activation
///         via <see cref="ViewFactory" /> and dropped on deactivation, preserving the inactive-content
///         unload invariant. The optional <see cref="TabViewModel" /> receives
///         <c>OnActivated</c>/<c>OnDeactivated</c>.
///     </para>
/// </summary>
public sealed class WorkspaceTabDescriptor : INotifyPropertyChanged
{
    private Control? _activeContent;

    /// <summary>Unique id within the module (e.g. <c>"builtin.parser"</c>). Session-persistence key.</summary>
    public required string TabId { get; init; }

    /// <summary>Tab header text.</summary>
    public required string Header { get; init; }

    /// <summary>Optional header icon (Geometry / StreamGeometry / path key).</summary>
    public object? Icon { get; init; }

    /// <summary>Sort key within (Placement) in the tab strip.</summary>
    public int Order { get; init; }

    /// <summary>Where the tab sits.</summary>
    public TabPlacement Placement { get; init; } = TabPlacement.Main;

    /// <summary>
    ///     The DataContext assigned to the realized View. For shell-routed built-ins this is the shell
    ///     itself (so the existing <c>{Binding TabVM.X}</c> bindings keep resolving); for module-owned
    ///     tabs it is the per-tab VM. Set directly when the VM is pre-built (the built-in case), or
    ///     left null and produced lazily via <see cref="TabViewModel" />.
    /// </summary>
    public object? DataContext { get; init; }

    /// <summary>
    ///     Optional lazily-built per-tab VM (state retention). Built on first activation and
    ///     retained. When set and <see cref="DataContext" /> is null, the realized View's DataContext
    ///     is this VM. Receives <c>OnActivated</c>/<c>OnDeactivated</c> if it implements
    ///     <see cref="IWorkspaceTabViewModel" />.
    /// </summary>
    public Func<IWorkspaceTabViewModel>? ViewModelFactory { get; init; }

    /// <summary>Realizes the tab's View on each activation. Dropped on deactivation.</summary>
    public required Func<Control> ViewFactory { get; init; }

    /// <summary>
    ///     The realized View while this tab is active; null when inactive (so the View is collectible
    ///     and out of the visual tree). Bound by the shell's ContentTemplate.
    /// </summary>
    public Control? ActiveContent
    {
        get => _activeContent;
        private set
        {
            if (ReferenceEquals(_activeContent, value))
            {
                return;
            }

            _activeContent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveContent)));
        }
    }

    /// <summary>The cached per-tab VM (built on first activation), if any.</summary>
    public IWorkspaceTabViewModel? TabViewModel { get; private set; }

    /// <summary>
    ///     Session state restored from disk, waiting for this tab's VM to exist.
    ///     <para>
    ///         Module tab VMs are built LAZILY, on first activation — that is the point of
    ///         <see cref="ViewModelFactory" />. So a restore at startup has nothing to hand the state to, and
    ///         forcing every module VM into existence just to restore it would defeat the laziness and pay
    ///         every module's construction cost on every launch. The shell parks the state here instead and
    ///         <see cref="Activate" /> applies it the first time the VM is actually built.
    ///     </para>
    ///     <para>
    ///         Consumed exactly once: a tab the user never opens simply never restores, and re-activating a
    ///         tab later in the same session must not re-apply a stale startup snapshot over live state.
    ///     </para>
    /// </summary>
    public object? PendingRestoreState { get; set; }

    /// <summary>True while this descriptor is the selected tab.</summary>
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     Activates the tab: builds the cached VM on first use, realizes the View, sets its
    ///     DataContext (the shell or the per-tab VM), and calls <c>OnActivated</c>. Idempotent.
    /// </summary>
    public void Activate(IModuleContext context)
    {
        if (IsActive)
        {
            return;
        }

        bool built = TabViewModel is null;
        TabViewModel ??= ViewModelFactory?.Invoke();

        // Apply a startup snapshot at the only moment the VM exists to receive it. Guarded on "we just built
        // it" so a later re-activation cannot replay a stale snapshot over state the user has since changed.
        if (built && TabViewModel is not null && PendingRestoreState is { } pending)
        {
            PendingRestoreState = null;
            try
            {
                TabViewModel.RestoreState(pending);
            }
            catch (Exception)
            {
                // A restore that throws must never cost the user the TAB. Session state is a convenience,
                // not a source of truth, and the blob is attacker-free but not shape-free: it was written by
                // a previous BUILD, so a renamed field or a changed record shape lands here as a
                // JsonException. The contract asks modules to tolerate that themselves; this is the backstop
                // for the ones that do not, because the alternative is a tab that cannot be opened — or, if
                // the tab is the restored active one, a launch that fails.
            }
        }

        Control view = ViewFactory();
        view.DataContext = DataContext ?? TabViewModel;
        ActiveContent = view;

        TabViewModel?.OnActivated(context);
        IsActive = true;
    }

    /// <summary>
    ///     Deactivates the tab: calls <c>OnDeactivated</c> and drops the realized View so it is
    ///     collectible (the inactive-content-unload invariant). The cached VM is retained for state
    ///     retention. Idempotent.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        TabViewModel?.OnDeactivated();

        // Only null the View's own DataContext when it owns a per-tab VM. For shell-routed built-ins
        // the DataContext is the shared shell — nulling it would churn a reference still alive
        // elsewhere; just drop the View and let the presenter collect it.
        if (ActiveContent is { } view && DataContext is null)
        {
            view.DataContext = null;
        }

        ActiveContent = null;
        IsActive = false;
    }
}
