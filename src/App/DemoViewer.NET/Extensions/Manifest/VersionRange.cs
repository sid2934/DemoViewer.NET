#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     A version range in the npm/semver syntax a manifest's <c>requiresHost</c> and
///     <c>requiresCs2DemoKit</c> carry: comparator sets (<c>&gt;=1.0.0 &lt;2.0.0</c>), caret
///     (<c>^1.0</c>: same major), tilde (<c>~1.2</c>: same minor), a bare version (exact, or an X-range
///     when partial: <c>1.2</c> is <c>&gt;=1.2.0 &lt;1.3.0</c>), <c>*</c> for any, and <c>||</c> between
///     alternatives. A prerelease version satisfies a set only when some comparator in it names a
///     prerelease of the same major.minor.patch, so <c>^1.0</c> never admits <c>1.5.0-rc1</c> while
///     <c>0.13.0-beta0001</c> is an exact match for itself; <c>*</c> alone admits everything, prereleases
///     included, since a pre-1.0 CS2DemoKit is the normal case. Two ranges are equal when written the same.
/// </summary>
public sealed partial class VersionRange : IEquatable<VersionRange>
{
    private readonly IReadOnlyList<IReadOnlyList<Comparator>> _alternatives;
    private readonly string _text;

    private VersionRange(string text, IReadOnlyList<IReadOnlyList<Comparator>> alternatives)
    {
        _text = text;
        _alternatives = alternatives;
    }

    /// <summary>A range every version satisfies.</summary>
    public static VersionRange Any { get; } = new("*", [[]]);

    /// <summary>A range only <paramref name="version" /> satisfies.</summary>
    public static VersionRange Exactly(SemVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new VersionRange(version.ToString(), [[new Comparator(Op.Eq, version)]]);
    }

    /// <summary>Parses a range.</summary>
    /// <exception cref="FormatException">Not a range this syntax covers.</exception>
    public static VersionRange Parse(string text) =>
        TryParse(text, out VersionRange? range) ? range : throw new FormatException($"'{text}' is not a version range.");

    /// <summary>Parses a range; false when the text is not one.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out VersionRange? range)
    {
        range = null;
        if (text is null)
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            range = Any;
            return true;
        }

        List<IReadOnlyList<Comparator>> alternatives = [];
        foreach (string alternative in trimmed.Split("||", StringSplitOptions.None))
        {
            // Only an explicit "*" may yield an empty set (which admits everything); an alternative with no
            // token at all ("^1.0 ||") is a typo, and a typo must not widen the range.
            string[] tokens = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                return false;
            }

            List<Comparator> set = [];
            foreach (string token in tokens)
            {
                if (!TryParseComparator(token, set))
                {
                    return false;
                }
            }

