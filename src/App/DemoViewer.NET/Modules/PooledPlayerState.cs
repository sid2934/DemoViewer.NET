#region

using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Pooled, mutable <see cref="IPlayerState" /> instance (POOLED: the
///     choice that honors the zero-per-tick-framework-alloc invariant). The host keeps a
///     fixed set of these (~10) and re-aims their fields on each push instead of allocating new ones.
///     It owns its own pawn/controller <see cref="ReadOnlyEntityFacade" /> instances, also re-aimed,
///     so a full per-tick join allocates nothing on the framework hot path. Modules copy out what they
///     need inside the callback; they must not retain the instance (transient rule).
/// </summary>
internal sealed class PooledPlayerState : IPlayerState
{
    private readonly ReadOnlyEntityFacade _controllerFacade = new();
    private readonly ReadOnlyEntityFacade _pawnFacade = new();

    public int Slot { get; private set; }
    public int Team { get; private set; }
    public bool HasLivePawn { get; private set; }
    public IReadOnlyEntity? Pawn { get; private set; }
    public IReadOnlyEntity? Controller { get; private set; }
    public (float X, float Y, float Z)? WorldPosition { get; private set; }

    /// <summary>
    ///     Re-aims this pooled instance (and its owned facades) to a new per-tick join result. Pass a
    ///     null <paramref name="pawn" /> for a roster player with no live pawn this tick (dead/orphaned or
    ///     pre-spawn): <see cref="HasLivePawn" /> reflects that and <see cref="Pawn" /> reads null, so the
    ///     module can gray the card / hold the last-known marker. Pass a null controller for a pawn not yet
    ///     bound. No allocation.
    /// </summary>
    public void Set(int slot, int team, EntityState? pawn, EntityState? controller,
        (float X, float Y, float Z)? worldPosition)
    {
        Slot = slot;
        Team = team;

        if (pawn is not null)
        {
            _pawnFacade.Aim(pawn);
            Pawn = _pawnFacade;
        }
        else
        {
            Pawn = null;
        }

        if (controller is not null)
        {
            _controllerFacade.Aim(controller);
            Controller = _controllerFacade;
        }
        else
        {
            Controller = null;
        }

        WorldPosition = worldPosition;
        HasLivePawn = pawn is not null;
    }
}
