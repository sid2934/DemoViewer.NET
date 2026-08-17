#region

using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using AvPoint = Avalonia.Point;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

/// <summary>
///     Translates the library's interface-driven graph model into MSAGL's GeometryGraph,
///     runs layout, and extracts positions and edge curves back into library coordinates.
/// </summary>
internal static class MsaglTranslator
{
    // Polyline segments per cubic-Bézier curve when flattening a spline route.
    private const int BezierFlattenSteps = 8;

    internal static (
        IReadOnlyDictionary<IGraphNode, NodePosition> NodePositions,
        IReadOnlyDictionary<IGraphEdge, IReadOnlyList<AvPoint>> EdgeRoutes,
        double GraphWidth,
        double GraphHeight
        ) RunLayout(
            IReadOnlyList<IGraphNode> nodes,
            IReadOnlyList<IGraphEdge> edges,
            GraphStyle style)
    {
        NodeStyleConfig nodeStyle = style.Node;
        GeometryGraph graph = new();

        Dictionary<IGraphNode, Node> msaglNodes = new();
        foreach (IGraphNode node in nodes)
        {
            double w = node.Style?.Width ?? nodeStyle.Width;
            double h = node.Style?.Height ?? nodeStyle.Height;
            double cr = nodeStyle.CornerRadius;

            Node msaglNode = new(
                CurveFactory.CreateRectangleWithRoundedCorners(w, h, cr, cr, new MsaglPoint(0, 0)));
            msaglNode.UserData = node;
            graph.Nodes.Add(msaglNode);
            msaglNodes[node] = msaglNode;
        }

        Dictionary<Edge, IGraphEdge> edgeMap = new();
        foreach (IGraphEdge edge in edges)
        {
            if (ReferenceEquals(edge.Source, edge.Destination))
            {
                continue;
            }

            if (!msaglNodes.TryGetValue(edge.Source, out Node? srcNode))
            {
                continue;
            }

            if (!msaglNodes.TryGetValue(edge.Destination, out Node? dstNode))
            {
                continue;
            }

            Edge msaglEdge = new(srcNode, dstNode);
            graph.Edges.Add(msaglEdge);
            edgeMap[msaglEdge] = edge;
        }

        LayoutStyleConfig ls = style.Layout;
        SugiyamaLayoutSettings settings = new()
        {
            Transformation = PlaneTransformation.Rotation(Math.PI / 2),
            NodeSeparation = ls.NodeSeparation,
            LayerSeparation = ls.LayerSeparation
        };
        settings.EdgeRoutingSettings.EdgeRoutingMode = EdgeRoutingMode.Rectilinear;
        settings.EdgeRoutingSettings.Padding = ls.EdgeRoutingPadding;

        LayoutHelpers.CalculateLayout(graph, settings, null);

        // MSAGL uses Y-up; Avalonia uses Y-down. Flip all Y coordinates.
        Rectangle bb = graph.BoundingBox;
        double maxY = bb.Top; // In MSAGL, Top > Bottom (Y-up)
        double minX = bb.Left;
        const double Pad = 60;

        double FlipY(double y)
        {
            return maxY - y + Pad;
        }

        double AdjustX(double x)
        {
            return x - minX + Pad;
        }

        Dictionary<IGraphNode, NodePosition> positions = new();
        foreach ((IGraphNode graphNode, Node msaglNode) in msaglNodes)
        {
            Point center = msaglNode.Center;
            positions[graphNode] = new NodePosition(AdjustX(center.X), FlipY(center.Y));
        }

        Dictionary<IGraphEdge, IReadOnlyList<AvPoint>> routes = new();
        foreach ((Edge msaglEdge, IGraphEdge graphEdge) in edgeMap)
        {
            if (msaglEdge.Curve is null)
            {
                continue;
            }

            routes[graphEdge] = CurveToWaypoints(msaglEdge.Curve, AdjustX, FlipY);
        }

        return (positions, routes, bb.Width + 2 * Pad, bb.Height + 2 * Pad);
    }

    private static List<AvPoint> CurveToWaypoints(ICurve curve,
        Func<double, double> adjustX, Func<double, double> flipY)
    {
        List<AvPoint> points = new();

        if (curve is Curve composite)
        {
            foreach (ICurve seg in composite.Segments)
            {
                switch (seg)
                {
                    case LineSegment line:
                        if (points.Count == 0)
                        {
                            points.Add(new AvPoint(adjustX(line.Start.X), flipY(line.Start.Y)));
                        }

                        points.Add(new AvPoint(adjustX(line.End.X), flipY(line.End.Y)));
                        break;

                    case CubicBezierSegment bezier:
                        // B(1)/B(2) are the cubic CONTROL points, which generally
                        // do NOT lie on the curve — pushing them as polyline
                        // vertices mis-traces the segment. Flatten by sampling the
                        // curve itself at intermediate parameters instead. (Dead
                        // under EdgeRoutingMode.Rectilinear today; correct the
                        // moment spline/bundled routing is enabled.)
                        if (points.Count == 0)
                        {
                            points.Add(new AvPoint(adjustX(bezier.B(0).X), flipY(bezier.B(0).Y)));
                        }

                        for (int s = 1; s <= BezierFlattenSteps; s++)
                        {
                            double t = (double)s / BezierFlattenSteps;
                            Point p = bezier[bezier.ParStart + t * (bezier.ParEnd - bezier.ParStart)];
                            points.Add(new AvPoint(adjustX(p.X), flipY(p.Y)));
                        }

                        break;

                    default:
                        if (points.Count == 0)
                        {
                            points.Add(new AvPoint(adjustX(seg.Start.X), flipY(seg.Start.Y)));
                        }

                        points.Add(new AvPoint(adjustX(seg.End.X), flipY(seg.End.Y)));
                        break;
                }
            }
        }
        else if (curve is LineSegment single)
        {
            points.Add(new AvPoint(adjustX(single.Start.X), flipY(single.Start.Y)));
            points.Add(new AvPoint(adjustX(single.End.X), flipY(single.End.Y)));
        }
        else
        {
            points.Add(new AvPoint(adjustX(curve.Start.X), flipY(curve.Start.Y)));
            points.Add(new AvPoint(adjustX(curve.End.X), flipY(curve.End.Y)));
        }

        return points;
    }
}
