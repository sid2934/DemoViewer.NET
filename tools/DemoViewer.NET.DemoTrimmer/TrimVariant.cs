#region

using System.Collections.Frozen;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>
///     One rung of the trim ladder. The rungs exist so the boundary between "still plays in the real
///     CS2 client" and "broken" can be found empirically: each successive variant removes exactly one
///     more class of data, so a failure isolates to the thing that rung added.
/// </summary>
/// <param name="Id">Short id used in the emitted filename, e.g. <c>v1-verbatim</c>.</param>
/// <param name="Description">One-line description for the report / console.</param>
/// <param name="EnterAtCheckpoint">
///     <c>false</c> keeps the stream contiguous from frame 0 (no mid-stream entry at all);
///     <c>true</c> skips the pre-round-1 stream and enters at a <c>DEM_FullPacket</c> checkpoint.
/// </param>
/// <param name="DroppedFrameCommands">Whole frame commands removed from the retained window.</param>
/// <param name="StrippedInnerTypeIds">Net-message type ids removed from inside each packet payload.</param>
internal sealed record TrimVariant(
    string Id,
    string Description,
    bool EnterAtCheckpoint,
    FrozenSet<string> DroppedFrameCommands,
    FrozenSet<int> StrippedInnerTypeIds)
{
    private static readonly FrozenSet<string> NoFrames = FrozenSet<string>.Empty;
    private static readonly FrozenSet<int> NoInner = FrozenSet<int>.Empty;

    /// <summary>
    ///     Animation frames: pure client-side animation replay data. DemoViewer.NET reads none of it,
    ///     and it is ~12% of an AnimGraph2 pro demo. Header and data are dropped together. The header
    ///     only exists to interpret the data.
    /// </summary>
    private static readonly FrozenSet<string> AnimationFrames =
        new[]
        {
            "DEM_AnimationData", "DEM_AnimationHeader"
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<int> UserCmds = new[]
    {
        PacketRewriter.SvcUserCmdsTypeId
    }.ToFrozenSet();

    /// <summary>
    ///     V0: no mid-stream entry. Everything from frame 0 through the end of round N, verbatim.
    ///     Isolates "truncating the tail" from "entering at a checkpoint"; the single most likely
    ///     candidate to survive CS2 playback, at the cost of carrying the warmup.
    /// </summary>
    public static readonly TrimVariant V0 = new(
        "v0-contiguous",
        "frame 0 → end of round N, verbatim (no mid-stream entry)",
        false, NoFrames, NoInner);

    /// <summary>V1: setup frames + entry at the <c>DEM_FullPacket</c> before round 1, then verbatim.</summary>
    public static readonly TrimVariant V1 = new(
        "v1-verbatim",
        "setup + DEM_FullPacket entry → end of round N, verbatim",
        true, NoFrames, NoInner);

    /// <summary>V2: V1 minus whole animation frames. Still no payload rewriting.</summary>
    public static readonly TrimVariant V2 = new(
        "v2-no-anim",
        "V1 minus DEM_AnimationData / DEM_AnimationHeader frames",
        true, AnimationFrames, NoInner);

    /// <summary>V3: V2 plus <c>svc_UserCmds</c> stripped from inside every packet. Expected to break CS2.</summary>
    public static readonly TrimVariant V3 = new(
        "v3-no-usercmds",
        "V2 plus svc_UserCmds stripped from inside every DEM_Packet / DEM_FullPacket",
        true, AnimationFrames, UserCmds);

    /// <summary>
    ///     V2C: V2's message removal without the mid-stream entry.
    ///     <para>
    ///         Measured: checkpoint entry saves ~0.03 MiB on both reference demos (their first
    ///         <c>DEM_FullPacket</c> is at tick 1, so "the checkpoint before round 1" IS the demo start),
    ///         while it makes the file undecodable by any sequential reader, including DemoViewer.NET's
    ///         own load path, which skips a <c>DEM_FullPacket</c>'s entities as redundant. Separating the
    ///         two axes gives a candidate with V2's savings and V0's readability.
    ///     </para>
    /// </summary>
    public static readonly TrimVariant V2C = new(
        "v2c-no-anim-contiguous",
        "V2's frame drops, but contiguous from frame 0 (no mid-stream entry)",
        false, AnimationFrames, NoInner);

    /// <summary>V3C: V3's <c>svc_UserCmds</c> strip without the mid-stream entry. The smallest readable candidate.</summary>
    public static readonly TrimVariant V3C = new(
        "v3c-no-usercmds-contiguous",
        "V3's svc_UserCmds strip, but contiguous from frame 0 (no mid-stream entry)",
        false, AnimationFrames, UserCmds);

    /// <summary>True when this variant has to decode, edit and re-serialize packet payloads.</summary>
    public bool RewritesPayloads => StrippedInnerTypeIds.Count > 0;

    /// <summary>The ladder in increasing-risk order.</summary>
    public static IReadOnlyList<TrimVariant> All { get; } = [V0, V1, V2, V3, V2C, V3C];

    /// <summary>Resolves a comma-separated id list (<c>v0,v1</c>) against <see cref="All" />.</summary>
    public static IReadOnlyList<TrimVariant> Parse(string spec)
    {
        List<TrimVariant> chosen = [];
        foreach (string token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            TrimVariant? match = All.FirstOrDefault(v =>
                v.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
                || v.Id.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase));
            chosen.Add(match ?? throw new ArgumentException($"Unknown variant '{token}'.", nameof(spec)));
        }

        return chosen;
    }
}
