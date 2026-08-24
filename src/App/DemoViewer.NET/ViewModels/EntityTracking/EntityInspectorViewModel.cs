#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     Builds the debugger-style relationship tree for the selected entity: a root node whose
///     children are the entity's full property list (dotted/array fields grouped into collapsible
///     sections), where every handle field that resolves to another live entity becomes an
///     expandable node that lazily reveals <i>that</i> entity's properties — recursively, from the
///     root parent down to all of its referenced children.
///     <para>
///         CS2 entities are flat field bags whose only inter-entity links are <c>CHandle&lt;T&gt;</c>
///         fields carrying an <c>index | (serial &lt;&lt; 14)</c> reference. Handle detection is
///         <b>type-driven</b> (<see cref="EntityTracker.GetFieldMeta" /> →
///         <c>
///             CHandle/CStrongHandle/
///             CEntityHandle
///         </c>
///         ), never name-guessed: a numeric field equal to an entity index (e.g.
///         <c>m_iHealth == 100</c>) is a scalar, not a reference. Cross-entity expansion is lazy and
///         cycle-guarded (an ancestor set per branch), so following handles never loops or builds an
///         unbounded tree.
///     </para>
/// </summary>
public sealed partial class EntityInspectorViewModel : ObservableObject
{
    private const uint IndexMask = 0x3FFF;
    private const int InvalidIndex = 0x3FFF;
    private const int SerialShift = 14;

    [ObservableProperty]
    private bool _hasTree;

    [ObservableProperty]
    private ObservableCollection<EntityInspectorNode> _rootNodes = [];

    [ObservableProperty]
    private string _statusText = "Select an entity, then toggle Relations to walk its references.";

    /// <summary>Resets the tree to empty.</summary>
    public void Clear()
    {
        RootNodes = [];
        HasTree = false;
        StatusText = "Select an entity, then toggle Relations to walk its references.";
    }

    /// <summary>
    ///     Rebuilds the tree rooted at <paramref name="focus" />. Synchronous: only the focus
    ///     entity's own field tree is built now; referenced entities expand lazily on demand.
    /// </summary>
    public void BuildFor(EntityState? focus, EntityTracker? tracker)
    {
        try
        {
            if (focus is null || tracker is null)
            {
                Clear();
                return;
            }

            int focusIndex = IndexOf(tracker, focus);
            if (focusIndex < 0)
            {
                Clear();
                return;
            }

            List<EntityInspectorNode> fields = BuildEntityFields(tracker, focus, new HashSet<int>
            {
                focusIndex
            });
            EntityInspectorNode root = new(
                $"{focus.ClassName} #{focusIndex}", "", "root", EntityInspectorNodeKind.Root, fields)
            {
                IsExpanded = true
            };

            RootNodes = [root];
            HasTree = true;
            StatusText = $"{focus.ClassName} #{focusIndex}  ·  {fields.Count} top-level rows";
        }
        catch (Exception ex)
        {
            Clear();
            StatusText = $"Inspector build failed: {ex.Message}";
        }
    }

    /// <summary>
    ///     Builds one entity's complete field tree: dotted/array paths grouped into nested sections,
    ///     scalars as leaves, and handle fields as (lazy) entity references. Finite — never recurses
    ///     into another entity (that happens on expand via the lazy factory).
    /// </summary>
    private static List<EntityInspectorNode> BuildEntityFields(
        EntityTracker tracker, EntityState entity, IReadOnlySet<int> ancestors)
    {
        GroupBuilder root = new();

        foreach (string key in entity.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            object? value = entity.Fields[key];
            List<string> segments = SplitSegments(key);

            GroupBuilder group = root;
            for (int i = 0; i < segments.Count - 1; i++)
            {
                group = group.Child(segments[i]);
            }

            group.Leaves.Add(BuildLeaf(tracker, entity.ClassName, key, segments[^1], value, ancestors));
        }

        return root.ToNodes();
    }

