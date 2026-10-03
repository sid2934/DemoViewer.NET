namespace DemoViewer.NET.Extensions;

/// <summary>
///     The packs compiled into this build, in composition order. A static list for now: both heads hand
///     it to <c>App.BuildServices</c>, and the catalog composes from it when nothing composed it first.
/// </summary>
public static class FeaturePacks
{
    /// <summary>Every first-party pack.</summary>
    public static IReadOnlyList<IFeaturePack> Default { get; } = [];
}
