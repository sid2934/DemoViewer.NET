#region

using System.Globalization;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Transient read-only facade over one live <see cref="EntityState" />. Its backing
///     pointer is RE-AIMED across reads (pooled), so the framework allocates nothing per entity per
///     push. The indexer maps to the allocation-free <c>EntityState["path"]</c> accessor, never
///     <c>EntityState.Fields</c>. Valid only inside the callback; never retain it.
/// </summary>
internal sealed class ReadOnlyEntityFacade : IReadOnlyEntity
{
    private EntityState? _entity;

    public ReadOnlyEntityFacade()
    {
    }

    public ReadOnlyEntityFacade(EntityState entity) => _entity = entity;

    public string ClassName => _entity?.ClassName ?? "";
    public int Serial => _entity?.Serial ?? 0;
    public bool IsInPvs => _entity?.IsInPvs ?? false;
    public object? this[string fieldPath] => _entity?[fieldPath];

    public bool TryGet<T>(string fieldPath, out T value)
    {
        object? raw = _entity?[fieldPath];
        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        // Light numeric coercion for the common boxed-as-other-numeric case (handles/bools, etc.).
        try
        {
            if (raw is not null && typeof(T).IsValueType)
            {
                value = (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // fall through to the not-found result
        }

        value = default!;
        return false;
    }

    /// <summary>Re-aims this facade at another live entity (pooling, no allocation).</summary>
    public void Aim(EntityState entity) => _entity = entity;
}
