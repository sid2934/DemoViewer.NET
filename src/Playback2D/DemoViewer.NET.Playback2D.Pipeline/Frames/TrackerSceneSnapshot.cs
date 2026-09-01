#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Frames;

/// <summary>
///     Presents a <see cref="EntityTracker" /> as the two read surfaces
///     <c>SceneFrameBuilder.Build(in SceneFrameInput)</c> wants: an
///     <see cref="IReadOnlyList{T}" /> of <see cref="IPlayerState" /> and an
///     <see cref="IReadOnlyEntityView" />.
///     <para>
///         This is the headless twin of the App's <c>ModuleContext</c> player join: the same
///         <see cref="PawnLookup.ForEachLivePawn" /> sweep, the same controller-anchored emission (a dead
///         player keeps a row so the marker layer can hold a gray last-known position), the same
///         <see cref="PositionUtil.CellToWorld" /> reconstruction. It exists so the CLI and the export
///         session can build frames without an App, a dispatcher, or a shared playback clock.
///     </para>
///     <para>
///         <b>Pooled and re-aimed</b>, like the App's: one instance per source, refilled per frame. The
///         emitted <see cref="IPlayerState" />s are transient: copy scalars out, never retain them.
///     </para>
/// </summary>
public sealed class TrackerSceneSnapshot
{
    // CS2 seats controllers at entity index slot+1. Sixty-four covers every slot a GOTV demo can carry.
    private const int MaxSlots = 64;

    // Held, not written inline at the call site. A lambda that captures `this` is NOT cached by Roslyn
    // (only a fully non-capturing one is), so `ForEachLivePawn(tracker, (slot, pawn) => …)` allocates a
    // fresh delegate on every single frame, in the one adapter that runs once per exported frame.
    private readonly Action<int, EntityState> _collectPawn;
    private readonly EntityView _entities = new();
    private readonly Dictionary<int, string> _labelBySlot = [];
    private readonly Dictionary<string, string> _labelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<int, EntityState> _pawnBySlot = [];
    private readonly List<IPlayerState> _players = [];
    private readonly List<PooledPlayer> _pool = [];
    private readonly Dictionary<int, ulong> _steamIdBySlot = [];

    /// <summary>Creates a snapshot with its pools and its per-frame callback allocated once.</summary>
    public TrackerSceneSnapshot() => _collectPawn = (slot, pawn) => _pawnBySlot[slot] = pawn;

    /// <summary>The players as of the last <see cref="Refresh" />. Transient: do not retain.</summary>
    public IReadOnlyList<IPlayerState> Players => _players;

    /// <summary>The entity read surface as of the last <see cref="Refresh" />. Transient.</summary>
    public IReadOnlyEntityView Entities => _entities;

    /// <summary>Slot → two-character marker label, from the controller's networked name.</summary>
    /// <param name="slot">The roster slot.</param>
    public string LabelForSlot(int slot) =>
        _labelBySlot.TryGetValue(slot, out string? label) ? label : Fallback(slot);

    /// <summary>Slot → SteamID for annotation and camera anchoring, or 0 when unresolved.</summary>
    /// <param name="slot">The roster slot.</param>
    public ulong SteamIdForSlot(int slot) => _steamIdBySlot.GetValueOrDefault(slot);

