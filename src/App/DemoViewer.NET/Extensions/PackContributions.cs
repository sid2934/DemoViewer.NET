#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
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
    private readonly List<IPlaybackContribution> _playback = [];
    private readonly List<ILibraryContribution> _library = [];
    private readonly List<StoreDescriptor> _stores = [];
    private IPackDataRemoval? _dataRemoval;

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

    /// <summary>2D Playback contributions, in contribution order.</summary>
    public IReadOnlyList<IPlaybackContribution> PlaybackContributions => _playback;

    /// <summary>Library filter/badge contributions, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<ILibraryContribution> LibraryContributions => _library;

    /// <summary>Store and cache paths the pack declared, in contribution order.</summary>
    public IReadOnlyList<StoreDescriptor> Stores => _stores;

    /// <summary>The pack's "delete extension data" action, or null when it declared none.</summary>
    public IPackDataRemoval? DataRemovalContribution => _dataRemoval;

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

    /// <inheritdoc />
    public void Playback(IPlaybackContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _playback.Add(contribution);
    }

    /// <inheritdoc />
    public void Library(ILibraryContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _library.Add(contribution.FeatureId is null ? new StampedLibraryContribution(contribution, Pack.FeatureId) : contribution);
    }

    /// <inheritdoc />
    public void Store(StoreDescriptor store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(store.Id);
        _stores.Add(store);
    }

    /// <inheritdoc />
    public void DataRemoval(IPackDataRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        _dataRemoval = removal;
    }

    // Stamps the owning pack's id onto a contribution that left FeatureId null, so the host always has a
    // concrete gate id and never has to fall back to "always on" the way a settings page or chip would.
    private sealed class StampedLibraryContribution(ILibraryContribution inner, string featureId) : ILibraryContribution
    {
        public string? FeatureId => featureId;

        public event Action? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public LibraryFilter? Filter => inner.Filter;
        public bool HasBadge => inner.HasBadge;
        public LibraryBadge? BadgeFor(DemoEntry entry) => inner.BadgeFor(entry);
        public IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<DemoEntry> entries) => inner.BadgesFor(entries);
        public IReadOnlyList<string> BadgeLabels => inner.BadgeLabels;
        public string? BadgeResetLabel => inner.BadgeResetLabel;
        public string? BadgeResetTooltip => inner.BadgeResetTooltip;
        public void SetLabel(DemoEntry entry, string? label) => inner.SetLabel(entry, label);
    }
}
