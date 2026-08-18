#region

using CS2DemoKit.Analysis.Plugins;
using DemoViewer.NET.Modules.Abstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Transient read-only view over the authoritative <see cref="EntitySet" /> at the current tick
///. Re-aimed across pushes (no per-push allocation of the view itself). The single
///     lookups (<see cref="BySerial" /> / <see cref="ByIndex" /> / <see cref="ResolveHandle" />) return
///     a shared pooled facade; the enumerations (<see cref="All" /> / <see cref="OfClass" />) yield a
///     fresh facade per element (on-demand module reads, not the per-tick hot path — the pilot reads
///     positions off the pooled <c>PlayerState</c> list).
/// </summary>
internal sealed class ReadOnlyEntityView : IReadOnlyEntityView
{
    private readonly ReadOnlyEntityFacade _scratch = new();
    private EntitySet _set;

    public ReadOnlyEntityView(EntitySet set) => _set = set;

    public IEnumerable<IReadOnlyEntity> All()
    {
        foreach (EntityState e in _set.All())
        {
            yield return new ReadOnlyEntityFacade(e);
        }
    }

    public IEnumerable<IReadOnlyEntity> OfClass(string className)
    {
        foreach (EntityState e in _set.OfClass(className))
        {
            yield return new ReadOnlyEntityFacade(e);
        }
    }

    public IReadOnlyEntity? BySerial(int serial)
    {
        foreach ((int _, EntityState e) in _set.AllIndexed())
        {
            if (e.Serial == serial)
            {
                _scratch.Aim(e);
                return _scratch;
            }
        }

        return null;
    }

    public IReadOnlyEntity? ByIndex(int entityIndex)
    {
        EntityState? e = _set[entityIndex];
        if (e is null)
        {
            return null;
        }

        _scratch.Aim(e);
        return _scratch;
    }

    public IReadOnlyEntity? ResolveHandle(ulong handle)
    {
        // Mask low 14 bits → entity index (PawnLookup.EntityIndexMask). Coerce via the shared
        // unboxing helper's mask so handle semantics match the rest of the codebase.
        if (handle == 0 || handle == 0xFFFF_FFFF)
        {
            return null;
        }

        int index = (int)(handle & PawnLookup.EntityIndexMask);
        return ByIndex(index);
    }

    /// <summary>Re-aims this view at the current authoritative entity set (pooling — no allocation).</summary>
    public void Aim(EntitySet set) => _set = set;
}
