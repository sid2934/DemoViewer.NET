namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     Compares a (pass id, demo key) pair: the pass id ordinally, the demo key (a path or a content id)
///     ignoring case, as every path is compared in the processing services.
/// </summary>
internal sealed class PassDemoKeyComparer : IEqualityComparer<(string Pass, string Demo)>
{
    public static readonly PassDemoKeyComparer Instance = new();

    public bool Equals((string Pass, string Demo) x, (string Pass, string Demo) y) =>
        string.Equals(x.Pass, y.Pass, StringComparison.Ordinal)
        && string.Equals(x.Demo, y.Demo, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Pass, string Demo) obj) =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Pass), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Demo));
}
