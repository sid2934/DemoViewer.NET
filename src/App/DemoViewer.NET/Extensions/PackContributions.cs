#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The composition root's collector for one pack's contributions, in the order the pack made them.
///     Each pack gets its own instance so the shell knows which pack contributed what.
/// </summary>
internal sealed class PackContributions(IFeaturePack pack) : IPackContributions
{
    private readonly List<IWorkspaceModule> _modules = [];
    private readonly List<HostTabContribution> _hostTabs = [];
    private readonly List<EvaluatorContribution> _evaluators = [];
    private readonly List<JobKindDescriptor> _jobKinds = [];
    private readonly List<CommandDescriptor> _commands = [];
    private readonly List<RulesetContribution> _rulesets = [];
    private readonly List<SettingsPageContribution> _settingsPages = [];
    private readonly List<StatusChipContribution> _statusChips = [];
    private readonly List<IPackReindexEstimate> _reindexEstimates = [];

    /// <summary>The pack these contributions belong to.</summary>
    public IFeaturePack Pack { get; } = pack;

    /// <summary>Rulesets the pack owns, in contribution order.</summary>
    public IReadOnlyList<RulesetContribution> Rulesets => _rulesets;

    /// <summary>Modules, in contribution order.</summary>
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    /// <summary>Host tabs, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<HostTabContribution> HostTabs => _hostTabs;

    /// <summary>Evaluators, in contribution order.</summary>
    public IReadOnlyList<EvaluatorContribution> Evaluators => _evaluators;

    /// <summary>Job kinds, in contribution order.</summary>
    public IReadOnlyList<JobKindDescriptor> JobKinds => _jobKinds;

    /// <summary>Commands, in contribution order. Named apart from the <see cref="Commands(IEnumerable{CommandDescriptor})" /> method the interface declares.</summary>
    public IReadOnlyList<CommandDescriptor> ContributedCommands => _commands;

    /// <summary>Settings pages, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<SettingsPageContribution> SettingsPages => _settingsPages;

    /// <summary>Status-chip slots, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<StatusChipContribution> StatusChips => _statusChips;

    /// <summary>Re-index estimates, in contribution order.</summary>
    public IReadOnlyList<IPackReindexEstimate> ReindexEstimates => _reindexEstimates;

    /// <inheritdoc />
    public void Module(IWorkspaceModule workspaceModule)
    {
        ArgumentNullException.ThrowIfNull(workspaceModule);
        _modules.Add(workspaceModule);
    }

    /// <inheritdoc />
    public void HostTab(HostTabContribution host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.HostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.TabId);
        ArgumentNullException.ThrowIfNull(host.ViewModelFactory);
        ArgumentNullException.ThrowIfNull(host.ViewFactory);
        _hostTabs.Add(host.FeatureId is null ? host with { FeatureId = Pack.FeatureId } : host);
    }

    /// <inheritdoc />
    public void Evaluator(string id, Func<IDemoEvaluator> factory, params string[] after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _evaluators.Add(new EvaluatorContribution(id, factory, [.. after]));
    }

    /// <inheritdoc />
    public void JobKind(JobKindDescriptor kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        _jobKinds.Add(kind);
    }

    /// <inheritdoc />
    public void Commands(IEnumerable<CommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.AddRange(commands);
    }

    /// <inheritdoc />
    public void Ruleset(string rulesetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesetId);
        _rulesets.Add(new RulesetContribution(rulesetId));
    }

    /// <inheritdoc />
    public void SettingsPage(SettingsPageContribution page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Id);
        ArgumentNullException.ThrowIfNull(page.ViewModelFactory);
        ArgumentNullException.ThrowIfNull(page.ViewFactory);
        _settingsPages.Add(page.FeatureId is null ? page with { FeatureId = Pack.FeatureId } : page);
    }

    /// <inheritdoc />
    public void StatusChip(StatusChipContribution chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentException.ThrowIfNullOrWhiteSpace(chip.Id);
        ArgumentNullException.ThrowIfNull(chip.Source);
        _statusChips.Add(chip.FeatureId is null ? chip with { FeatureId = Pack.FeatureId } : chip);
    }

    /// <inheritdoc />
    public void ReindexEstimate(IPackReindexEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        _reindexEstimates.Add(estimate);
    }
}
