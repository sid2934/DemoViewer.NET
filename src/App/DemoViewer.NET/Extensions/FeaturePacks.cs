#region

using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>The packs compiled into this build, in composition order. Both heads compose from this list.</summary>
public static class FeaturePacks
{
    /// <summary>Every first-party pack.</summary>
    public static IReadOnlyList<IFeaturePack> Default { get; } = [new StratBookPack()];
}
