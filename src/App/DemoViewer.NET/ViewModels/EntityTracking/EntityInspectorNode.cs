#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>Classifies a row in the entity inspector tree for display + expansion behaviour.</summary>
public enum EntityInspectorNodeKind
{
    /// <summary>The selected entity at the top of the tree.</summary>
    Root,

    /// <summary>A within-entity grouping of dotted/array fields (e.g. <c>m_pMovementServices</c>).</summary>
    Group,

    /// <summary>A plain networked value (leaf).</summary>
    Scalar,

    /// <summary>A handle field resolving to another live entity — expands into that entity's fields.</summary>
    EntityRef,

    /// <summary>A handle that is null, points at an empty/garbage slot, or would form a cycle (not expandable).</summary>
    DeadRef,

    /// <summary>Lazy-load placeholder; replaced with real children on first expand.</summary>
    Placeholder
}

/// <summary>
///     One row in the entity relationship inspector — a debugger-style tree. The selected entity is
///     the <see cref="EntityInspectorNodeKind.Root" />; its fields are children; dotted/array fields
///     nest under <see cref="EntityInspectorNodeKind.Group" /> sections; and a handle field that
///     resolves to another entity becomes an <see cref="EntityInspectorNodeKind.EntityRef" /> that
///     <b>lazily</b> expands into that entity's own field tree.
///     <para>
///         Cross-entity children load on demand (on first expand) rather than eagerly, so following
///         handles can't build an unbounded or cyclic tree up front — an unexpanded ref carries a
///         single placeholder child so the tree's expander chevron appears.
///     </para>
/// </summary>
public sealed partial class EntityInspectorNode : ObservableObject
{
    private readonly Func<List<EntityInspectorNode>>? _lazyChildren;

    [ObservableProperty]
    private bool _isExpanded;

    private bool _loaded;

    /// <summary>Creates a leaf or eagerly-populated node.</summary>
    public EntityInspectorNode(
        string name, string valueText, string typeText, EntityInspectorNodeKind kind,
        IReadOnlyList<EntityInspectorNode>? children = null)
    {
        Name = name;
        ValueText = valueText;
        TypeText = typeText;
        Kind = kind;
        Children = [];

        if (children is not null)
        {
            foreach (EntityInspectorNode child in children)
            {
                Children.Add(child);
            }

            _loaded = true;
        }
        else
        {
            _loaded = true; // leaf
        }
    }

    /// <summary>Creates a lazily-expanded node (e.g. an entity reference); children build on first expand.</summary>
    public EntityInspectorNode(
        string name, string valueText, string typeText, EntityInspectorNodeKind kind,
        Func<List<EntityInspectorNode>> lazyChildren)
    {
        Name = name;
        ValueText = valueText;
        TypeText = typeText;
        Kind = kind;
        _lazyChildren = lazyChildren;
        Children = [new EntityInspectorNode("…", "", "", EntityInspectorNodeKind.Placeholder)];
    }

    /// <summary>Field name / group name / entity label.</summary>
    public string Name { get; }

    /// <summary>Formatted value, or a <c>→ ClassName #idx</c> reference summary.</summary>
    public string ValueText { get; }

    /// <summary>Wire type (e.g. <c>int32</c>, <c>CHandle&lt; CCSPlayerPawn &gt;</c>); empty for groups.</summary>
    public string TypeText { get; }

    /// <summary>Row classification.</summary>
    public EntityInspectorNodeKind Kind { get; }

    /// <summary>Child rows. Mutated in place when a lazy node first expands.</summary>
    public ObservableCollection<EntityInspectorNode> Children { get; }

    /// <summary>True when this row resolves to another live entity (rendered with an accent).</summary>
    public bool IsEntityRef => Kind == EntityInspectorNodeKind.EntityRef;

    /// <summary>True for null/cycle/garbage references (rendered dimmed).</summary>
    public bool IsDeadRef => Kind == EntityInspectorNodeKind.DeadRef;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || _lazyChildren is null)
        {
            return;
        }

        _loaded = true;
        Children.Clear(); // drop the placeholder
        foreach (EntityInspectorNode child in _lazyChildren())
        {
            Children.Add(child);
        }
    }
}
