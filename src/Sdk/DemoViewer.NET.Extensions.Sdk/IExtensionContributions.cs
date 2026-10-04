using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Modules.Abstractions;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     What an extension adds to the app, handed over in <see cref="IExtension.Contribute" />. Each item is
///     shown only while the extension's master switch is on, and the item's own feature id when it names one.
/// </summary>
public interface IExtensionContributions
{
    /// <summary>The calling extension's host context.</summary>
    IExtensionContext Context { get; }

    /// <summary>A module whose tabs join the strip, or a host tab's sections when a descriptor names a <c>HostId</c>.</summary>
    void Tabs(IWorkspaceModule workspaceModule);

    /// <summary>An evaluator on the library's shared parse, run after every evaluator named in <paramref name="after" />.</summary>
    void Evaluator(string id, Func<IExtensionEvaluator> factory, params string[] after);

    /// <summary>Commands for the keymap, in addition to <see cref="IExtension.Commands" />.</summary>
    void Commands(IEnumerable<CommandDescriptor> commands);

    /// <summary>A page in Settings.</summary>
    void SettingsPage(SettingsPageContribution page);

    /// <summary>Lanes, panes, panels, toolbar items and handlers in every 2D Playback tab.</summary>
    void Playback(IPlaybackContribution contribution);

    /// <summary>A Library filter or per-demo badge.</summary>
    void Library(ILibraryContribution contribution);

    /// <summary>A folder or file the extension owns, listed and removed by "Delete extension data".</summary>
    void Store(StoreDescriptor store);

    /// <summary>The extension's own "Delete extension data" action, for data a plain folder delete would get wrong.</summary>
    void DataRemoval(IExtensionDataRemoval removal);

    /// <summary>How many demos switching the extension back on will re-index, for the notice Settings shows.</summary>
    void ReindexEstimate(IReindexEstimate estimate);

    /// <summary>An action on Match Overview for the open demo.</summary>
    void DemoAction(DemoAction action);
}
