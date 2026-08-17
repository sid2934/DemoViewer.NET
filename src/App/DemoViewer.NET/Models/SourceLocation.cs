namespace DemoViewer.NET.Models;

/// <summary>Location of a proto definition within the protobufs submodule.</summary>
public readonly record struct SourceLocation(string RelativeFile, int Line);
