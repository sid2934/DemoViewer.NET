#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.RuleAuthoring;

/// <summary>
///     One splice into a document: replace <paramref name="Length" /> characters at
///     <paramref name="Start" /> with <paramref name="Replacement" />. An insertion has length 0; a
///     deletion has an empty replacement.
/// </summary>
/// <param name="Start">0-based character offset into the original document.</param>
/// <param name="Length">Characters replaced. Zero for an insertion.</param>
/// <param name="Replacement">Text to put there. Empty for a deletion.</param>
/// <param name="Description">What the edit is for, used in error messages and undo labels.</param>
public readonly record struct TextEdit(int Start, int Length, string Replacement, string Description)
{
    /// <summary>The offset one past the last character this edit replaces.</summary>
    public int End => Start + Length;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Start}..{End}) -> {Replacement.Length} chars: {Description}");
}

/// <summary>Applies a batch of <see cref="TextEdit" /> to a document.</summary>
public static class TextEditor
{
    /// <summary>
    ///     Applies every edit to <paramref name="source" /> and returns the result.
    ///     <para>
    ///         Every edit is addressed against the ORIGINAL text and they are applied from the end
    ///         backwards, so no edit has to know what an earlier one did to the offsets. Overlapping
    ///         edits are rejected rather than resolved: two operations fighting over the same span is
    ///         a caller bug, and silently letting one win is how an editor corrupts a file.
    ///     </para>
    /// </summary>
    /// <param name="source">The original document text.</param>
    /// <param name="edits">The edits, in any order.</param>
    /// <exception cref="ArgumentOutOfRangeException">An edit falls outside the document.</exception>
    /// <exception cref="InvalidOperationException">Two edits overlap.</exception>
    public static string Apply(string source, IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edits);

        if (edits.Count == 0)
        {
            return source;
        }

        List<TextEdit> ordered = [.. edits];
        ordered.Sort(static (a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : a.Length.CompareTo(b.Length));

        for (int i = 0; i < ordered.Count; i++)
        {
            TextEdit edit = ordered[i];
            if (edit.Start < 0 || edit.Length < 0 || edit.End > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"{edit} is outside a {source.Length}-character document");
            }

            // Zero-length insertions at the same offset are the one legal coincidence: two inserts
            // at the same point are ordered, not overlapping.
            if (i > 0 && ordered[i - 1].End > edit.Start)
            {
                throw new InvalidOperationException(
                    $"overlapping edits: {ordered[i - 1]} and {edit}");
            }
        }

        System.Text.StringBuilder sb = new(source, source.Length);
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            TextEdit edit = ordered[i];
            sb.Remove(edit.Start, edit.Length);
            sb.Insert(edit.Start, edit.Replacement);
        }

        return sb.ToString();
    }
}
