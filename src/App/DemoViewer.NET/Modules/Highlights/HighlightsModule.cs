#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.Views.Highlights;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>
///     The Reels tab module.
///     Contributes one Main-strip tab (<c>"highlights.browser"</c>, Order 3 — after Stats) whose VM is the
///     reel-authoring dashboard: an ordered clip tray plus the promoted reel configuration pane.
///     <para>
///         <b>The header is display text; the ids are persisted keys.</b> The header reads "Reels",
///         but <c>TabId "highlights.browser"</c> and the feature id <c>"tab.highlights"</c> do NOT change —
///         they key the user's per-tab session state and their feature overrides, so renaming either silently
///         resets both. This mismatch is deliberate, not drift.
///     </para>
///     <para>
///         <b>Wiring contract.</b> The descriptor uses
///         <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy + retained) — NOT
///         <c>DataContext</c> — so <c>Activate()</c> builds the VM and drives its
///         <c>OnActivated</c>/<c>OnDeactivated</c> lifecycle (the tab-activation staleness trigger).
///         The VM is <b>delegate-injected</b> (Library precedent): the composition root supplies the cache
///         store, scanner, and settings directly, and the shell-bound behaviours (open-in-workspace, Live
///         Sync verify) as lazily-resolved delegates — the module itself references no shell.
///     </para>
/// </summary>
public sealed class HighlightsModule : IWorkspaceModule
{
    private readonly Func<HighlightsTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">
    ///     Builds the tab VM on first activation. Constructed at the composition root
    ///     (<c>App.BuildRegistry</c>) with the DI-resolved cache store / scanner / settings and the
    ///     lazily-resolved shell delegates.
    /// </param>
    public HighlightsModule(Func<HighlightsTabViewModel> viewModelFactory) =>
        _viewModelFactory = viewModelFactory;

    public string Id => "net.demoviewer.highlights";
    public string DisplayName => "Reels";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = "highlights.browser",
            // Display text only — see the type comment. TabId below is the persisted key and is unchanged.
            Header = "Reels",
            Order = 3, // after Stats (2); ties with Analysis (3) resolve by registration order
            Placement = TabPlacement.Main,
            // ViewModelFactory (LAZY + RETAINED) — never DataContext (the DataContext branch skips the
            // OnActivated lifecycle the staleness trigger relies on).
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new HighlightsTabView()
        };
    }
}
