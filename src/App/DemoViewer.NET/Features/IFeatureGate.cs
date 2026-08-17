#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     The live show/hide authority for gated features. Resolves a feature id to an on/off decision from the
///     <see cref="FeatureCatalog" /> defaults, the user's <see cref="Category" />, and explicit
///     <c>AppSettings.Features.Overrides</c>. UI binds to <see cref="IsEnabled" /> and re-queries when
///     <see cref="Changed" /> fires (the shell wires the enforcement; this is pure resolution).
/// </summary>
public interface IFeatureGate
{
    /// <summary>
    ///     The effective user category driving defaults — <c>AppSettings.UserCategory</c>, escalated to
    ///     <see cref="UserCategory.Developer" /> when <c>AppSettings.Features.DeveloperMode</c> is on.
    /// </summary>
    UserCategory Category { get; }

    /// <summary>
    ///     How many non-Required catalog features a <see cref="UserCategory.Developer" /> would see that the
    ///     current user does not — drives the "N features hidden" affordance (0 for a developer).
    /// </summary>
    int HiddenCount { get; }

    /// <summary>
    ///     Whether <paramref name="featureId" /> is visible for the current user. Resolution order:
    ///     Required → explicit override → category default → group-leader state → parent-tab cascade. An id
    ///     not in the catalog is not gated and returns <c>true</c> (fail-open).
    /// </summary>
    bool IsEnabled(string featureId);

    /// <summary>Raised when settings change such that gate decisions may have changed; a cue to re-query.</summary>
    event EventHandler? Changed;
}
