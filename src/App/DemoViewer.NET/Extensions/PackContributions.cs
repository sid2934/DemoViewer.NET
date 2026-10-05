#region

using Avalonia.Controls;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Settings;
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
/// <param name="guard">
///     Runs the pack's code as the pack's: every interface and delegate collected here is wrapped so a throw
///     is reported against the pack and the host gets a fallback. Null builds a guard that only logs.
/// </param>
/// <remarks>
///     Modules are kept as the pack's own objects, so the pack can still find what it contributed; the
///     shell runs their tabs through <see cref="ExtensionTabs" /> with the guard recorded here.
/// </remarks>
internal sealed class PackContributions(IExtension pack, Func<IExtensionContext> context, Action<Action>? toUiThread = null,
    ExtensionGuard? guard = null)
    : IFirstPartyContributions
{
    private readonly Action<Action> _toUiThread = toUiThread ?? (static a => a());
    private readonly ExtensionGuard _guard = guard ?? ExtensionGuard.Standalone(pack);
    private IExtensionContext? _context;
    private readonly List<IWorkspaceModule> _modules = [];
    private readonly List<HostTabContribution> _hostTabs = [];
    private readonly List<PassContribution> _passes = [];
    private readonly List<RecordPassContribution> _recordPasses = [];
    private readonly List<CommandDescriptor> _commands = [];
    private readonly List<SettingsPageContribution> _settingsPages = [];
    private readonly List<StatusChipContribution> _statusChips = [];
    private readonly List<IReindexEstimate> _reindexEstimates = [];
    private readonly List<IPlaybackContribution> _playback = [];
    private readonly List<ILibraryContribution> _library = [];
    private readonly List<StoreDescriptor> _stores = [];
    private readonly List<GatedDemoAction> _demoActions = [];
    private readonly List<ContributedRuleset> _rulesets = [];
    private IExtensionDataRemoval? _dataRemoval;
    private readonly List<Action> _dataDeleted = [];

    /// <summary>The pack these contributions belong to.</summary>
    public IExtension Pack { get; } = pack;

    /// <summary>The guard every contribution of the pack runs under.</summary>
    public ExtensionGuard Guard => _guard;
    public IExtensionContext Context => _context ??= context();
    public IReadOnlyList<GatedDemoAction> DemoActions => _demoActions;


    /// <summary>Modules, in contribution order.</summary>
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    /// <summary>Host tabs, in contribution order, each stamped with a gate id.</summary>
    public IReadOnlyList<HostTabContribution> HostTabs => _hostTabs;

    /// <summary>Passes, in contribution order.</summary>
    public IReadOnlyList<PassContribution> Passes => _passes;

    /// <summary>Rulesets, in contribution order, each under its qualified id.</summary>
    public IReadOnlyList<ContributedRuleset> Rulesets => _rulesets;

    /// <summary>Record passes, in contribution order.</summary>
    public IReadOnlyList<RecordPassContribution> RecordPasses => _recordPasses;

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

    /// <summary>The pack's own "delete extension data", replacing the host's, or null when it declared none.</summary>
    public IExtensionDataRemoval? DataRemovalContribution => _dataRemoval;

    /// <inheritdoc />
    public void Tabs(IWorkspaceModule workspaceModule)
    {
        ArgumentNullException.ThrowIfNull(workspaceModule);
        ExtensionGuards.Register(workspaceModule, _guard);
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
        // A failed view model shows the placeholder rather than the extension's view over nothing.
        bool viewModelFailed = false;
        Func<IHostTabViewModel> viewModel = host.ViewModelFactory;
        Func<Control> view = host.ViewFactory;
        HostTabContribution guarded = host with
        {
            FeatureId = host.FeatureId ?? Pack.FeatureId,
            // Null tells the shell to leave the host tab out.
            ViewModelFactory = () =>
            {
                IHostTabViewModel? built = _guard.Run<IHostTabViewModel?>("hub view model", () => viewModel(), null);
                viewModelFailed = built is null;
                return built!;
            },
            ViewFactory = () => viewModelFailed
                ? ExtensionPlaceholder.View(_guard.Scope, "this tab")
                : _guard.Run("hub view", view, ExtensionPlaceholder.View(_guard.Scope, "this tab"))
        };
        ExtensionGuards.Register(guarded, _guard);
        _hostTabs.Add(guarded);
    }

    /// <inheritdoc />
    public void Pass(string id, Func<IExtensionPass> factory, params string[] after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        string[] order = [.. after];
        _passes.Add(new PassContribution(id, HostCache(factory, order), order));
    }

    /// <inheritdoc />
    public void RecordPass(string id, Func<IExtensionRecordPass> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _recordPasses.Add(new RecordPassContribution(id, factory, _guard));
    }

    // The registry calls the factory on every resolve. One host per pass instance keeps the identity a visit
    // tells passes apart by, and the quarantine with it; a factory that hands out a new instance gets a new host.
    private Func<IDemoPass> HostCache(Func<IExtensionPass> factory, IReadOnlyList<string> after)
    {
        object gate = new();
        ExtensionPassHost? cached = null;
        return () =>
        {
            IExtensionPass inner = factory();
            lock (gate)
            {
                if (cached is null || !ReferenceEquals(cached.Inner, inner))
                {
                    cached = new ExtensionPassHost(inner, after, _guard);
                }

                return cached;
            }
        };
    }

    /// <inheritdoc />

    /// <inheritdoc />
    public void Ruleset(RulesetContribution ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(ruleset.Yaml);
        // A bad name drops this ruleset, not the extension's other contributions.
        if (!RulesetContribution.IsValidId(ruleset.Id))
        {
            _guard.Report("ruleset", new ArgumentException($"'{ruleset.Id}' is not a ruleset name.", nameof(ruleset)));
            return;
        }

        string id = RulesetContribution.QualifiedId(Pack.Id, ruleset.Id);
        Func<Stream> open = ruleset.Yaml;
        _rulesets.Add(new ContributedRuleset(id, Pack.Id, ruleset.FeatureId ?? Pack.FeatureId, () => _guard.Run<string?>("ruleset " + id, () =>
        {
            using Stream stream = open();
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }, null)));
    }

    /// <inheritdoc />
    public void Commands(IEnumerable<CommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.AddRange(commands);
    }

    /// <inheritdoc />
    public void SettingsPage(SettingsPageContribution page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Id);
        ArgumentNullException.ThrowIfNull(page.ViewModelFactory);
        ArgumentNullException.ThrowIfNull(page.ViewFactory);
        // A failed view model shows the placeholder rather than the extension's view over nothing.
        bool viewModelFailed = false;
        Func<object> viewModel = page.ViewModelFactory;
        Func<Control> view = page.ViewFactory;
        _settingsPages.Add(page with
        {
            FeatureId = page.FeatureId ?? Pack.FeatureId,
            ViewModelFactory = () =>
            {
                object? built = _guard.Run<object?>("settings page view model", () => viewModel(), null);
                viewModelFailed = built is null;
                return built ?? new object();
            },
            ViewFactory = () => viewModelFailed
                ? ExtensionPlaceholder.View(_guard.Scope, "this page")
                : _guard.Run("settings page view", view, ExtensionPlaceholder.View(_guard.Scope, "this page"))
        });
    }

    /// <inheritdoc />
    public void SettingsSchema(SettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema.Id);
        ArgumentNullException.ThrowIfNull(schema.Settings);
        foreach (SettingDescriptor setting in schema.Settings)
        {
            ArgumentNullException.ThrowIfNull(setting);
            ArgumentException.ThrowIfNullOrWhiteSpace(setting.Key);
        }

        string keywords = string.Join(' ', schema.Settings.Select(s => s.Label).Prepend(schema.Keywords).Prepend(schema.Header));
        SettingsPage(new SettingsPageContribution(schema.Id, schema.Header, schema.Order, keywords,
            () => new SchemaSettingsPageViewModel(schema, Context.Settings), () => new SchemaSettingsPageView(), schema.FeatureId));
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
        _reindexEstimates.Add(new GuardedReindexEstimate(estimate, _guard));
    }

    /// <inheritdoc />
    public void Playback(SdkPlayback.IPlaybackContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _playback.Add(new SdkPlaybackContribution(contribution, _guard));
    }

    /// <inheritdoc />
    public void FirstPartyPlayback(IPlaybackContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _playback.Add(new GuardedPlaybackContribution(contribution, _guard));
    }

    /// <inheritdoc />
    public void Library(ILibraryContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        string featureId = _guard.Run("library contribution", () => contribution.FeatureId, null) ?? Pack.FeatureId;
        _library.Add(new HostLibraryContribution(contribution, featureId, _toUiThread, _guard));
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
        _dataRemoval = new GuardedDataRemoval(removal, _guard, Pack.FeatureId);
    }

    /// <inheritdoc />
    public void DataDeleted(Action afterDelete)
    {
        ArgumentNullException.ThrowIfNull(afterDelete);
        _dataDeleted.Add(afterDelete);
    }

    /// <summary>Runs the extension's after-delete callbacks on the UI thread, each under the guard.</summary>
    internal void RaiseDataDeleted()
    {
        foreach (Action callback in _dataDeleted)
        {
            _toUiThread(() => _guard.Run("data deleted", callback));
        }
    }

    /// <inheritdoc />
    public void DemoAction(DemoAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Func<string, bool> isAvailable = _guard.Wrap("demo action availability", action.IsAvailable, false);
        Action<string> run = _guard.Wrap("demo action", action.Run);
        DemoAction guarded = new(action.Id, action.Label, action.Tooltip, isAvailable, run, action.FeatureId);
        action.Changed += guarded.NotifyChanged;
        _demoActions.Add(new GatedDemoAction(guarded, action.FeatureId ?? Pack.FeatureId) { ToUiThread = _toUiThread });
    }


    // Stamps the owning pack's id onto a contribution that left FeatureId null, so the host always has a
    // concrete gate id and never has to fall back to "always on" the way a settings page or chip would.
    // Changed may be raised on any thread; the host's handlers run on the UI thread. Every read and call
    // into the extension is guarded: a throwing filter keeps the demo, a throwing badge shows none.
    private sealed class HostLibraryContribution(ILibraryContribution inner, string featureId, Action<Action> toUiThread,
        ExtensionGuard guard)
        : ILibraryContribution
    {
        private readonly List<(Action Handler, Action Marshaled)> _handlers = [];
        private (LibraryFilter Inner, LibraryFilter Guarded)? _filter;

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

                try
                {
                    inner.Changed += marshaled;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    guard.Report("library subscribe", ex);
                }
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
                    try
                    {
                        inner.Changed -= marshaled;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        guard.Report("library unsubscribe", ex);
                    }
                }
            }
        }

        public LibraryFilter? Filter
        {
            get
            {
                if (guard.Run("library filter", () => inner.Filter, null) is not { } current)
                {
                    return null;
                }

                if (_filter is { } cached && ReferenceEquals(cached.Inner, current))
                {
                    return cached.Guarded;
                }

                Func<LibraryDemo, string, bool> matches = current.Matches;
                LibraryFilter wrapped = current with
                {
                    Matches = (demo, key) =>
                    {
                        try
                        {
                            return matches(demo, key);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            guard.Report("library filter", ex);
                            return true;
                        }
                    }
                };
                _filter = (current, wrapped);
                return wrapped;
            }
        }

        public bool HasBadge => guard.Run("library badge", () => inner.HasBadge, false);

        public LibraryBadge? BadgeFor(LibraryDemo demo) => guard.Run("library badge", () => inner.BadgeFor(demo), null);

        public IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<LibraryDemo> demos) =>
            guard.Run("library badge", () => inner.BadgesFor(demos), new Dictionary<string, LibraryBadge?>());

        public IReadOnlyList<string> BadgeLabels => guard.Run("library badge labels", () => inner.BadgeLabels, []);

        public string? BadgeResetLabel => guard.Run("library badge labels", () => inner.BadgeResetLabel, null);

        public string? BadgeResetTooltip => guard.Run("library badge labels", () => inner.BadgeResetTooltip, null);

        public void SetLabel(LibraryDemo demo, string? label) => guard.Run("library label", () => inner.SetLabel(demo, label));
    }

    private sealed class GuardedReindexEstimate(IReindexEstimate inner, ExtensionGuard guard) : IReindexEstimate
    {
        public string FeatureId { get; } = guard.Run("re-index estimate", () => inner.FeatureId, guard.Scope.FeatureId);

        public Task<int> CountAsync() => guard.RunAsync("re-index estimate", inner.CountAsync, 0);
    }

    private sealed class GuardedDataRemoval(IExtensionDataRemoval inner, ExtensionGuard guard, string packFeatureId)
        : IExtensionDataRemoval
    {
        public string FeatureId { get; } = guard.Run("data removal", () => inner.FeatureId, packFeatureId);

        public Task<ExtensionDataInventory> InventoryAsync() =>
            guard.RunAsync("data inventory", inner.InventoryAsync, ExtensionDataInventory.Empty);

        public Task<ExtensionDataRemovalResult> DeleteAsync() =>
            guard.RunAsync("data removal", inner.DeleteAsync, ExtensionDataRemovalResult.NotRun);
    }
}
