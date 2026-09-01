namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The module-facing projection of the shell's feature gate: the ONE seam a module reads to decide
///     whether a gated surface of its own is on. The shell folds its platform ANDs (desktop-only ids) into
///     the projection, so a module never re-derives them, and a module is never handed the shell's
///     <c>IFeatureGate</c> itself (it would then see tabs / chrome that are none of its business).
/// </summary>
public interface IModuleFeatureGate
{
    /// <summary>
    ///     Live answer; re-query on <see cref="Changed" />, never cache for a tab's lifetime.
    ///     An id the host does not know fails OPEN.
    /// </summary>
    bool IsEnabled(string featureId);

    /// <summary>Raised on the UI thread when any gate answer may have changed.</summary>
    event Action? Changed;
}
