namespace DemoViewer.NET.Models;

/// <summary>Field source entry.</summary>
public class FieldSourceEntry
{
    /// <summary>Entity class.</summary>
    public required string EntityClass { get; init; }

    /// <summary>Entity serial.</summary>
    public required int EntitySerial { get; init; }

    /// <summary>Field name.</summary>
    public required string FieldName { get; init; }

    /// <summary>Value.</summary>
    public required string Value { get; init; }
}
