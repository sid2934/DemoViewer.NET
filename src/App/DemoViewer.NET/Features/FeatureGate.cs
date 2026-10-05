#region

using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     The default <see cref="IFeatureGate" /> over a live <c>IOptionsMonitor&lt;AppSettings&gt;</c>. A
///     singleton (it holds the options-monitor subscription), see <c>App.BuildServices</c>. Every query
///     reads the monitor's current value, so a settings write is reflected without reconstructing the gate;
///     the <see cref="Changed" /> event is a re-query cue, not a cache invalidation.
///     <para>
///         <b>Resolution</b> (see <see cref="Resolve" />): (1) a Required descriptor is on; (2) an explicit
///         <c>Overrides[id]</c> wins; (3) otherwise the category default; (4) a grouped feature adopts the
///         group LEADER's own-state (the first catalog member of the group) so a group toggles atomically;
///         (5) a feature whose parent resolves disabled is implicitly off (cascade: sub-feature to tab to pack);
///         (6) a feature whose owning pack (<see cref="FeatureDescriptor.OwnerPackId" />) resolves disabled is
///         implicitly off too, whatever its <see cref="FeatureDescriptor.ParentId" /> chain says: a sub-feature
///         docked in a core tab still goes off with the pack that contributed it.
///         An id not in the catalog fails open unless it is a <c>pack.*</c> id, which resolves off. A pack id
///         suspended for the session (<see cref="SuspendForSession" />) resolves off ahead of every rule above.
///         Group (horizontal, "toggle together") and cascade (vertical, "parent hides child") are
///         orthogonal: a chrome member follows its leader even while the leader is itself cascade-hidden.
///     </para>
/// </summary>
public sealed class FeatureGate : IFeatureGate, IFeatureSuspension, IDisposable
{
    private static readonly Dictionary<string, bool> _emptyOverrides = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _noneSuspended = new(StringComparer.Ordinal);

    // Replaced, never mutated: IsEnabled runs on queue threads while the UI thread suspends or resumes.
    private volatile HashSet<string> _suspended = _noneSuspended;
    private readonly ExtensionFaults? _faults;
    private readonly MulticastGuard<EventHandler> _changedGuard = new("feature change handler");

    // Marshal Changed to the UI thread in the headed app (external-file-edit OnChange arrives on a
    // threadpool thread). Disabled by the internal ctor for unit tests: the App.Tests process is shared,
    // so a sibling headless test can leave an Avalonia dispatcher installed process-wide: a runtime
    // "is Avalonia up?" probe would flake. A construction-time flag is deterministic instead.
    private readonly bool _marshalChangedToUiThread;

    private readonly IOptionsMonitor<AppSettings> _monitor;
    private readonly IDisposable? _subscription;

    /// <summary>Production ctor: marshals <see cref="Changed" /> to the UI thread.</summary>
    public FeatureGate(IOptionsMonitor<AppSettings> monitor) : this(monitor, true)
    {
    }

    /// <summary>
    ///     The composition root's ctor: <paramref name="faults" /> switches a failing extension off through
    ///     this gate, and an extension's <see cref="Changed" /> handler that throws is contained.
    /// </summary>
    public FeatureGate(IOptionsMonitor<AppSettings> monitor, ExtensionFaults faults) : this(monitor, true, faults)
    {
    }

    // Test seam: pass marshalChangedToUiThread=false to raise Changed inline (synchronously observable
    // without an Avalonia dispatcher).
    internal FeatureGate(IOptionsMonitor<AppSettings> monitor, bool marshalChangedToUiThread, ExtensionFaults? faults = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _marshalChangedToUiThread = marshalChangedToUiThread;
        _faults = faults;
        _subscription = monitor.OnChange(_ => RaiseChanged());
        faults?.AttachSwitch(this);
    }

    /// <inheritdoc />
    public void SuspendForSession(string packFeatureId)
    {
        ArgumentException.ThrowIfNullOrEmpty(packFeatureId);
        if (FeatureCatalog.ById(packFeatureId) is not { Scope: FeatureScope.Pack } || _suspended.Contains(packFeatureId))
        {
            return;
        }

        _suspended = new HashSet<string>(_suspended, StringComparer.Ordinal) { packFeatureId };
        RaiseChanged();
    }

    /// <inheritdoc />
    public void ResumeForSession(string packFeatureId)
    {
        ArgumentException.ThrowIfNullOrEmpty(packFeatureId);
        if (!_suspended.Contains(packFeatureId))
        {
            return;
        }

        HashSet<string> next = new(_suspended, StringComparer.Ordinal);
        next.Remove(packFeatureId);
        _suspended = next;
        RaiseChanged();
    }

