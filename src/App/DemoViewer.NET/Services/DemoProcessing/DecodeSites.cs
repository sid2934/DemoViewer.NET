namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Entity decode sites in the host, numbered apart from Strat Book's 3 to 7: 2 the merged bare
///     run, 8 the shell's own rules run on an open. The library reads the run's tables, so it has no site 1.
///     Null outside the decode-count harness.
/// </summary>
internal static class DecodeSites
{
    /// <summary>Called once per decode, before it starts, on the decoding thread.</summary>
    internal static Action<int>? Entered;

    internal static void Enter(int site) => Entered?.Invoke(site);
}
