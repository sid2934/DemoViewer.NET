#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     Backs the entity list (F6.1). A virtualized <c>ListBox</c> over a stable collection
///     rebuilt per seek; the ~2k entity rows virtualize cleanly under a
///     <c>VirtualizingStackPanel</c>. Row selection is surfaced via
///     <see cref="EntitySelected" /> so the owning <c>EntityTrackingTabViewModel</c> can drive
///     the field tree + delta log + schema view.
///     <para>
///         Deviation from the design doc: it specified <c>Avalonia.Controls.TreeDataGrid</c>, but
///         that package is a commercial product under Avalonia 11.3's licensing (AVLIC0001 blocks
///         the build without a paid key, at every TDG version). The columnar virtualized ListBox
///         is the doc's named fallback shape and closes F6.1 without the license footgun.
///     </para>
/// </summary>
public sealed partial class EntityListViewModel : ObservableObject
{
    [ObservableProperty]
    private EntityNode? _selectedNode;

    private bool _suppressSelectionEvent;

    /// <summary>Entities.</summary>
    public ObservableCollection<EntityNode> Entities { get; } = [];

    /// <summary>Clear.</summary>
    public void Clear() => Rebuild([]);

    /// <summary>Fired when the selected entity row changes (null when cleared).</summary>
    public event Action<EntityNode?>? EntitySelected;

    /// <summary>Replaces the rows with a fresh entity set after a seek.</summary>
    public void Rebuild(IReadOnlyList<EntityNode> nodes)
    {
        _suppressSelectionEvent = true;
        SelectedNode = null;
        Entities.Clear();
        foreach (EntityNode node in nodes)
        {
            Entities.Add(node);
        }

        _suppressSelectionEvent = false;
    }

    /// <summary>
    ///     Highlights the row backing <paramref name="entity" /> without re-raising
    ///     <see cref="EntitySelected" /> (the selection originated upstream). No-op when the
    ///     entity isn't a visible row (e.g. filtered out by the class browser).
    /// </summary>
    public void SelectByEntity(EntityState? entity)
    {
        EntityNode? match = null;
        if (entity is not null)
        {
            foreach (EntityNode node in Entities)
            {
                if (ReferenceEquals(node.Entity, entity))
                {
                    match = node;
                    break;
                }
            }
        }

        _suppressSelectionEvent = true;
        SelectedNode = match;
        _suppressSelectionEvent = false;
    }

    partial void OnSelectedNodeChanged(EntityNode? value)
    {
        if (_suppressSelectionEvent)
        {
            return;
        }

        EntitySelected?.Invoke(value);
    }
}
