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

    /// <summary>
    ///     A pass on every demo's visit, run after every pass named in <paramref name="after" /> that is on the
    ///     same visit. The factory is called only while the extension is on.
    /// </summary>
    /// <param name="id">The pass's <see cref="IExtensionPass.Id" />, declared up front so the order can be checked without building the pass.</param>
    /// <param name="factory">Builds or returns the pass.</param>
    /// <param name="after">Pass ids whose writes this one reads, such as <see cref="HostIds.LibraryPass" />.</param>
    void Pass(string id, Func<IExtensionPass> factory, params string[] after);

    /// <summary>
    ///     A pass over what the library already holds for each demo, run without reading the demo file. The
    ///     factory is called only while the extension is on.
    /// </summary>
    /// <param name="id">The pass's <see cref="IExtensionRecordPass.Id" />.</param>
    /// <param name="factory">Builds or returns the pass.</param>
    void RecordPass(string id, Func<IExtensionRecordPass> factory);

    /// <summary>Commands for the keymap, in addition to <see cref="IExtension.Commands" />.</summary>
    void Commands(IEnumerable<CommandDescriptor> commands);

    /// <summary>A page in Settings with controls of the extension's own.</summary>
    void SettingsPage(SettingsPageContribution page);

    /// <summary>A page in Settings the host renders from a list of settings, stored in <see cref="IExtensionContext.Settings" />.</summary>
    void SettingsSchema(SettingsSchema schema);

    /// <summary>Lanes, panes, panels, toolbar items and handlers in every 2D Playback tab.</summary>
    void Playback(IPlaybackContribution contribution);

    /// <summary>A Library filter or per-demo badge.</summary>
    void Library(ILibraryContribution contribution);

    /// <summary>
    ///     A folder or file the extension owns outside its own folders, listed and removed by "Delete extension
    ///     data". The extension's own folders, its per-demo data included, are always listed.
    /// </summary>
    void Store(StoreDescriptor store);

    /// <summary>
    ///     The extension's own "Delete extension data", replacing the host's, for data a plain folder delete would
    ///     get wrong. The host switches the extension off before calling it.
    /// </summary>
    void DataRemoval(IExtensionDataRemoval removal);

    /// <summary>
    ///     Runs on the UI thread after "Delete extension data" removed the extension's files, while the extension
    ///     is off: drop what is still held in memory, so switching back on shows nothing that was deleted.
    /// </summary>
    void DataDeleted(Action afterDelete);

    /// <summary>How many demos switching the extension back on will re-index, for the notice Settings shows.</summary>
    void ReindexEstimate(IReindexEstimate estimate);

    /// <summary>An action on Match Overview for the open demo.</summary>
    void DemoAction(DemoAction action);
}
