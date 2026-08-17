namespace DemoViewer.NET.ViewModels;

/// <summary>
///     Describes a coloured byte range in <see cref="HarvestHexViewModel" />.
/// </summary>
/// <param name="Start">Absolute byte offset in the loaded buffer.</param>
/// <param name="Length">Number of bytes covered.</param>
/// <param name="Level">
///     Priority tier.  0 = the innermost / selected range; higher values are ancestors.
///     When two spans overlap, the one with the lower Level wins.  On equal Level, the
///     shorter span wins (innermost in byte terms).
/// </param>
/// <param name="Label">Optional human-readable name shown in the status bar.</param>
public readonly record struct HexSpan(int Start, int Length, int Level = 0, string? Label = null)
{
    /// <summary>Contains.</summary>
    public bool Contains(int byteOffset) => byteOffset >= Start && byteOffset < Start + Length;
}
