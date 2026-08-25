namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     What one <see cref="MapSpace.Rebuild" /> did to the level set. <c>PaneSet</c> reconciles against
///     it, B3's level-crossing buffers key off the added/removed ids, and
///     <see cref="TryRemapAnchor" /> rebases annotation anchors that named a band which no longer
///     exists (design §5.3, plan D6).
/// </summary>
/// <param name="Changed">False when the rebuild was a no-op (an identical band list).</param>
/// <param name="Added">Ids present after the rebuild but not before.</param>
/// <param name="Removed">Ids present before but not after.</param>
/// <param name="Retained">Ids present in both — the ones whose panes keep their camera.</param>
public sealed record LevelSetChange(
    bool Changed,
    IReadOnlyList<MapLevelId> Added,
    IReadOnlyList<MapLevelId> Removed,
    IReadOnlyList<MapLevelId> Retained)
{
    private static readonly Dictionary<MapLevelId, MapLevelId> _noRemap = [];

    /// <summary>The "nothing happened" result. Shared, so an idempotent rebuild allocates nothing.</summary>
    public static readonly LevelSetChange None = new(false, [], [], []);

    /// <summary>True when the rebuild changed nothing. The inverse of <see cref="Changed" />.</summary>
    public bool IsEmpty => !Changed;

    /// <summary>
    ///     Old id → surviving new id, for every level matched across the rebuild. Identity entries by
    ///     construction (a matched band <i>inherits</i> the old id — that is what "carried" means), kept
    ///     because the registry names this member and because a future non-identity carry would land
    ///     here rather than in a new type.
    /// </summary>
    public IReadOnlyDictionary<MapLevelId, MapLevelId> Remapped { get; init; } = _noRemap;

    /// <summary>The level set as it stands after the rebuild, lowest band first.</summary>
    public IReadOnlyList<MapLevel> LevelsAfter { get; init; } = [];

    /// <summary>The (id, lower Z) of every level as it stood before the rebuild, lowest band first.</summary>
    public IReadOnlyList<(MapLevelId Id, double ZMin)> LevelsBefore { get; init; } = [];

    /// <summary>
    ///     Rebases a <c>SpaceRef.World(LevelMinZ)</c> annotation anchor onto the new level set: the level
    ///     that inherited the old band's <b>identity</b> wins; else the level whose band owns the Z; else
    ///     the nearest band centre — which mirrors <see cref="FloorSplitter.SliceIndexFor" />'s own
    ///     fallback, so an anchor can never end up belonging to no level at all.
    ///     <para>
    ///         <b>Identity before geometry</b>, which inverts the B3 plan's step 8 (see plan deviation
    ///         13). Real band lists are <i>contiguous</i> — <c>FloorSplitter</c> emits slice N's
    ///         <c>MaxZ</c> as slice N+1's <c>MinZ</c>, and de_nuke's baked bundle publishes
    ///         <c>[-100000..-528]</c>/<c>[-528..100000]</c> — so an anchor stamped with a level's
    ///         <c>ZMin</c> sits exactly on a shared boundary, which <see cref="MapLevel.Contains" />
    ///         answers true for on <i>both</i> sides. Letting containment win therefore sank every
    ///         upper-floor anchor onto the floor below on the first rebuild that moved the boundary —
    ///         and moving that boundary is precisely what the histogram does all demo long.
    ///     </para>
    /// </summary>
    /// <param name="oldLevelMinZ">The anchor's stored level lower Z.</param>
    /// <param name="newLevelMinZ">The lower Z to store instead.</param>
    /// <returns>False only when the space has no levels at all (nothing to rebase onto).</returns>
    public bool TryRemapAnchor(double oldLevelMinZ, out double newLevelMinZ)
    {
        newLevelMinZ = oldLevelMinZ;

        IReadOnlyList<MapLevel> after = LevelsAfter;
        if (after.Count == 0)
        {
            return false;
        }

        // a. The anchor names a band that existed before this rebuild. If that band's level survived —
        //    which overlap-carry makes the common case — its identity IS the answer, wherever the
        //    boundary drifted to.
        for (int i = 0; i < LevelsBefore.Count; i++)
        {
            (MapLevelId id, double zMin) = LevelsBefore[i];
            if (Math.Abs(zMin - oldLevelMinZ) > 1e-3)
            {
                continue;
            }

            for (int j = 0; j < after.Count; j++)
            {
                if (after[j].Id == id)
                {
                    newLevelMinZ = after[j].ZMin;
                    return true;
                }
            }
        }

        // b. Otherwise the level whose band owns the Z. Half-open, so a value sitting on a shared
        //    boundary belongs to the band ABOVE it — an anchor is a band's LOWER bound, never its top.
        for (int i = 0; i < after.Count; i++)
        {
            if (oldLevelMinZ >= after[i].ZMin && oldLevelMinZ < after[i].ZMax)
            {
                newLevelMinZ = after[i].ZMin;
                return true;
            }
        }

        // b'. The very top of the highest band has no band above it to belong to.
        for (int i = 0; i < after.Count; i++)
        {
            if (after[i].Contains(oldLevelMinZ))
            {
                newLevelMinZ = after[i].ZMin;
                return true;
            }
        }

        // c. Nearest band centre.
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < after.Count; i++)
        {
            double d = Math.Abs(after[i].MidZ - oldLevelMinZ);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        newLevelMinZ = after[nearest].ZMin;
        return true;
    }
}
