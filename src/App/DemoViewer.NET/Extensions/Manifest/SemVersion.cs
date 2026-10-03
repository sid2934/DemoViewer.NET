#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     A semantic version (major.minor.patch with an optional prerelease tag), ordered by SemVer 2.0
///     precedence: numeric core first, then a prerelease sorts below the plain release of the same core,
///     and prerelease identifiers compare numerically when both are numbers and ordinally otherwise. Build
///     metadata is dropped on parse and never compared.
/// </summary>
/// <param name="Major">The major component.</param>
/// <param name="Minor">The minor component.</param>
/// <param name="Patch">The patch component.</param>
/// <param name="Prerelease">The dot-separated prerelease identifiers after the hyphen, or null for a release.</param>
public sealed partial record SemVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemVersion>
{
    /// <summary>Whether the version carries a prerelease tag.</summary>
    public bool IsPrerelease => Prerelease is not null;

    /// <inheritdoc />
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int core = CompareCore(other);
        if (core != 0)
        {
            return core;
        }

        if (Prerelease is null)
        {
            return other.Prerelease is null ? 0 : 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Compares only the numeric core, ignoring any prerelease tag.</summary>
    public int CompareCore(SemVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    /// <summary>Whether <paramref name="other" /> has the same major, minor and patch.</summary>
    public bool SameCore(SemVersion other) => CompareCore(other) == 0;

    /// <summary>Parses a strict SemVer 2.0 string (three components, optional prerelease, optional build metadata).</summary>
    /// <exception cref="FormatException">Not a semantic version.</exception>
    public static SemVersion Parse(string text) =>
        TryParse(text, out SemVersion? version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    /// <summary>Parses a strict SemVer 2.0 string; false when it is not one.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match m = Strict().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        version = new SemVersion(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    /// <summary>
    ///     Parses an assembly's informational version. Nerdbank.GitVersioning stamps four numeric
    ///     components plus build metadata (<c>0.13.0.1-beta0001+9f1e3e3b</c>); the fourth component and the
    ///     metadata are dropped so the result equals the package version it was built from.
    /// </summary>
    public static bool TryParseInformational(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match m = Informational().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }

        version = new SemVersion(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        Prerelease is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}");

    public static bool operator <(SemVersion left, SemVersion right) => Compare(left, right) < 0;
    public static bool operator >(SemVersion left, SemVersion right) => Compare(left, right) > 0;
    public static bool operator <=(SemVersion left, SemVersion right) => Compare(left, right) <= 0;
    public static bool operator >=(SemVersion left, SemVersion right) => Compare(left, right) >= 0;

    private static int Compare(SemVersion? left, SemVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            bool aNum = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out long an);
            bool bNum = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out long bn);
            int c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i])
            };
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex Strict();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:\.\d+)?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+.*)?$")]
    private static partial Regex Informational();
}
