namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     A level's stable identity, minted from its quantized lower Z (<see cref="MapSpace.QuantizeZ" />)
///     and then <b>carried</b> across rebuilds by band overlap — never re-derived from Z after minting.
///     <para>
///         <b>Never a level INDEX.</b> Design risk 5 is the confusion between "the third band from the
///         bottom" and "the band that is Nuke's lower floor": insert a basement and every index shifts
///         while every identity holds. Panes, cameras, picture caches and (from B2) annotations are all
///         keyed on this, so making it a distinct struct means the compiler rejects the mix-up that
///         would otherwise silently repaint one floor with another's camera.
///     </para>
///     <para>
///         The minting rule is only ever consulted for a band that could <i>not</i> be matched to an
///         existing level. A boundary that drifts by a bucket keeps its identity, because a drifting
///         band still overlaps the one it came from — see <see cref="MapSpace.Rebuild" />.
///     </para>
/// </summary>
/// <param name="Key">The quantized lower Z. Opaque — compare it, never do arithmetic on it.</param>
public readonly record struct MapLevelId(int Key)
{
    /// <summary>The "no level" sentinel. Distinct from every real id, including a level at Z 0.</summary>
    public static MapLevelId None => new(int.MinValue);

    /// <summary>Whether this is <see cref="None" />.</summary>
    public bool IsNone => Key == int.MinValue;

    /// <inheritdoc />
    public override string ToString() => IsNone ? "none" : $"lv{Key}";
}