    /// <summary>Re-aims this snapshot at the tracker's current tick. Allocation-free in steady state.</summary>
    /// <param name="tracker">The private tracker to read.</param>
    public void Refresh(EntityTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        EntitySet set = tracker.CurrentEntities;
        _entities.Aim(set);
        _players.Clear();
        _pawnBySlot.Clear();

        PawnLookup.ForEachLivePawn(tracker, _collectPawn);

        // Controller-anchored emission over every seated slot, not just the live pawns: identity comes
        // from the persistent controller, so a dead player still gets a row. Slots are walked in order
        // rather than over the pawn dictionary, because dictionary order is not a stable render order and
        // a golden must not depend on it.
        int used = 0;
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            EntityState? controller = ControllerFor(set, slot);
            _pawnBySlot.TryGetValue(slot, out EntityState? pawn);
            if (controller is null && pawn is null)
            {
                continue;
            }

            CacheIdentity(slot, controller);

            int team = pawn is not null ? CoerceInt(pawn["m_iTeamNum"]) : CoerceInt(controller?["m_iTeamNum"]);
            Vector3? v = pawn is not null ? PositionUtil.CellToWorld(pawn) : null;
            (float X, float Y, float Z)? world = v is { } p ? (p.X, p.Y, p.Z) : null;

            PooledPlayer state = Rent(used++);
            state.Set(slot, team, pawn, controller, world);
            _players.Add(state);
        }
    }

    private static EntityState? ControllerFor(EntitySet set, int slot)
    {
        EntityState? controller = set[slot + 1];
        return controller is not null &&
               controller.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase)
            ? controller
            : null;
    }

    private void CacheIdentity(int slot, EntityState? controller)
    {
        if (controller is null)
        {
            return;
        }

        if (controller["m_iszPlayerName"] is string name && !string.IsNullOrWhiteSpace(name))
        {
            // Interned through a small cache: the name is the same string every frame, and the two-char
            // projection would otherwise allocate once per player per frame.
            if (!_labelCache.TryGetValue(name, out string? label))
            {
                string trimmed = name.Trim();
                label = trimmed.Length <= 2 ? trimmed : trimmed[..2].ToUpperInvariant();
                _labelCache[name] = label;
            }

            _labelBySlot[slot] = label;
        }

        if (controller["m_steamID"] is { } raw)
        {
            _steamIdBySlot[slot] = CoerceUlong(raw);
        }
    }

    private static string Fallback(int slot) => (slot + 1).ToString(CultureInfo.InvariantCulture);

    private PooledPlayer Rent(int index)
    {
        while (_pool.Count <= index)
        {
            _pool.Add(new PooledPlayer());
        }

        return _pool[index];
    }

    // Wire scalars arrive boxed as whichever integral type the field's decoder produced; coerce rather
    // than hard-cast (project_cs2_wire_encoding, mirrored from ModuleContext.CoerceInt).
    private static int CoerceInt(object? value) => value switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        sbyte s => s,
        _ => 0
    };

    private static ulong CoerceUlong(object? value) => value switch
    {
        ulong u => u,
        long l => (ulong)l,
        uint u => u,
        int i => i >= 0 ? (ulong)i : 0,
        string s => ulong.TryParse(s, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : 0,
        _ => 0
    };

    // ── Pooled facades ──────────────────────────────────────────────────────────────────────────────

    private sealed class PooledPlayer : IPlayerState
    {
        private readonly EntityFacade _controller = new();
        private readonly EntityFacade _pawn = new();

        public int Slot { get; private set; }
        public int Team { get; private set; }
        public bool HasLivePawn { get; private set; }
        public IReadOnlyEntity? Pawn { get; private set; }
        public IReadOnlyEntity? Controller { get; private set; }
        public (float X, float Y, float Z)? WorldPosition { get; private set; }

        public void Set(int slot, int team, EntityState? pawn, EntityState? controller,
            (float X, float Y, float Z)? worldPosition)
        {
            Slot = slot;
            Team = team;

            if (pawn is not null)
            {
                _pawn.Aim(pawn);
                Pawn = _pawn;
            }
            else
            {
                Pawn = null;
            }

            if (controller is not null)
            {
                _controller.Aim(controller);
                Controller = _controller;
            }
            else
            {
                Controller = null;
            }

            WorldPosition = worldPosition;
            HasLivePawn = pawn is not null;
        }
    }

    private sealed class EntityFacade : IReadOnlyEntity
    {
        private EntityState? _entity;

        public EntityFacade()
        {
        }

        public EntityFacade(EntityState entity) => _entity = entity;

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

            try
            {
                if (raw is not null && typeof(T).IsValueType)
                {
                    value = (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
            {
                // fall through to the not-found result
            }

            value = default!;
            return false;
        }

        public void Aim(EntityState entity) => _entity = entity;
    }

    private sealed class EntityView : IReadOnlyEntityView
    {
        private readonly EntityFacade _scratch = new();
        private EntitySet? _set;

        public IEnumerable<IReadOnlyEntity> All()
        {
            if (_set is null)
            {
                yield break;
            }

            foreach (EntityState e in _set.All())
            {
                yield return new EntityFacade(e);
            }
        }

        public IEnumerable<IReadOnlyEntity> OfClass(string className)
        {
            if (_set is null)
            {
                yield break;
            }

            foreach (EntityState e in _set.OfClass(className))
            {
                yield return new EntityFacade(e);
            }
        }

        public IReadOnlyEntity? BySerial(int serial)
        {
            if (_set is null)
            {
                return null;
            }

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
            EntityState? e = _set?[entityIndex];
            if (e is null)
            {
                return null;
            }

            _scratch.Aim(e);
            return _scratch;
        }

        public IReadOnlyEntity? ResolveHandle(ulong handle)
        {
            // Both invalid encodings are folded to null: the full-width 0xFFFFFFFF and the narrower
            // 24-bit 0x00FFFFFF, which is what a dead entity's handle looks like on the wire and which
            // would otherwise mask to a perfectly plausible index (16383).
            if (handle is 0 or 0xFFFF_FFFF or 0x00FF_FFFF)
            {
                return null;
            }

            return ByIndex((int)(handle & PawnLookup.EntityIndexMask));
        }

        public void Aim(EntitySet set) => _set = set;
    }
}
