#region

using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     One row in the entity <c>TreeDataGrid</c>. Wraps a live
///     <see cref="EntityState" /> with the slot index and a mutable delta count so the
///     grid can surface "ΔN" per seek. Hierarchical so a later milestone can group by
///     class without changing the source type.
/// </summary>
public sealed partial class EntityNode : ObservableObject
{
    [ObservableProperty]
    private int _deltaCount;

    /// <summary>Flat today; the hierarchical source keeps the grouping door open.</summary>
    public IReadOnlyList<EntityNode> Children { get; init; } = [];

    /// <summary>Class name.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>Δ-badge text for the grid column (empty when no fields changed).</summary>
    public string DeltaText => DeltaCount > 0 ? $"Δ{DeltaCount}" : "";

    /// <summary>Dormant.</summary>
    public bool Dormant { get; init; }

    /// <summary>Check-mark for the dormant column (empty when in-PVS).</summary>
    public string DormantText => Dormant ? "✓" : "";

    /// <summary>Backing live entity: funnelled into the tab VM's selection on row select.</summary>
    public EntityState? Entity { get; init; }

    /// <summary>Index.</summary>
    public int Index { get; init; }

    /// <summary>Serial.</summary>
    public int Serial { get; init; }
}
