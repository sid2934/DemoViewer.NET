namespace DemoViewer.NET.Models;

/// <summary>
///     One flattened [byteRange → <see cref="PayloadNode" />] entry for reverse hit-testing
///     (F5.2). <see cref="End" /> is exclusive.
/// </summary>
public readonly record struct ByteRangeEntry(int Start, int End, int Depth, PayloadNode Node);

/// <summary>
///     Reverse byte → node mapping for the hex view.
///     <para>
///         The design doc placed these helpers in the parser's <c>DownstreamUtilities.cs</c>, but
///         <see cref="PayloadNode" /> is an App-project type: the parser library cannot reference it
///         without an inverted dependency. The algorithm is unchanged; only the home moves here, next
///         to <see cref="PayloadNode" /> / <see cref="PayloadNodeBuilder" />.
///     </para>
/// </summary>
public static class PayloadNodeByteRangeIndex
{
    /// <summary>
    ///     Builds a flat list of [byteRange → <see cref="PayloadNode" />] sorted ascending by
    ///     <see cref="ByteRangeEntry.Start" /> (outer ranges first within an equal Start) for fast
    ///     hit-testing when the user clicks a byte. Byte ranges are taken from
    ///     <see cref="PayloadNode.ByteStart" /> / <see cref="PayloadNode.ByteLength" />. Cheaper than
    ///     an interval tree for the sizes we see: most cards have &lt; 200 ranges.
    /// </summary>
    public static List<ByteRangeEntry> Build(IEnumerable<PayloadNode> roots)
    {
        List<ByteRangeEntry> list = new(256);
        foreach (PayloadNode root in roots)
        {
            Walk(root, 0, list);
        }

        list.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : b.End.CompareTo(a.End)); // tie-break: outer (wider) ranges first
        return list;

        static void Walk(PayloadNode n, int depth, List<ByteRangeEntry> acc)
        {
            if (n.HasByteRange)
            {
                int s = n.ByteStart;
                int e = n.ByteStart + n.ByteLength;
                if (e > s)
                {
                    acc.Add(new ByteRangeEntry(s, e, depth, n));
                }
            }

            foreach (PayloadNode child in n.Children)
            {
                Walk(child, depth + 1, acc);
            }
        }
    }

    /// <summary>
    ///     Finds the deepest (innermost) <see cref="PayloadNode" /> whose byte range contains
    ///     <paramref name="byteOffset" />, or <see langword="null" /> if no range covers it.
    /// </summary>
    public static PayloadNode? FindContainingNode(IReadOnlyList<ByteRangeEntry> index, int byteOffset)
    {
        // Lower-bound binary search: latest Start <= byteOffset.
        int lo = 0, hi = index.Count;
        while (lo < hi)
        {
            int mid = lo + hi >> 1;
            if (index[mid].Start <= byteOffset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // Walk back from lo-1, keeping the deepest range still containing the offset.
        PayloadNode? best = null;
        int bestDepth = -1;
        for (int i = lo - 1; i >= 0; i--)
        {
            ByteRangeEntry r = index[i];
            if (r.End <= byteOffset)
            {
                continue; // Start <= offset (search invariant) but ends before it
            }

            if (r.Depth > bestDepth)
            {
                best = r.Node;
                bestDepth = r.Depth;
            }
        }

        return best;
    }
}
