namespace DemoViewer.NET.Features;

/// <summary>
///     Turns an extension's master switch off for the rest of the session without touching the user's
///     settings. A suspended pack id resolves off, so everything under it cascades off with it, and
///     <see cref="IFeatureGate.Changed" /> tells every consumer.
/// </summary>
public interface IFeatureSuspension
{
    /// <summary>Resolves <paramref name="packFeatureId" /> off until <see cref="ResumeForSession" /> or exit.</summary>
    /// <param name="packFeatureId">A pack's master switch; any other id is ignored.</param>
    void SuspendForSession(string packFeatureId);

    /// <summary>Lifts a suspension; the id resolves from settings again.</summary>
    /// <param name="packFeatureId">A pack's master switch.</param>
    void ResumeForSession(string packFeatureId);

    /// <summary>True while <paramref name="packFeatureId" /> is suspended.</summary>
    /// <param name="packFeatureId">A pack's master switch.</param>
    bool IsSuspended(string packFeatureId);
}