    /// <summary>Builds a single leaf: an entity reference (handle) or a scalar value.</summary>
    private static EntityInspectorNode BuildLeaf(
        EntityTracker tracker, string className, string fullKey, string leafName, object? value,
        IReadOnlySet<int> ancestors)
    {
        RuntimeField? meta = MetaFor(tracker, className, fullKey);
        string typeText = meta is null ? "" : Collapse(meta.TypeName);

        if (meta is null || !IsHandleType(meta.TypeName))
        {
            return new EntityInspectorNode(leafName, MainViewModel.FormatValue(value), typeText,
                EntityInspectorNodeKind.Scalar);
        }

        uint h = PawnLookup.TryUnboxHandle(value);
        int idx = (int)(h & IndexMask);
        if (h == 0 || idx == InvalidIndex)
        {
            return new EntityInspectorNode(leafName, "→ (null)", typeText, EntityInspectorNodeKind.DeadRef);
        }

        EntityState? target = tracker.CurrentEntities[idx];
        if (target is null)
        {
            return new EntityInspectorNode(leafName, $"→ #{idx} (empty slot)", typeText,
                EntityInspectorNodeKind.DeadRef);
        }

        if (ancestors.Contains(idx))
        {
            return new EntityInspectorNode(leafName, $"↑ {target.ClassName} #{idx} (shown above)", typeText,
                EntityInspectorNodeKind.DeadRef);
        }

        bool stale = target.Serial != (int)(h >> SerialShift);
        string summary = $"→ {target.ClassName} #{idx}" + (stale ? "  (stale)" : "");

        // Capture for the lazy expand: build the target entity's tree with this index added to the
        // ancestor set so a back-reference doesn't recurse forever.
        HashSet<int> childAncestors = new(ancestors)
        {
            idx
        };
        EntityState capturedTarget = target;
        return new EntityInspectorNode(leafName, summary, typeText, EntityInspectorNodeKind.EntityRef,
            () => BuildEntityFields(tracker, capturedTarget, childAncestors));
    }

    private static int IndexOf(EntityTracker tracker, EntityState entity)
    {
        foreach ((int idx, EntityState e) in tracker.CurrentEntities.AllIndexed())
        {
            if (ReferenceEquals(e, entity))
            {
                return idx;
            }
        }

        return -1;
    }

    // Array elements arrive as `m_hMyWeapons[3]`; descriptors are keyed on the base path.
    private static RuntimeField? MetaFor(EntityTracker tracker, string className, string key)
        => tracker.GetFieldMeta(className, key) ?? tracker.GetFieldMeta(className, StripArrayIndex(key));

    // ENTITY handles only — CHandle<T> and untyped CEntityHandle, the 32-bit group whose packed
    // index+serial this inspector resolves through CurrentEntities. The four RESOURCE names
    // (CStrongHandle<T>, CStrongHandleCopyable<T>, CStrongHandleVoid, CWeakHandle<T>) are 64-bit
    // resource IDs with unrelated sentinels; masking one with the entity index mask renders a
    // phantom "→ Class #idx" reference to an arbitrary live entity. StartsWith("CStrongHandle")
    // is the exact prefix trap the SDK's docs/HANDLES.md spec calls out — it also catches
    // Copyable and Void. Resource handles fall through to the scalar leaf, which is the correct
    // rendering: an opaque number.
    private static bool IsHandleType(string typeName)
        => typeName.StartsWith("CHandle", StringComparison.Ordinal)
           || typeName.StartsWith("CEntityHandle", StringComparison.Ordinal);

    // "m_pMovementServices.m_flMaxspeed" → [m_pMovementServices, m_flMaxspeed];
    // "m_hMyWeapons[0]" → [m_hMyWeapons, [0]] so arrays nest under a single group.
    private static List<string> SplitSegments(string key)
    {
        List<string> result = [];
        foreach (string part in key.Split('.'))
        {
            int bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket > 0)
            {
                result.Add(part[..bracket]);
                result.Add(part[bracket..]);
            }
            else
            {
                result.Add(part);
            }
        }

        return result;
    }

    private static string StripArrayIndex(string key)
    {
        int bracket = key.LastIndexOf('[');
        return bracket > 0 ? key[..bracket] : key;
    }

    private static string Collapse(string typeName) =>
        string.Join(' ', typeName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Mutable scaffold for assembling the within-entity group tree before materialising nodes.</summary>
    private sealed class GroupBuilder
    {
        private readonly Dictionary<string, GroupBuilder> _groups = [];
        public List<EntityInspectorNode> Leaves { get; } = [];

        public GroupBuilder Child(string name)
        {
            if (!_groups.TryGetValue(name, out GroupBuilder? child))
            {
                child = new GroupBuilder();
                _groups[name] = child;
            }

            return child;
        }

        public List<EntityInspectorNode> ToNodes()
        {
            List<EntityInspectorNode> nodes = [];
            foreach ((string name, GroupBuilder group) in _groups.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                nodes.Add(new EntityInspectorNode(name, "", "", EntityInspectorNodeKind.Group, group.ToNodes()));
            }

            nodes.AddRange(Leaves);
            return nodes;
        }
    }
}
