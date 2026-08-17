#region

using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     The default <see cref="IFeatureGate" /> over a live <c>IOptionsMonitor&lt;AppSettings&gt;</c>. A
///     singleton (it holds the options-monitor subscription) — see <c>App.BuildServices</c>. Every query
///     reads the monitor's current value, so a settings write is reflected without reconstructing the gate;
///     the <see cref="Changed" /> event is a re-query cue, not a cache invalidation.
///     <para>
///         <b>Resolution</b> (see <see cref="Resolve" />): (1) a Required descriptor is on; (2) an explicit
///         <c>Overrides[id]</c> wins; (3) otherwise the category default; (4) a grouped feature adopts the
///         group LEADER's own-state (the first catalog member of the group) so a group toggles atomically;
///         (5) a sub-feature/chrome whose parent tab resolves disabled is implicitly off (cascade).
///         Group (horizontal, "toggle together") and cascade (vertical, "parent hides child") are
///         orthogonal: a chrome member follows its leader even while the leader is itself cascade-hidden.
///     </para>
/// </summary>
public sealed class FeatureGate : IFeatureGate, IDisposable
{
    private static readonly Dictionary<string, bool> _emptyOverrides = new(StringComparer.Ordinal);

    // Marshal Changed to the UI thread in the headed app (external-file-edit OnChange arrives on a
    // threadpool thread). Disabled by the internal ctor for unit tests: the App.Tests process is shared,
    // so a sibling headless test can leave an Avalonia dispatcher installed process-wide — a runtime
    // "is Avalonia up?" probe would flake. A construction-time flag is deterministic instead.
    private readonly bool _marshalChangedToUiThread;

    private readonly IOptionsMonitor<AppSettings> _monitor;
    private readonly IDisposable? _subscription;

    /// <summary>Production ctor — marshals <see cref="Changed" /> to the UI thread.</summary>
    public FeatureGate(IOptionsMonitor<AppSettings> monitor) : this(monitor, true)
    {
    }

    // Test seam: pass marshalChangedToUiThread=false to raise Changed inline (synchronously observable
    // without an Avalonia dispatcher).
    internal FeatureGate(IOptionsMonitor<AppSettings> monitor, bool marshalChangedToUiThread)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _marshalChangedToUiThread = marshalChangedToUiThread;
        _subscription = monitor.OnChange(_ => RaiseChanged());
    }

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
            // DeveloperMode is the master unlock — it escalates any category to Developer (matches the
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
            return true; // fail-open: an id not in the catalog is not gated (visible).
        }

        Dictionary<string, bool> overrides = _monitor.CurrentValue.Features.Overrides ?? _emptyOverrides;
        return Resolve(descriptor, Category, overrides, NewVisiting());
    }

    /// <inheritdoc />
    public int HiddenCount
    {
        get
        {
            UserCategory category = Category;
            Dictionary<string, bool> overrides = _monitor.CurrentValue.Features.Overrides ?? _emptyOverrides;

            int hidden = 0;
            foreach (FeatureDescriptor descriptor in FeatureCatalog.All)
            {
                if (descriptor.Required)
                {
                    continue; // Required features are never hidden — excluded from the count.
                }

                // The Developer-full baseline: what a developer with default settings sees (no overrides).
                bool developerBaseline = Resolve(descriptor, UserCategory.Developer, _emptyOverrides, NewVisiting());
                bool current = Resolve(descriptor, category, overrides, NewVisiting());
                if (developerBaseline && !current)
                {
                    hidden++;
                }
            }

            return hidden;
        }
    }

    // Full resolution of one descriptor: group-leader own-state, then parent-tab cascade. The visiting set
    // guards against a malformed catalog cycle (never happens with the shipped catalog); a re-entry
    // fail-opens rather than recursing forever.
    private static bool Resolve(
        FeatureDescriptor descriptor,
        UserCategory category,
        IReadOnlyDictionary<string, bool> overrides,
        HashSet<string> visiting)
    {
        if (!visiting.Add(descriptor.Id))
        {
            return true;
        }

        // (1)-(4): the feature's own on/off, deferring to the group LEADER when grouped so the whole group
        // toggles as one. The leader's own-state (Required/override/default) is authoritative for the group.
        FeatureDescriptor stateSource = descriptor.GroupId is { } groupId
            ? FeatureCatalog.GroupLeader(groupId) ?? descriptor
            : descriptor;
        bool enabled = ResolveOwn(stateSource, category, overrides);

        // (5) CASCADE: a feature under a tab that resolves disabled is implicitly off — regardless of its
        // own/group state. Uses THIS feature's ParentId (chrome has none → no cascade).
        if (enabled && descriptor.ParentId is { } parentId)
        {
            FeatureDescriptor? parent = FeatureCatalog.ById(parentId);
            if (parent is not null && !Resolve(parent, category, overrides, visiting))
            {
                enabled = false;
            }
        }

        return enabled;
    }

    // A descriptor's own on/off, ignoring group and cascade: Required → explicit override → category default.
    private static bool ResolveOwn(FeatureDescriptor descriptor, UserCategory category, IReadOnlyDictionary<string, bool> overrides)
    {
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

    private static HashSet<string> NewVisiting() => new(StringComparer.Ordinal);

    private void RaiseChanged()
    {
        EventHandler? handler = Changed;
        if (handler is null)
        {
            return;
        }

        // In unit tests (marshal disabled) or when already on the UI thread, raise inline — this keeps a
        // self-write's OnChange synchronously observable. Otherwise (headed app, off-thread external edit)
        // marshal to the UI dispatcher.
        if (!_marshalChangedToUiThread || Dispatcher.UIThread.CheckAccess())
        {
            handler(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(this, EventArgs.Empty));
        }
    }
}
