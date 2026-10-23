namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Entity decode sites in the host, numbered apart from Strat Book's 3 to 7: 1 the library's final-state
///     replay, only on a visit that runs no rules, 2 the merged bare run, 8 the shell's own rules run on an
///     open, 9 the forward read's own tap tracker, 10 the game clock's replay to the first freeze end, 11 the
///     checkpoint tracker's signon prime. Null outside the decode-count harness.
/// </summary>
internal static class DecodeSites
{
    /// <summary>Called once per decode, before it starts, on the decoding thread.</summary>
    internal static Action<int>? Entered;

    internal static void Enter(int site) => Entered?.Invoke(site);
}
