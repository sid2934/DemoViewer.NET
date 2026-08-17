namespace DemoViewer.NET.ViewModels;

/// <summary>One decoded field row inside an <see cref="AnalysisMessageViewModel" />.</summary>
public sealed record AnalysisMessageFieldViewModel(string Name, string Value, string WireType);
