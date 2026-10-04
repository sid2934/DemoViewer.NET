#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.RoundTagger;
using DemoViewer.NET.Views.RoundTagger;

#endregion

namespace DemoViewer.NET.Modules.RoundTagger;

/// <summary>
///     The Round Tagger module: the home of the Tag Palette hosted by the 2D Playback
///     tab and of The Matrix, the Tags section of the Strat Book tab's rail (<see cref="MatrixTabId" />,
///     after Situations).
///     <para>
///         The Tag Palette (<c>Palette/TagPaletteViewModel</c>) docks in the 2D Playback tab, which owns the
///         tag session it writes through, under <see cref="PaletteFeatureId" />; this module contributes only
///         The Matrix. The module id and the two feature ids are persisted keys (the user's per-tab session
///         state and <c>Features:Overrides:{id}</c>) and never move.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and retained),
///         never <c>DataContext</c>, with the VM delegate-injected at the composition root, the Highlights
///         precedent; the module references no shell.
///     </para>
/// </summary>
public sealed class RoundTaggerModule : IWorkspaceModule
{
    /// <summary>The Matrix tab's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.tagger";

    /// <summary>The Tag Palette's feature id, a sub-feature of the 2D Playback tab. A persisted key; never renamed.</summary>
    public const string PaletteFeatureId = "playback2d.tagger";

    /// <summary>The Matrix section's tab id; its descriptor declares <see cref="TabFeatureId" /> directly.</summary>
    public const string MatrixTabId = "tagger.matrix";

    private readonly Func<TagMatrixTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds The Matrix's VM on first activation, at the composition root.</param>
    public RoundTaggerModule(Func<TagMatrixTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.roundtagger";
    public string DisplayName => "Round Tagger";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = MatrixTabId,
            Header = "Tags",
            Order = 2, // after Situations (1)
            HostId = DemoViewer.NET.ViewModels.StratBook.StratBookHubViewModel.HostId,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new TagMatrixTabView()
        };
    }
}
