#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>
///     Sanitizes untrusted user-generated strings (CS2 player names) for DISPLAY in wrapping
///     TextBlocks. Invisible Unicode format characters — bidi isolates/embeddings (U+2066–2069,
///     U+202A–202E), zero-width marks — combined with degenerate sequences (orphaned combining
///     marks) produce shaped text runs that crash Avalonia's line-wrap splitter
///     ("Cannot split: requested length N consumes entire run", ShapedTextRun.Split; hit in the
///     wild via a player named "ุ⁧⁧Vetxed"). Stripping the Format category is proven sufficient
///     to make such strings measurable at every width, and removes nothing visible.
/// </summary>
public static class DisplayText
{
    /// <summary>Returns <paramref name="text" /> with all Unicode Format-category chars removed.</summary>
    public static string Sanitize(string text)
    {
        // Fast path: scan first — player names are overwhelmingly clean.
        bool clean = true;
        foreach (char c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        return new string(text
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .ToArray());
    }
}
