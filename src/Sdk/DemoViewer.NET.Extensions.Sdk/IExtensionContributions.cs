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
    ///     <para>
    ///         A pass that names inputs in <paramref name="dependsOn" /> waits until each one's write on the demo
    ///         has settled, then runs again exactly when one of those writes moves, not on other changes to the
    ///         row. A failed or stale write counts as settled: check <see cref="IAnalysisFacts.Status" />, or the
    ///         facet's stamp. A ruleset's outputs share one write, so naming one table re-runs on any of its
    ///         tables. While a named ruleset, or the extension owning a named facet, is off the pass waits. A fact
    ///         key no ruleset on now declares (<see cref="IAnalysisFacts.Declared" />), a scoreboard, or a facet of
    ///         an extension the host does not know refuses the pass; the keys are checked again on every run.
    ///     </para>
    /// </summary>
    /// <param name="id">The pass's <see cref="IExtensionRecordPass.Id" />.</param>
    /// <param name="factory">Builds or returns the pass.</param>
    /// <param name="dependsOn">
    ///     Ruleset outputs (<see cref="FactKey" />) and per-demo facets (<see cref="DemoDataKey" />, such as
    ///     <see cref="DemoDataKey.RoundFacts" />) the pass reads; empty runs it on every change to the demo's row.
    /// </param>
    void RecordPass(string id, Func<IExtensionRecordPass> factory, params RecordInput[] dependsOn);

    /// <summary>
    ///     A per-frame reader for one of the extension's passes, run inside the host's rules run of a demo instead of a
    ///     walk of its own. A provider or projectile class the engine does not know refuses the collector.
    /// </summary>
    void Collector(CollectorContribution collector);

    /// <summary>
    ///     A ruleset the host runs with the highlights on every demo; its tables become library facts, or the input of
    ///     the pass <see cref="RulesetContribution.ReadBy" /> names.
    /// </summary>
    void Ruleset(RulesetContribution ruleset);

    /// <summary>A page in Settings with controls of the extension's own.</summary>
    void SettingsPage(SettingsPageContribution page);

    /// <summary>A page in Settings the host renders from a list of settings, stored in <see cref="IExtensionContext.Settings" />.</summary>
    void SettingsSchema(SettingsSchema schema);

    /// <summary>Lanes, panes, panels, toolbar items and handlers in every 2D Playback tab.</summary>
    void Playback(IPlaybackContribution contribution);

    /// <summary>A Library filter or per-demo badge.</summary>
    void Library(ILibraryContribution contribution);

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

    /// <summary>A main tab whose sections the extension contributes and whose rail the host draws.</summary>
    void HubTab(HubTabContribution hub);

    /// <summary>A chip on the status strip.</summary>
    void StatusChip(StatusChipContribution chip);
}
