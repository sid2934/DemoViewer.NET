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
        //
        // BOTH invalid encodings are folded to null, the full-width 0xFFFFFFFF and the narrower
        // 0x00FFFFFF, matching EntityTracker.ResolveHandle's contract. The networked form is 14
        // index bits then 10 serial bits with no gap, so the 24-bit all-ones sentinel is what a
        // dead entity's handle actually looks like on the wire — and it masks to a perfectly
        // plausible index (16383) rather than to anything obviously wrong. Missing it therefore
        // does not throw or return garbage; it silently resolves a DEAD reference to whatever
        // occupies that slot. This view reimplements the mask rather than delegating, so it did
        // not inherit the fix when the parser folded these upstream in 0.9.2 — keep the two in
        // step, or a module reading a dead pawn's weapon handle gets a live answer.
        if (handle is 0 or 0xFFFF_FFFF or 0x00FF_FFFF)
        {
            return null;
        }

        int index = (int)(handle & PawnLookup.EntityIndexMask);
        return ByIndex(index);
    }

    /// <summary>Re-aims this view at the current authoritative entity set (pooling — no allocation).</summary>
    public void Aim(EntitySet set) => _set = set;
}
