#region

using System.Globalization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>A stable fingerprint of a full <see cref="EntityTracker" /> state at one sample tick.</summary>
/// <param name="Tick">Sample tick the digest was taken at.</param>
/// <param name="Hash">FNV-1a 64 over every live entity's index, class, serial, PVS flag and sorted fields.</param>
/// <param name="EntityCount">Live entities.</param>
/// <param name="FieldCount">Total received fields across all live entities.</param>
/// <param name="DeltaUnknownCount">Tracker's running unknown-delta counter — spikes on desync.</param>
/// <param name="PacketCount">Packets the tracker consumed.</param>
/// <param name="LastError">Tracker's last decode error, if any.</param>
internal readonly record struct EntityDigest(
    int Tick, ulong Hash, int EntityCount, long FieldCount,
    int DeltaUnknownCount, int PacketCount, string? LastError)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"tick={Tick} hash={Hash:x16} ents={EntityCount} fields={FieldCount} " +
        $"unknownDeltas={DeltaUnknownCount} packets={PacketCount}{(LastError is null ? "" : " err=" + LastError)}");
}

/// <summary>How a frame sequence is fed to the tracker.</summary>
internal enum ReplayPolicy
{
    /// <summary>
    ///     Plain <c>AdvanceOneFrame</c> for every frame — exactly what DemoViewer.NET's own demo load
    ///     does. Note that <c>EntityTracker.ProcessFrame</c> deliberately <b>skips</b> a
    ///     <c>DEM_FullPacket</c>'s <c>svc_PacketEntities</c> here, because in an untrimmed demo that
    ///     snapshot is redundant with the delta stream that already built the state.
    /// </summary>
    Sequential,

    /// <summary>
    ///     Treat the first <c>DEM_FullPacket</c> as a real state restore
    ///     (<c>ResetEntitiesKeepSchema</c> + <c>LoadInstanceBaselineSnapshot</c> +
    ///     <c>ProcessFullPacketCheckpoint</c>), then continue sequentially. This is what a consumer
    ///     entering the stream mid-match must do — and what <see cref="ReplayPolicy.Sequential" />
    ///     cannot do, which is why a checkpoint-entry trim is not decodable by a naive reader.
    /// </summary>
    CheckpointEntry
}

/// <summary>Result of one tracked replay.</summary>
/// <param name="Digests">Fingerprints at the requested sample ticks.</param>
/// <param name="InstanceBaselinesSeeded">
///     Whether the entry checkpoint actually carried an <c>instancebaseline</c> snapshot. The
///     full-packet string-table dump is incremental, so a checkpoint that omits it cannot seed
///     per-class baselines on its own.
/// </param>
internal readonly record struct ReplayOutcome(IReadOnlyList<EntityDigest> Digests, bool InstanceBaselinesSeeded);

/// <summary>
///     Replays a frame sequence through an <see cref="EntityTracker" /> and fingerprints the entity
///     state at chosen ticks.
///     <para>
///         <b>Why hand-rolled hashing:</b> <c>System.HashCode</c> is per-process randomized, so it is a
///         useless oracle across processes (learned the hard way in the v0.5.1 perf sweep). FNV-1a over a canonical
///         text form is stable everywhere, and floats are canonicalized through their bit pattern rather
///         than through formatting.
///     </para>
/// </summary>
internal static class EntityDigestBuilder
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    ///     Replays <paramref name="frames" /> in order, taking a digest as soon as the replay passes each
    ///     tick in <paramref name="sampleTicks" /> (and once more at the end for any that were never
    ///     passed). Sampling by tick — not by frame index — is what makes a source-frame replay and a
    ///     trimmed-file replay comparable, since the two have different frame numbering.
    /// </summary>
    public static ReplayOutcome Replay(
        IEnumerable<DemoFrame> frames, IReadOnlyList<int> sampleTicks, ReplayPolicy policy)
    {
        EntityTracker tracker = new();
        List<EntityDigest> digests = [];
        Queue<int> pending = new(sampleTicks.Order());
        bool checkpointPending = policy == ReplayPolicy.CheckpointEntry;
        bool baselinesSeeded = false;

        foreach (DemoFrame frame in frames)
        {
            while (pending.Count > 0 && frame.ServerTick > pending.Peek())
            {
                digests.Add(Compute(tracker, pending.Dequeue()));
            }

            if (checkpointPending && string.Equals(frame.Command, "DEM_FullPacket", StringComparison.Ordinal))
            {
                // Drop whatever the signon run created, seed the per-class baselines from the
                // checkpoint's own string-table snapshot, then apply its full ENTERPVS snapshot.
                tracker.ResetEntitiesKeepSchema();
                baselinesSeeded = tracker.LoadInstanceBaselineSnapshot(frame);
                tracker.ProcessFullPacketCheckpoint(frame);
                checkpointPending = false;
                continue;
            }

            tracker.AdvanceOneFrame(frame);
        }

        while (pending.Count > 0)
        {
            digests.Add(Compute(tracker, pending.Dequeue()));
        }

        return new ReplayOutcome(digests, baselinesSeeded);
    }

    private static EntityDigest Compute(EntityTracker tracker, int tick)
    {
        ulong hash = FnvOffsetBasis;
        int entityCount = 0;
        long fieldCount = 0;

        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed().OrderBy(t => t.Index))
        {
            entityCount++;
            MixText(ref hash, index.ToString(CultureInfo.InvariantCulture));
            MixText(ref hash, entity.ClassName);
            MixText(ref hash, entity.Serial.ToString(CultureInfo.InvariantCulture));
            MixText(ref hash, entity.IsInPvs ? "1" : "0");

            foreach (KeyValuePair<string, object?> field in
                     entity.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                fieldCount++;
                MixText(ref hash, field.Key);
                MixText(ref hash, Canonicalize(field.Value));
            }
        }

        return new EntityDigest(tick, hash, entityCount, fieldCount,
            tracker.DeltaUnknownCount, tracker.PacketCount, tracker.LastEntityError);
    }

    /// <summary>
    ///     Canonical text for a decoded field value. Floats and doubles go through their raw bit pattern
    ///     so no formatting rounding can mask (or invent) a difference.
    /// </summary>
    private static string Canonicalize(object? value) => value switch
    {
        null => "~null",
        float f => "f" + BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture),
        double d => "d" + BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture),
        bool b => b ? "T" : "F",
        string s => "s" + s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static void MixText(ref ulong hash, string text)
    {
        foreach (char c in text)
        {
            hash = (hash ^ (byte)(c & 0xFF)) * FnvPrime;
            hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
        }

        hash = (hash ^ 0xFFu) * FnvPrime; // field separator — keeps "ab"+"c" distinct from "a"+"bc"
    }
}
