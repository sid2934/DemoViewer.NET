#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;
using SdkPlayback = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The composition root's collector for one pack's contributions, in the order the pack made them.
///     Each pack gets its own instance so the shell knows which pack contributed what.
/// </summary>
/// <param name="pack">The pack contributing.</param>
/// <param name="context">The pack's host context, built on first use.</param>
/// <param name="toUiThread">
///     Runs a host handler for a <c>Changed</c> the pack raised, on the UI thread. Null runs it inline on
///     the raising thread.
/// </param>
internal sealed class PackContributions(IExtension pack, Func<IExtensionContext> context, Action<Action>? toUiThread = null)
    : IFirstPartyContributions
{
    private readonly Action<Action> _toUiThread = toUiThread ?? (static a => a());
    private IExtensionContext? _context;
    private readonly List<IWorkspaceModule> _modules = [];
    private readonly List<HostTabContribution> _hostTabs = [];
    private readonly List<EvaluatorContribution> _evaluators = [];
    private readonly List<CommandDescriptor> _commands = [];
    private readonly List<RulesetContribution> _rulesets = [];
    private readonly List<SettingsPageContribution> _settingsPages = [];
    private readonly List<StatusChipContribution> _statusChips = [];
    private readonly List<IReindexEstimate> _reindexEstimates = [];
    private readonly List<IPlaybackContribution> _playback = [];
    private readonly List<ILibraryContribution> _library = [];
    private readonly List<StoreDescriptor> _stores = [];
    private readonly List<GatedDemoAction> _demoActions = [];
    private IExtensionDataRemoval? _dataRemoval;

    /// <summary>The pack these contributions belong to.</summary>
    public IExtension Pack { get; } = pack;
    public IExtensionContext Context => _context ??= context();
    public IReadOnlyList<GatedDemoAction> DemoActions => _demoActions;

    /// <summary>Rulesets the pack owns, in contribution order.</summary>
    public IReadOnlyList<RulesetContribution> Rulesets => _rulesets;

    /// <summary>Modules, in contribution order.</summary>
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    /// <summary>Host tabs, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<HostTabContribution> HostTabs => _hostTabs;

    /// <summary>Evaluators, in contribution order.</summary>
    public IReadOnlyList<EvaluatorContribution> Evaluators => _evaluators;

    /// <summary>Job kinds, in contribution order.</summary>

    /// <summary>Commands, in contribution order. Named apart from the <see cref="Commands(IEnumerable{CommandDescriptor})" /> method the interface declares.</summary>
    public IReadOnlyList<CommandDescriptor> ContributedCommands => _commands;

    /// <summary>Settings pages, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<SettingsPageContribution> SettingsPages => _settingsPages;

    /// <summary>Status-chip slots, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<StatusChipContribution> StatusChips => _statusChips;

    /// <summary>Re-index estimates, in contribution order.</summary>
    public IReadOnlyList<IReindexEstimate> ReindexEstimates => _reindexEstimates;

    /// <summary>2D Playback contributions, in contribution order.</summary>
    public IReadOnlyList<IPlaybackContribution> PlaybackContributions => _playback;

    /// <summary>Library filter/badge contributions, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<ILibraryContribution> LibraryContributions => _library;

    /// <summary>Store and cache paths the pack declared, in contribution order.</summary>
    public IReadOnlyList<StoreDescriptor> Stores => _stores;

    /// <summary>The pack's "delete extension data" action, or null when it declared none.</summary>
    public IExtensionDataRemoval? DataRemovalContribution => _dataRemoval;

    /// <inheritdoc />
    public void Tabs(IWorkspaceModule workspaceModule)
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
    public void Evaluator(string id, Func<IExtensionEvaluator> factory, params string[] after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _evaluators.Add(new EvaluatorContribution(id, AdapterCache(factory), [.. after]));
    }

    // The registry calls the factory on every resolve. One adapter per evaluator instance keeps the
    // adapter identity stable across resolves; a factory that hands out a new instance gets a new adapter.
    private static Func<IDemoEvaluator> AdapterCache(Func<IExtensionEvaluator> factory)
    {
        object gate = new();
        ExtensionEvaluatorAdapter? cached = null;
        return () =>
        {
            IExtensionEvaluator inner = factory();
            lock (gate)
            {
                if (cached is null || !ReferenceEquals(cached.Inner, inner))
                {
                    cached = new ExtensionEvaluatorAdapter(inner);
                }

                return cached;
            }
        };
    }
    public void FirstPartyEvaluator(string id, Func<IDemoEvaluator> factory, params string[] after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _evaluators.Add(new EvaluatorContribution(id, factory, [.. after]));
    }

    /// <inheritdoc />

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
    public void ReindexEstimate(IReindexEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        _reindexEstimates.Add(estimate);
    }

    /// <inheritdoc />
    public void Playback(SdkPlayback.IPlaybackContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _playback.Add(new SdkPlaybackContribution(contribution));
    }
    public void FirstPartyPlayback(IPlaybackContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _playback.Add(contribution);
    }

    /// <inheritdoc />
    public void Library(ILibraryContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _library.Add(new HostLibraryContribution(contribution, contribution.FeatureId ?? Pack.FeatureId, _toUiThread));
    }

    /// <inheritdoc />
    public void Store(StoreDescriptor store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(store.Id);
        _stores.Add(store);
    }

    /// <inheritdoc />
    public void DataRemoval(IExtensionDataRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        _dataRemoval = removal;
    }

    /// <inheritdoc />
    public void DemoAction(DemoAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _demoActions.Add(new GatedDemoAction(action, action.FeatureId ?? Pack.FeatureId) { ToUiThread = _toUiThread });
    }

    // Stamps the owning pack's id onto a contribution that left FeatureId null, so the host always has a
    // concrete gate id and never has to fall back to "always on" the way a settings page or chip would.
    // Changed may be raised on any thread; the host's handlers run on the UI thread.
    private sealed class HostLibraryContribution(ILibraryContribution inner, string featureId, Action<Action> toUiThread)
        : ILibraryContribution
    {
        private readonly List<(Action Handler, Action Marshaled)> _handlers = [];

        public string? FeatureId => featureId;

        public event Action? Changed
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                Action marshaled = () => toUiThread(value);
                lock (_handlers)
                {
                    _handlers.Add((value, marshaled));
                }

                inner.Changed += marshaled;
            }
            remove
            {
                Action? marshaled = null;
                lock (_handlers)
                {
                    int i = _handlers.FindIndex(h => h.Handler.Equals(value));
                    if (i >= 0)
                    {
                        marshaled = _handlers[i].Marshaled;
                        _handlers.RemoveAt(i);
                    }
                }

                if (marshaled is not null)
                {
                    inner.Changed -= marshaled;
                }
            }
        }

        public LibraryFilter? Filter => inner.Filter;
        public bool HasBadge => inner.HasBadge;
        public LibraryBadge? BadgeFor(LibraryDemo demo) => inner.BadgeFor(demo);
        public IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<LibraryDemo> demos) => inner.BadgesFor(demos);
        public IReadOnlyList<string> BadgeLabels => inner.BadgeLabels;
        public string? BadgeResetLabel => inner.BadgeResetLabel;
        public string? BadgeResetTooltip => inner.BadgeResetTooltip;
        public void SetLabel(LibraryDemo demo, string? label) => inner.SetLabel(demo, label);
    }
}
