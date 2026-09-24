#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules.RoundTagger;

/// <summary>
///     The Round Tagger module (tag-store.md §3.11): the home of the Tag Palette hosted by the 2D Playback
///     tab and of The Matrix's Main-strip tab.
///     <para>
///         <b>No tab of its own yet.</b> The Tag Palette (<c>Palette/TagPaletteViewModel</c>) docks in the
///         2D Playback tab, which owns the tag session it writes through, under <see cref="PaletteFeatureId" />;
///         The Matrix (<see cref="MatrixTabId" />) is its own build item. What is fixed here is the module id
///         and the two feature ids, because all three are persisted keys (the user's per-tab session state
///         and <c>Features:Overrides:{id}</c>) and must not move when the surfaces land.
///     </para>
///     <para>
///         <b>Wiring contract</b>, when the tabs arrive: <see cref="WorkspaceTabDescriptor.ViewModelFactory" />
///         (lazy and retained), never <c>DataContext</c>, with the VM delegate-injected at the composition
///         root, the Highlights precedent; the module references no shell.
///     </para>
/// </summary>
public sealed class RoundTaggerModule : IWorkspaceModule
{
    /// <summary>The Matrix tab's feature id. A persisted key; never renamed.</summary>
    public const string TabFeatureId = "tab.tagger";

    /// <summary>The Tag Palette's feature id, a sub-feature of the 2D Playback tab. A persisted key; never renamed.</summary>
    public const string PaletteFeatureId = "playback2d.tagger";

    /// <summary>The Matrix tab's id, mapped to <see cref="TabFeatureId" /> by the shell's tab gate.</summary>
    public const string MatrixTabId = "tagger.matrix";

    public string Id => "net.demoviewer.roundtagger";
    public string DisplayName => "Round Tagger";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host) => [];
}
