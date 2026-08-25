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
    ///     Rebases a <c>SpaceRef.World(LevelMinZ)</c> annotation anchor onto the new level set, by the
    ///     four rules in the B3 plan's remap algorithm (step 8): the containing level wins; else the
    ///     level that inherited the old band's identity; else the nearest band centre — which mirrors
    ///     <see cref="FloorSplitter.SliceIndexFor" />'s own fallback, so an anchor can never end up
    ///     belonging to no level at all.
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

        // a. A level that contains the old anchor Z keeps it where the user put it.
        for (int i = 0; i < after.Count; i++)
        {
            if (after[i].Contains(oldLevelMinZ))
            {
                newLevelMinZ = after[i].ZMin;
                return true;
            }
        }

        // b. The band that WAS at this Z survived under its own identity — follow it.
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
