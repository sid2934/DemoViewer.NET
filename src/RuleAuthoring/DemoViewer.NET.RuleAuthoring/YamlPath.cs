#region

using System.Globalization;
using System.Text;

#endregion

namespace DemoViewer.NET.RuleAuthoring;

/// <summary>
///     Where a value lives in a YAML document: a sequence of mapping keys and sequence indices from
///     the root, like <c>stats.kills.label</c> or <c>show.scoreboard[2]</c>.
///     <para>
///         A path rather than a node reference, because the node reference is invalidated the moment
///         anything is spliced. An edit is expressed against the ORIGINAL document and applied to
///         the original text, so a path stays meaningful across a whole batch.
///     </para>
/// </summary>
public sealed record YamlPath
{
    private YamlPath(IReadOnlyList<YamlPathSegment> segments) => Segments = segments;

    /// <summary>The document root.</summary>
    public static YamlPath Root { get; } = new([]);

    /// <summary>The segments from the root, in order. Empty for <see cref="Root" />.</summary>
    public IReadOnlyList<YamlPathSegment> Segments { get; }

    /// <summary>This path with one more mapping key on the end.</summary>
    public YamlPath Key(string key) => new([.. Segments, YamlPathSegment.ForKey(key)]);

    /// <summary>This path with one more sequence index on the end.</summary>
    public YamlPath Index(int index) => new([.. Segments, YamlPathSegment.ForIndex(index)]);

    /// <summary>The path without its last segment, or <c>null</c> at the root.</summary>
    public YamlPath? Parent =>
        Segments.Count == 0 ? null : new YamlPath([.. Segments.Take(Segments.Count - 1)]);

    /// <inheritdoc />
    public override string ToString()
    {
        if (Segments.Count == 0)
        {
            return "<root>";
        }

        StringBuilder sb = new();
        foreach (YamlPathSegment segment in Segments)
        {
            if (segment.Key is { } key)
            {
                if (sb.Length > 0)
                {
                    sb.Append('.');
                }

                sb.Append(key);
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"[{segment.Index}]");
            }
        }

        return sb.ToString();
    }
}

/// <summary>One step of a <see cref="YamlPath" />: a mapping key, or a sequence index.</summary>
/// <param name="Key">The mapping key, or <c>null</c> when this is a sequence index.</param>
/// <param name="Index">The sequence index, or -1 when this is a mapping key.</param>
public readonly record struct YamlPathSegment(string? Key, int Index)
{
    internal static YamlPathSegment ForIndex(int index) => new(null, index);

    internal static YamlPathSegment ForKey(string key) => new(key, -1);
}
