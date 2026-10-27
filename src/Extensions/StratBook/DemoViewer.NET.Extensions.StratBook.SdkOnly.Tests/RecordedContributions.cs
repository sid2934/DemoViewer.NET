#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace SdkOnlyHost;

/// <summary>
///     Collects what an extension hands over in <see cref="IExtension.Contribute" /> instead of wiring it
///     into a shell. A pass and a record pass are stored by id with their declared order, and the factories
///     are never called: a host calls them only while the extension is on, so calling them here would build
///     services the gate says nothing should build.
/// </summary>
internal sealed class RecordedContributions(IExtensionContext context) : IExtensionContributions
{
    private readonly List<IWorkspaceModule> _modules = [];
    private readonly List<(string Id, string[] After)> _passes = [];
    private readonly List<(string Id, RecordInput[] DependsOn)> _recordPasses = [];
    private readonly List<CollectorContribution> _collectors = [];
    private readonly List<RulesetContribution> _rulesets = [];
    private readonly List<SettingsPageContribution> _settingsPages = [];
    private readonly List<SettingsSchema> _settingsSchemas = [];
    private readonly List<IPlaybackContribution> _playback = [];
    private readonly List<ILibraryContribution> _library = [];
    private readonly List<DemoAction> _demoActions = [];
    private readonly List<HubTabContribution> _hubTabs = [];
    private readonly List<StatusChipContribution> _statusChips = [];
    private readonly List<IExtensionDataRemoval> _dataRemovals = [];
    private readonly List<IReindexEstimate> _reindexEstimates = [];

    public IExtensionContext Context { get; } = context;

    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    public IReadOnlyList<(string Id, string[] After)> Passes => _passes;

    public IReadOnlyList<(string Id, RecordInput[] DependsOn)> RecordPasses => _recordPasses;

    public IReadOnlyList<CollectorContribution> Collectors => _collectors;

    public IReadOnlyList<RulesetContribution> Rulesets => _rulesets;

    public IReadOnlyList<SettingsPageContribution> SettingsPages => _settingsPages;

    public IReadOnlyList<SettingsSchema> SettingsSchemas => _settingsSchemas;

    public IReadOnlyList<IPlaybackContribution> PlaybackContributions => _playback;

    public IReadOnlyList<ILibraryContribution> LibraryContributions => _library;

    public IReadOnlyList<DemoAction> DemoActions => _demoActions;

    public IReadOnlyList<HubTabContribution> HubTabs => _hubTabs;

    public IReadOnlyList<StatusChipContribution> StatusChips => _statusChips;

    public IReadOnlyList<IExtensionDataRemoval> DataRemovals => _dataRemovals;

    public IReadOnlyList<IReindexEstimate> ReindexEstimates => _reindexEstimates;

    public int DataDeletedHandlers { get; private set; }

    public void Tabs(IWorkspaceModule workspaceModule) => _modules.Add(workspaceModule);

    public void Pass(string id, Func<IExtensionPass> factory, params string[] after) => _passes.Add((id, after));

    public void RecordPass(string id, Func<IExtensionRecordPass> factory, params RecordInput[] dependsOn) =>
        _recordPasses.Add((id, dependsOn));

    public void Collector(CollectorContribution collector) => _collectors.Add(collector);

    public void Ruleset(RulesetContribution ruleset) => _rulesets.Add(ruleset);

    public void SettingsPage(SettingsPageContribution page) => _settingsPages.Add(page);

    public void SettingsSchema(SettingsSchema schema) => _settingsSchemas.Add(schema);

    public void Playback(IPlaybackContribution contribution) => _playback.Add(contribution);

    public void Library(ILibraryContribution contribution) => _library.Add(contribution);

    public void DataRemoval(IExtensionDataRemoval removal) => _dataRemovals.Add(removal);

    public void DataDeleted(Action afterDelete) => DataDeletedHandlers++;

    public void ReindexEstimate(IReindexEstimate estimate) => _reindexEstimates.Add(estimate);

    public void DemoAction(DemoAction action) => _demoActions.Add(action);

    public void HubTab(HubTabContribution hub) => _hubTabs.Add(hub);

    public void StatusChip(StatusChipContribution chip) => _statusChips.Add(chip);
}
