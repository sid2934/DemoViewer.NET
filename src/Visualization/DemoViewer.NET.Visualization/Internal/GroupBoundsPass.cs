#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Pass 3 — Group bounds. AABB over each group's member node boxes plus padding,
///     honouring per-node box sizes via <see cref="LayoutContext.NodeRect" /> rather
///     than assuming the theme default.
/// </summary>
internal static class GroupBoundsPass
{
    private const double Pad = 30;

    internal static void Run(LayoutContext ctx)
    {
        List<GroupBounds> result = new();
        if (ctx.Groups is null)
        {
            ctx.GroupBounds = result;
            return;
        }

        foreach (INodeGroup group in ctx.Groups)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (IGraphNode member in group.Members)
            {
                if (!ctx.NodePositions.ContainsKey(member))
                {
                    continue;
                }

                Rect r = ctx.NodeRect(member);
                if (r.Left < minX)
                {
                    minX = r.Left;
                }

                if (r.Top < minY)
                {
                    minY = r.Top;
                }

                if (r.Right > maxX)
                {
                    maxX = r.Right;
                }

                if (r.Bottom > maxY)
                {
                    maxY = r.Bottom;
                }
            }

            if (minX < double.MaxValue)
            {
                result.Add(new GroupBounds(group.GroupName,
                    minX - Pad, minY - Pad,
                    maxX - minX + 2 * Pad, maxY - minY + 2 * Pad));
            }
        }

        ctx.GroupBounds = result;
    }
}
