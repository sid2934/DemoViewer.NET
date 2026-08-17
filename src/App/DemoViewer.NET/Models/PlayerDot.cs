#region

using System.Numerics;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>Player dot.</summary>
public class PlayerDot
{
    /// <summary>Class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Field sources.</summary>
    public required IReadOnlyList<FieldSourceEntry> FieldSources { get; init; }

    /// <summary>Health.</summary>
    public required int Health { get; init; }

    /// <summary>Is alive.</summary>
    public required bool IsAlive { get; init; }

    /// <summary>Serial.</summary>
    public required int Serial { get; init; }

    /// <summary>Team num.</summary>
    public required int TeamNum { get; init; } // 2=T, 3=CT

    /// <summary>World pos.</summary>
    public required Vector3 WorldPos { get; init; }
}
