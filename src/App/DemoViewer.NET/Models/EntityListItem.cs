#region

using Cs2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>Entity list item.</summary>
public class EntityListItem
{
    /// <summary>Delta count.</summary>
    public int DeltaCount { get; init; }

    /// <summary>Badge text shown next to entity name when fields changed.</summary>
    public string DeltaText => DeltaCount > 0 ? $"{DeltaCount} Δ" : "";

    /// <summary>"ClassName  (serial)" for entity rows; separator text for header rows.</summary>
    public string DisplayName => IsHeader
        ? "── All entities ──"
        : $"{Entity!.ClassName}  ({Entity.Serial})";

    /// <summary>Entity.</summary>
    public EntityState? Entity { get; init; }

    /// <summary>Has delta.</summary>
    public bool HasDelta => DeltaCount > 0;

    /// <summary>Is header.</summary>
    public bool IsHeader { get; init; }

    /// <summary>Is selectable.</summary>
    public bool IsSelectable => !IsHeader;

    /// <summary>Entity slot index (carried so the entity grid avoids an O(N²) lookup). -1 for headers.</summary>
    public int SlotIndex { get; init; } = -1;
}
