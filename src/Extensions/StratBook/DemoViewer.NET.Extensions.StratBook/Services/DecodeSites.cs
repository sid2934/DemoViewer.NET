namespace DemoViewer.NET.Extensions.StratBook.Services;

/// <summary>
///     Entity decode sites in Strat Book, numbered apart from the host's sites: 3 round index
///     positions, 4 suggested tags projectiles, 5 the occupancy fallback walk, 6 grenade projectiles,
///     7 the grenade read pass. Null outside the decode-count harness.
/// </summary>
internal static class DecodeSites
{
    /// <summary>Called once per decode, before it starts, on the decoding thread.</summary>
    internal static Action<int>? Entered;

    internal static void Enter(int site) => Entered?.Invoke(site);
}