            alternatives.Add(set);
        }

        range = new VersionRange(trimmed, alternatives);
        return true;
    }

    /// <summary>Whether <paramref name="version" /> is inside the range.</summary>
    public bool Satisfies(SemVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        foreach (IReadOnlyList<Comparator> set in _alternatives)
        {
            if (set.Count == 0)
            {
                return true;
            }

            if (set.All(c => c.Holds(version)) && (!version.IsPrerelease || set.Any(c => c.Version.IsPrerelease && c.Version.SameCore(version))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The range as written.</summary>
    public override string ToString() => _text;

    /// <inheritdoc />
    public bool Equals(VersionRange? other) => other is not null && string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as VersionRange);

    /// <inheritdoc />
    public override int GetHashCode() => string.GetHashCode(_text, StringComparison.Ordinal);

    // One token into its comparators. A caret, tilde or partial version becomes a >= / < pair.
    private static bool TryParseComparator(string token, List<Comparator> into)
    {
        if (token is "*" or "x" or "X")
        {
            return true;
        }

        Match m = Token().Match(token);
        if (!m.Success)
        {
            return false;
        }

        string op = m.Groups[1].Value;
        if (!TryParsePartial(m.Groups[2].Value, out Partial? p))
        {
            return false;
        }

        SemVersion low = p.Floor;
        switch (op)
        {
            case ">=":
                into.Add(new Comparator(Op.Gte, low));
                return true;
            case ">":
                // ">1.2" means at least 1.3.0: anything in the 1.2 line is still 1.2.
                into.Add(p.Patch is null ? new Comparator(Op.Gte, p.NextPartial()) : new Comparator(Op.Gt, low));
                return true;
            case "<=":
                into.Add(p.Patch is null ? new Comparator(Op.Lt, p.NextPartial()) : new Comparator(Op.Lte, low));
                return true;
            case "<":
                into.Add(new Comparator(Op.Lt, low));
                return true;
            case "^":
                into.Add(new Comparator(Op.Gte, low));
                into.Add(new Comparator(Op.Lt, p.CaretCeiling()));
                return true;
            case "~":
                into.Add(new Comparator(Op.Gte, low));
                into.Add(new Comparator(Op.Lt, p.TildeCeiling()));
                return true;
            default:
                if (p.Patch is null)
                {
                    into.Add(new Comparator(Op.Gte, low));
                    into.Add(new Comparator(Op.Lt, p.NextPartial()));
                }
                else
                {
                    into.Add(new Comparator(Op.Eq, low));
                }

                return true;
        }
    }

    private static bool TryParsePartial(string text, [NotNullWhen(true)] out Partial? partial)
    {
        partial = null;
        Match m = PartialVersion().Match(text);
        if (!m.Success)
        {
            return false;
        }

        int major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        bool minorWild = m.Groups[2].Success && IsWildcard(m.Groups[2].Value);
        bool patchWild = m.Groups[3].Success && IsWildcard(m.Groups[3].Value);
        // A concrete component after a wildcard ("1.x.3") names nothing a range can mean.
        if (minorWild && m.Groups[3].Success && !patchWild)
        {
            return false;
        }

        int? minor = m.Groups[2].Success && !minorWild ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        int? patch = minor is not null && m.Groups[3].Success && !patchWild ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        if (m.Groups[4].Success && patch is null)
        {
            return false;
        }

        partial = new Partial(major, minor, patch, m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    private static bool IsWildcard(string component) => component is "x" or "X" or "*";

    [GeneratedRegex(@"^(>=|<=|>|<|=|\^|~)?(.+)$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^(\d+)(?:\.(\d+|x|X|\*))?(?:\.(\d+|x|X|\*))?(?:-(" + SemVersion.PrereleasePattern + @"))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex PartialVersion();

    private enum Op
    {
        Gte,
        Gt,
        Lte,
        Lt,
        Eq
    }

    private sealed record Comparator(Op Op, SemVersion Version)
    {
        public bool Holds(SemVersion v)
        {
            int c = v.CompareTo(Version);
            return Op switch
            {
                Op.Gte => c >= 0,
                Op.Gt => c > 0,
                Op.Lte => c <= 0,
                Op.Lt => c < 0,
                _ => c == 0
            };
        }
    }

    private sealed record Partial(int Major, int? Minor, int? Patch, string? Prerelease)
    {
        public SemVersion Floor => new(Major, Minor ?? 0, Patch ?? 0, Prerelease);

        // The first version above everything the partial names: 1 -> 2.0.0, 1.2 -> 1.3.0.
        public SemVersion NextPartial() => Minor is null ? new SemVersion(Major + 1, 0, 0) : new SemVersion(Major, Minor.Value + 1, 0);

        // ^1.2.3 -> 2.0.0; ^0.2.3 -> 0.3.0; ^0.0.3 -> 0.0.4; ^0 -> 1.0.0; ^0.2 -> 0.3.0.
        public SemVersion CaretCeiling()
        {
            if (Major > 0 || Minor is null)
            {
                return new SemVersion(Major + 1, 0, 0);
            }

            if (Minor > 0 || Patch is null)
            {
                return new SemVersion(0, Minor.Value + 1, 0);
            }

            return new SemVersion(0, 0, Patch.Value + 1);
        }

        // ~1.2.3 -> 1.3.0; ~1 -> 2.0.0.
        public SemVersion TildeCeiling() => Minor is null ? new SemVersion(Major + 1, 0, 0) : new SemVersion(Major, Minor.Value + 1, 0);
    }
}
