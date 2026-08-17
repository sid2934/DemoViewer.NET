#region

using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>Analysis node group data record.</summary>
public sealed record AnalysisNodeGroup(string GroupName, IReadOnlyList<IGraphNode> Members) : INodeGroup;
