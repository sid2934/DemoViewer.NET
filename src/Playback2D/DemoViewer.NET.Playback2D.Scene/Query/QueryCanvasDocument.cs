namespace DemoViewer.NET.Playback2D.Core.Query;

/// <summary>
///     The Query Canvas's ten slots: five per side, each empty or holding one <see cref="QueryToken" />.
///     The layer draws it and the tool mutates it; the App turns the placed tokens into the per-side
///     place-count tokens the round index stores.
///     <para>
///         <b>Partial queries are the normal case.</b> Nothing here requires a slot to be filled, a side
///         to be filled, or the two sides to agree on anything. An empty side is an unconstrained side.
///     </para>
///     <para>
///         Host-independent on purpose: no Skia type, no App type and no place resolution live here, so
///         a fixture can carry one and <c>dv2d</c> can draw it with no window.
///     </para>
/// </summary>
public sealed class QueryCanvasDocument
{
    /// <summary>Rail slots per side: a full five-stack.</summary>
    public const int SlotsPerSide = 5;

    private readonly QueryToken?[] _slots = new QueryToken?[2 * SlotsPerSide];
    private string _mapName = "";

    /// <summary>The map the tokens were placed on, as the demo header spells it. Empty before one is chosen.</summary>
    public string MapName
    {
        get => _mapName;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (string.Equals(_mapName, value, StringComparison.Ordinal))
            {
                return;
            }

            // A token placed on one map means nothing on another: the drop point is that map's world
            // and the place is that map's vocabulary.
            _mapName = value;
            Array.Clear(_slots);
            Bump();
        }
    }

    /// <summary>Bumped on every change; a cheap way for a layer or a view to notice one.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every change, on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>How many slots hold a token, resolved or not.</summary>
    public int PlacedCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The placed tokens, CT row first then T, slot ascending. A fresh list per call.</summary>
    public IReadOnlyList<QueryToken> Placed
    {
        get
        {
            List<QueryToken> placed = [];
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] is { } token)
                {
                    placed.Add(token);
                }
            }

            return placed;
        }
    }

    /// <summary>The token in a slot, or null when the slot is empty.</summary>
    /// <param name="side">The rail row.</param>
    /// <param name="slot">The slot within the row.</param>
    public QueryToken? Get(QuerySide side, int slot) => _slots[Index(side, slot)];

    /// <summary>Whether a slot holds a token.</summary>
    /// <param name="side">The rail row.</param>
    /// <param name="slot">The slot within the row.</param>
    public bool IsPlaced(QuerySide side, int slot) => _slots[Index(side, slot)] is not null;

    /// <summary>
    ///     Puts a token in its slot, replacing whatever was there. Moving a token is a
    ///     <see cref="Place" /> with the same side and slot and a new position.
    /// </summary>
    /// <param name="token">The token; its side and slot say where it goes.</param>
    public void Place(QueryToken token)
    {
        _slots[Index(token.Side, token.Slot)] = token;
        Bump();
    }

    /// <summary>Empties a slot. A no-op when it was already empty.</summary>
    /// <param name="side">The rail row.</param>
    /// <param name="slot">The slot within the row.</param>
    public bool Lift(QuerySide side, int slot)
    {
        int index = Index(side, slot);
        if (_slots[index] is null)
        {
            return false;
        }

        _slots[index] = null;
        Bump();
        return true;
    }

    /// <summary>Empties every slot. The map stays.</summary>
    public void Clear()
    {
        if (PlacedCount == 0)
        {
            return;
        }

        Array.Clear(_slots);
        Bump();
    }

    /// <summary>
    ///     The resolved place names on one side, one per resolved token, in slot order. What the App
    ///     hands to <c>PlaceCountToken.EncodePlaces</c>; unresolved tokens are left out because the
    ///     query cannot name a place for them.
    /// </summary>
    /// <param name="side">The rail row.</param>
    public IReadOnlyList<string> ResolvedPlaces(QuerySide side)
    {
        List<string> places = [];
        for (int slot = 0; slot < SlotsPerSide; slot++)
        {
            if (_slots[Index(side, slot)] is { Place: { } place })
            {
                places.Add(place);
            }
        }

        return places;
    }

    private static int Index(QuerySide side, int slot)
    {
        if (slot < 0 || slot >= SlotsPerSide)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot,
                $"a rail slot is 0 to {SlotsPerSide - 1}");
        }

        return (side == QuerySide.T ? SlotsPerSide : 0) + slot;
    }

    private void Bump()
    {
        Version++;
        Changed?.Invoke();
    }
}
