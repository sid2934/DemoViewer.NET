namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Entity decode sites in the host, numbered as the single-walk plan numbers them: 1 the library's
///     final-state replay, 2 the merged bare run. 8 is the shell's own rules run on an open, which the plan
///     does not number. Null outside the decode-count harness.
/// </summary>
internal static class DecodeSites
{
    /// <summary>Called once per decode, before it starts, on the decoding thread.</summary>
    internal static Action<int>? Entered;

    internal static void Enter(int site) => Entered?.Invoke(site);
}