    /// <inheritdoc />
    public bool IsSuspended(string packFeatureId) => _suspended.Contains(packFeatureId);

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public UserCategory Category
    {
        get
        {
            AppSettings settings = _monitor.CurrentValue;
            // DeveloperMode is the master unlock: it escalates any category to Developer (matches the
            // AppSettings.Features.DeveloperMode contract: "unlocks developer-tier surfaces regardless of
            // category").
            return settings.Features.DeveloperMode ? UserCategory.Developer : settings.UserCategory;
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(string featureId)
    {
        FeatureDescriptor? descriptor = FeatureCatalog.ById(featureId);
        if (descriptor is null)
        {
            // Fail-open: an id not in the catalog is not gated (visible). A pack id is the exception: it
            // gates background work, so a typo must not silently enable it.
            return !FeatureCatalog.IsPackId(featureId);
        }

        Dictionary<string, bool> overrides = _monitor.CurrentValue.Features.Overrides ?? _emptyOverrides;
        return Resolve(descriptor, Category, overrides, _suspended, null);
    }

    /// <inheritdoc />
    public int HiddenCount
    {
        get
        {
            UserCategory category = Category;
            Dictionary<string, bool> overrides = _monitor.CurrentValue.Features.Overrides ?? _emptyOverrides;
            HashSet<string> suspended = _suspended;

            int hidden = 0;
            foreach (FeatureDescriptor descriptor in FeatureCatalog.All)
            {
                if (descriptor.Required)
                {
                    continue; // Required features are never hidden: excluded from the count.
                }

                if (descriptor.Scope == FeatureScope.Pack)
                {
                    continue; // The pack's own row renders as its live master switch, not a hidden feature.
                }

                // The Developer-full baseline: what a developer with default settings sees (no overrides).
                bool developerBaseline = Resolve(descriptor, UserCategory.Developer, _emptyOverrides, _noneSuspended, null);
                bool current = Resolve(descriptor, category, overrides, suspended, null);
                if (developerBaseline && !current)
                {
                    hidden++;
                }
            }

            return hidden;
        }
    }

    // Full resolution of one descriptor: group-leader own-state, then parent-tab and owning-pack cascade.
    // The visiting set guards against a malformed catalog cycle (never happens with the shipped catalog); a
    // re-entry fail-opens rather than recursing forever. Lazily allocated: null on every top-level call, and
    // most descriptors (chrome, the pack row itself, a leaf tab with neither ParentId nor OwnerPackId) never
    // recurse, so the common case allocates nothing.
    private static bool Resolve(
        FeatureDescriptor descriptor,
        UserCategory category,
        IReadOnlyDictionary<string, bool> overrides,
        HashSet<string> suspended,
        HashSet<string>? visiting)
    {
        if (visiting is not null && !visiting.Add(descriptor.Id))
        {
            return true;
        }

        // (1)-(4): the feature's own on/off, deferring to the group LEADER when grouped so the whole group
        // toggles as one. The leader's own-state (Required/override/default) is authoritative for the group.
        FeatureDescriptor stateSource = descriptor.GroupId is { } groupId
            ? FeatureCatalog.GroupLeader(groupId) ?? descriptor
            : descriptor;
        bool enabled = ResolveOwn(stateSource, category, overrides, suspended);

        if (!enabled || (descriptor.ParentId is null && descriptor.OwnerPackId is null))
        {
            return enabled;
        }

        // Only allocated once a cascade is actually possible; seeded with this id since visiting may still
        // be null here (the top-level call never pre-adds it).
        visiting ??= new HashSet<string>(StringComparer.Ordinal) { descriptor.Id };

        // (5) CASCADE: a feature under a parent that resolves disabled is implicitly off, regardless of its
        // own/group state. Uses THIS feature's ParentId (chrome and packs have none → no cascade). A tab's
        // parent is its pack, so the walk runs sub-feature → tab → pack.
        if (descriptor.ParentId is { } parentId)
        {
            // Composition rejects a parent the catalog lacks, so the null check is only a guard.
            FeatureDescriptor? parent = FeatureCatalog.ById(parentId);
            if (parent is not null && !Resolve(parent, category, overrides, suspended, visiting))
            {
                enabled = false;
            }
        }

        // (6) OWNING PACK: independent of ParentId, so a sub-feature docked in a core tab (2D Playback's
        // tagger and suggested-tags tracks) still goes off with the pack that contributed it.
        if (enabled && descriptor.OwnerPackId is { } ownerPackId)
        {
            FeatureDescriptor? owner = FeatureCatalog.ById(ownerPackId);
            if (owner is not null && !Resolve(owner, category, overrides, suspended, visiting))
            {
                enabled = false;
            }
        }

        return enabled;
    }

    // A descriptor's own on/off, ignoring group and cascade: session suspension → Required → explicit
    // override → category default.
    private static bool ResolveOwn(FeatureDescriptor descriptor, UserCategory category, IReadOnlyDictionary<string, bool> overrides,
        HashSet<string> suspended)
    {
        if (descriptor.Scope == FeatureScope.Pack && suspended.Contains(descriptor.Id))
        {
            return false;
        }

        if (descriptor.Required)
        {
            return true;
        }

        if (overrides.TryGetValue(descriptor.Id, out bool overridden))
        {
            return overridden;
        }

        return descriptor.Defaults.TryGetValue(category, out bool byDefault) && byDefault;
    }

    private void RaiseChanged()
    {
        EventHandler? handler = Changed;
        if (handler is null)
        {
            return;
        }

        // In unit tests (marshal disabled) or when already on the UI thread, raise inline: this keeps a
        // self-write's OnChange synchronously observable. Otherwise (headed app, off-thread external edit)
        // marshal to the UI dispatcher.
        if (!_marshalChangedToUiThread || Dispatcher.UIThread.CheckAccess())
        {
            Raise(handler);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Raise(handler));
        }
    }

    // Every subscriber runs even when an extension's handler throws; host handlers are not guarded, so a
    // host bug still surfaces.
    private void Raise(EventHandler handler)
    {
        if (_faults is null)
        {
            handler(this, EventArgs.Empty);
        }
        else
        {
            _changedGuard.Invoke(_faults, handler, this, static (h, gate) => h(gate, EventArgs.Empty));
        }
    }
}
