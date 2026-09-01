#region

using System.Globalization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.DemoTrimmer;

/// <summary>Verdict for one emitted candidate.</summary>
internal sealed class VerificationReport
{
    /// <summary>Assertions that failed. A non-empty list means the candidate is not trustworthy.</summary>
    public List<string> Failures { get; } = [];

    /// <summary>Observations that are informational, not pass/fail.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Checks that passed, for the record.</summary>
    public List<string> Passed { get; } = [];

    public bool Ok => Failures.Count == 0;

    public void Check(bool condition, string label, string detail = "")
    {
        if (condition)
        {
            Passed.Add(label);
        }
        else
        {
            Failures.Add(detail.Length == 0 ? label : $"{label} — {detail}");
        }
    }
}

/// <summary>
///     Verifies that an emitted candidate is semantically the same window of the source.
///     <para>
///         <b>The entity comparison is deliberately three-way</b>, because comparing a
///         from-frame-0 source replay against a checkpoint-entry trim would fail for a
///         <em>correct</em> trim (the trim legitimately never saw the pre-entry packets):
///         <list type="bullet">
///             <item>
///                 <b>D0</b>: source replayed from frame 0. Informational only.
///             </item>
///             <item>
///                 <b>D1</b>: source frames replayed in exactly the retained order (setup + window).
///                 This is the reference the trim must reproduce.
///             </item>
///             <item>
///                 <b>D2</b>: the emitted file re-parsed and replayed from its own frame 0.
///             </item>
///         </list>
///         <c>D2 == D1</c> is the trim-fidelity assertion. <c>D1 == D0</c> is a separate claim about how
///         completely a <c>DEM_FullPacket</c> checkpoint restores state. If it fails, that is a property
///         of checkpoint entry, not a trimmer defect, so it is reported as a note.
///     </para>
/// </summary>
internal static class TrimVerifier
{
    /// <summary>
    ///     Verifies <paramref name="result" /> against <paramref name="source" />.
    ///     <paramref name="trimmed" /> must be the re-parsed emitted file.
    /// </summary>
    public static VerificationReport Verify(
        ParsedDemo source, byte[] sourceRaw, TrimResult result,
        ParsedDemo trimmed, byte[] trimmedRaw, bool includeFromZeroBaseline)
    {
        VerificationReport report = new();

        VerifyContainer(source, sourceRaw, result, trimmed, trimmedRaw, report);
        VerifyMetadata(source, trimmed, report);
        VerifyEncoderIdentity(result, report);
        VerifyGameEvents(source, result, trimmed, report);
        VerifyTourContent(trimmed, result, report);
        VerifyTeams(result, trimmed, report);
        VerifyEntities(source, result, trimmed, report, includeFromZeroBaseline);

        return report;
    }

    /// <summary>
    ///     The synthesized player_team seatings must survive the round trip: the re-parsed file's
    ///     <c>PlayerInfo.Team</c> post-pass is fed exclusively by <c>player_team</c> events, so this is
    ///     the check that the trim does not render every player on one team. When the window itself
    ///     contains genuine <c>player_team</c> events (a trim crossing halftime), the parser's
    ///     last-event-wins rule may legitimately override a synthesized seating. The per-slot equality
    ///     check is scoped to the synthesized-only case.
    /// </summary>
    private static void VerifyTeams(TrimResult result, ParsedDemo trimmed, VerificationReport report)
    {
        if (result.SynthesizedTeams.Count == 0)
        {
            report.Notes.Add("teams: nothing synthesized (no seated controllers sampled) — skipping");
            return;
        }

        int eventCount = trimmed.AllGameEvents.Count(e => e.Name == "player_team");
        report.Check(eventCount >= result.SynthesizedTeams.Count,
            "teams: synthesized player_team events decoded by the parser",
            string.Create(CultureInfo.InvariantCulture,
                $"expected >= {result.SynthesizedTeams.Count}, got {eventCount}"));

        int unassigned = 0, mismatched = 0;
        bool synthesizedOnly = eventCount == result.SynthesizedTeams.Count;
        foreach (TeamSample sample in result.SynthesizedTeams)
        {
            if (!trimmed.Players.TryGetValue(sample.Slot, out PlayerInfo? info) || info.Team is not (2 or 3))
            {
                unassigned++;
            }
            else if (synthesizedOnly && info.Team != sample.Team)
            {
                mismatched++;
            }
        }

        report.Check(unassigned == 0,
            "teams: every sampled player carries a team in the re-parsed file",
            string.Create(CultureInfo.InvariantCulture, $"{unassigned} of {result.SynthesizedTeams.Count} unassigned"));
        report.Check(mismatched == 0,
            "teams: re-parsed teams match the sampled seating",
            string.Create(CultureInfo.InvariantCulture, $"{mismatched} mismatched"));

        int t = result.SynthesizedTeams.Count(s => s.Team == 2);
        int ct = result.SynthesizedTeams.Count(s => s.Team == 3);
        report.Notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"teams: {ct} CT / {t} T synthesized at the window's first freeze-end"));
    }

    /// <summary>
    ///     Checks the parts of the container <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" />
    ///     never reads: the 16-byte file header's two frame offsets, and the three frames that live
    ///     past <c>DEM_Stop</c>.
    ///     <para>
    ///         Nothing else in this class can see them: the parse loop starts at byte 16 and stops at
    ///         <c>DEM_Stop</c>, so without this check a file with dangling offsets and no tail would pass
    ///         every other assertion while being exactly the shape most likely to be rejected outright by
    ///         the real CS2 client.
    ///     </para>
    /// </summary>
    private static void VerifyContainer(
        ParsedDemo source, byte[] sourceRaw, TrimResult result,
        ParsedDemo trimmed, byte[] trimmedRaw, VerificationReport report)
    {
        DemoTail sourceTail = DemoTail.Read(sourceRaw, source);
        DemoTail trimmedTail = DemoTail.Read(trimmedRaw, trimmed);

        // DemoTail resolves each offset AND validates the command id of the frame it lands on, so a
        // non-null result proves the patched-back header offset points at the right frame.
        report.Check(sourceTail.Stop is null == trimmedTail.Stop is null,
            "container: DEM_Stop terminator present (matching the source)",
            string.Create(CultureInfo.InvariantCulture,
                $"source={Describe(sourceTail.Stop)} trimmed={Describe(trimmedTail.Stop)}"));
        report.Check(sourceTail.SpawnGroups is null == trimmedTail.SpawnGroups is null,
            "container: file header bytes 12-15 resolve to DEM_SpawnGroups",
            string.Create(CultureInfo.InvariantCulture,
                $"source={Describe(sourceTail.SpawnGroups)} trimmed={Describe(trimmedTail.SpawnGroups)}"));
        report.Check(sourceTail.FileInfo is null == trimmedTail.FileInfo is null,
            "container: file header bytes 8-11 resolve to DEM_FileInfo",
            string.Create(CultureInfo.InvariantCulture,
                $"source={Describe(sourceTail.FileInfo)} trimmed={Describe(trimmedTail.FileInfo)}"));

        if (trimmedTail.FileInfo is not { } fileInfoFrame)
        {
            return;
        }

        CDemoFileInfo info = CDemoFileInfo.Parser.ParseFrom(DemoTail.Payload(trimmedRaw, fileInfoFrame));
        report.Check(info.PlaybackTicks == result.Window.EndTick,
            "container: rewritten DEM_FileInfo.playback_ticks describes the trimmed window",
            string.Create(CultureInfo.InvariantCulture,
                $"expected {result.Window.EndTick}, got {info.PlaybackTicks}"));
        report.Check(info.GameInfo?.Cs is not { } cs || cs.RoundStartTicks.All(t => t <= result.Window.EndTick),
            "container: DEM_FileInfo.round_start_ticks confined to the retained window");

        if (result.LeftUncompressed > 0)
        {
            report.Notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{result.LeftUncompressed} rewritten frame(s) were written UNCOMPRESSED (stripped payload did not shrink under Snappy) — the output mixes compressed and uncompressed frames"));
        }
    }

    private static string Describe(RawFrame? frame) => frame?.ToString() ?? "(absent)";

    private static void VerifyMetadata(ParsedDemo source, ParsedDemo trimmed, VerificationReport report)
    {
        Compare(report, "map", source.MapName, trimmed.MapName);
        Compare(report, "tickInterval", source.TickInterval, trimmed.TickInterval);
        Compare(report, "serverName", source.ServerName, trimmed.ServerName);
        Compare(report, "clientName", source.ClientName, trimmed.ClientName);
        Compare(report, "gameDirectory", source.GameDirectory, trimmed.GameDirectory);
        Compare(report, "buildNumber", source.BuildNumber, trimmed.BuildNumber);
        Compare(report, "patchVersion", source.PatchVersion, trimmed.PatchVersion);
        Compare(report, "demoVersionName", source.DemoVersionName, trimmed.DemoVersionName);
        Compare(report, "demoVersionGuid", source.DemoVersionGuid, trimmed.DemoVersionGuid);
        Compare(report, "serverStartTick", source.ServerStartTick, trimmed.ServerStartTick);
        Compare(report, "addons", source.Addons, trimmed.Addons);
        Compare(report, "schemaPresent", source.Schema is not null, trimmed.Schema is not null);
    }

    private static void VerifyEncoderIdentity(TrimResult result, VerificationReport report)
    {
        if (!result.Variant.RewritesPayloads)
        {
            return;
        }

        int checkedPackets = result.IdentityExact + result.IdentityShorter + result.IdentityMismatch;
        if (checkedPackets == 0)
        {
            report.Notes.Add("encoder identity: not run");
            return;
        }

        report.Check(result.IdentityMismatch == 0, "encoder identity (re-encode == original)",
            string.Create(CultureInfo.InvariantCulture,
                $"{result.IdentityMismatch}/{checkedPackets} packets diverged " +
                $"(first at source frame {result.IdentityFirstDivergentFrame})"));

        if (result.IdentityShorter > 0)
        {
            report.Notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"encoder identity: {result.IdentityShorter}/{checkedPackets} packets matched as a prefix " +
                $"with the original carrying extra trailing padding bytes (benign)"));
        }
    }

    private static void VerifyGameEvents(
        ParsedDemo source, TrimResult result, ParsedDemo trimmed, VerificationReport report)
    {
        HashSet<int> retained = [.. result.EmittedSourceFrames];
        List<string> expected = source.AllGameEvents
            .Where(e => retained.Contains(e.FrameNumber))
            .Select(Signature).ToList();
        // The synthesized player_team seatings are additive by design and have no source-frame
        // counterpart. Exclude them here so this check keeps pinning the RETAINED stream
        // one-to-one (VerifyTeams owns the synthesized events).
        List<string> actual = trimmed.AllGameEvents
            .Where(e => !(result.SynthesizedTeams.Count > 0
                          && e.Payload is PlayerTeamEvent { OldTeam: 0, Silent: true }
                          && e.Name == "player_team"))
            .Select(Signature).ToList();

        if (expected.Count != actual.Count)
        {
            report.Failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"game-event count: expected {expected.Count} from the retained frames, got {actual.Count}"));
            return;
        }

        int firstDiff = -1;
        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                firstDiff = i;
                break;
            }
        }

        report.Check(firstDiff < 0,
            string.Create(CultureInfo.InvariantCulture, $"game-event stream ({expected.Count} events)"),
            firstDiff < 0 ? "" : $"first difference at #{firstDiff}: expected '{expected[firstDiff]}', got '{actual[firstDiff]}'");
    }

    /// <summary>
    ///     The artifact exists to drive the first-run tour's Stats and 2D-playback steps. A window whose
    ///     "rounds" are warmup parses perfectly and is still useless, so assert on retained content.
    /// </summary>
    private static void VerifyTourContent(ParsedDemo trimmed, TrimResult result, VerificationReport report)
    {
        int kills = trimmed.AllGameEvents.Count(e => string.Equals(e.Name, "player_death", StringComparison.Ordinal));
        int boundaries = trimmed.AllGameEvents.Count(e =>
            string.Equals(e.Name, WindowSelector.DefaultBoundaryEvent, StringComparison.Ordinal));

        report.Check(kills > 0, "retained window contains kills (usable for the tour)",
            "0 player_death events — the window is probably warmup");
        report.Check(boundaries >= result.Window.RoundsKept,
            string.Create(CultureInfo.InvariantCulture, $"retained round boundaries >= {result.Window.RoundsKept}"),
            string.Create(CultureInfo.InvariantCulture, $"only {boundaries} '{WindowSelector.DefaultBoundaryEvent}' events"));
        report.Notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"retained content: {kills} kills, {boundaries} round boundaries, {trimmed.Players.Count} players"));
    }

    private static void VerifyEntities(
        ParsedDemo source, TrimResult result, ParsedDemo trimmed,
        VerificationReport report, bool includeFromZeroBaseline)
    {
        IReadOnlyList<int> sampleTicks = SampleTicks(result.Window);

        // A checkpoint-entry file can only be decoded by a reader that treats its first DEM_FullPacket as
        // a state restore; a contiguous file is decoded by a plain sequential read.
        ReplayPolicy policy = result.Window.EnteredAtCheckpoint
            ? ReplayPolicy.CheckpointEntry
            : ReplayPolicy.Sequential;
        report.Notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"entity sample ticks: {string.Join(", ", sampleTicks)}; replay policy: {policy}"));

        // D1: source frames in exactly the retained order. Enumerated lazily so only one tracker's worth
        // of entity state is live at a time (16 GB machine; the source ParsedDemo is already resident).
        ReplayOutcome d1 = EntityDigestBuilder.Replay(
            result.EmittedSourceFrames.Select(i => source.Frames[i]), sampleTicks, policy);

        // D2: the emitted file, re-parsed, replayed from its own frame 0 under the same policy.
        ReplayOutcome d2 = EntityDigestBuilder.Replay(trimmed.Frames, sampleTicks, policy);

        CompareDigests(report, "entity state: trimmed file == retained source frames (D2 == D1)",
            d1.Digests, d2.Digests, true);

        foreach (EntityDigest digest in d2.Digests)
        {
            report.Notes.Add("  trimmed " + digest);
            if (digest.LastError is not null)
            {
                report.Failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"entity decode error at tick {digest.Tick} under {policy} replay: {Summarize(digest.LastError)}"));
            }
        }

        if (policy == ReplayPolicy.CheckpointEntry && !d2.InstanceBaselinesSeeded)
        {
            // Not a failure while the retained setup prefix (DEM_StringTables + the signon run) still
            // supplies the baselines, which it does whenever the entry checkpoint is early. It becomes
            // load-bearing for a genuinely mid-match entry: the full-packet string-table dump is
            // incremental, so a later checkpoint may omit instancebaseline entirely.
            report.Notes.Add(
                "entry DEM_FullPacket carries NO instancebaseline snapshot — the retained setup prefix "
                + "is supplying the per-class baselines instead");
        }

        VerifyNaiveSequentialReadability(trimmed, sampleTicks, policy, report);

        if (includeFromZeroBaseline)
        {
            ReplayOutcome d0 = EntityDigestBuilder.Replay(
                source.Frames.Take(result.Window.EndIndex + 1), sampleTicks, ReplayPolicy.Sequential);

            // For a contiguous trim the retained frames ARE source frames 0..EndIndex (minus frame types
            // the tracker ignores), so a difference is a bookkeeping bug and must fail. For a
            // checkpoint-entry trim a difference is a property of checkpoint entry, not of the trimmer.
            CompareDigests(report, "entity state: retained source frames == full source replay (D1 == D0)",
                d0.Digests, d1.Digests, !result.Window.EnteredAtCheckpoint);
        }
    }

    /// <summary>
    ///     Can DemoViewer.NET's own load path read this file as-is? That path is a plain sequential
    ///     replay, so a checkpoint-entry trim fails it. That is the tour's actual requirement, and the reason
    ///     the contiguous variant matters.
    /// </summary>
    private static void VerifyNaiveSequentialReadability(
        ParsedDemo trimmed, IReadOnlyList<int> sampleTicks, ReplayPolicy policy, VerificationReport report)
    {
        ReplayOutcome sequential = policy == ReplayPolicy.Sequential
            ? default
            : EntityDigestBuilder.Replay(trimmed.Frames, sampleTicks, ReplayPolicy.Sequential);

        IReadOnlyList<EntityDigest> digests = policy == ReplayPolicy.Sequential
            ? []
            : sequential.Digests;

        if (policy == ReplayPolicy.Sequential)
        {
            report.Passed.Add("naive sequential replay (DemoViewer.NET's own load path) is the verified path");
            return;
        }

        EntityDigest last = digests.Count > 0 ? digests[^1] : default;
        bool clean = digests.Count > 0 && digests.All(d => d.LastError is null);
        report.Notes.Add(clean
            ? "naive sequential replay (DemoViewer.NET's own load path): CLEAN"
            : string.Create(CultureInfo.InvariantCulture,
                $"naive sequential replay (DemoViewer.NET's own load path): BROKEN — " +
                $"{last.DeltaUnknownCount} unknown deltas, {Summarize(last.LastError)}"));
    }

    private static string Summarize(string? error) =>
        error is null ? "(none)" : error.Split('\n')[0];

    private static void CompareDigests(
        VerificationReport report, string label,
        IReadOnlyList<EntityDigest> reference, IReadOnlyList<EntityDigest> candidate, bool isFailure)
    {
        if (reference.Count != candidate.Count)
        {
            Record(report, isFailure, label,
                string.Create(CultureInfo.InvariantCulture,
                    $"sample count {candidate.Count} != {reference.Count}"));
            return;
        }

        List<string> diffs = [];
        for (int i = 0; i < reference.Count; i++)
        {
            if (reference[i].Hash != candidate[i].Hash)
            {
                diffs.Add(string.Create(CultureInfo.InvariantCulture,
                    $"tick {reference[i].Tick}: [{reference[i]}] vs [{candidate[i]}]"));
            }
        }

        if (diffs.Count == 0)
        {
            report.Passed.Add(string.Create(CultureInfo.InvariantCulture,
                $"{label} — {reference.Count} sample tick(s) identical"));
            return;
        }

        Record(report, isFailure, label, string.Join(" | ", diffs));
    }

    private static void Record(VerificationReport report, bool isFailure, string label, string detail)
    {
        if (isFailure)
        {
            report.Failures.Add($"{label} — {detail}");
        }
        else
        {
            report.Notes.Add($"[informational] {label} DIFFERS — {detail}");
        }
    }

    /// <summary>
    ///     One sample per round boundary plus the window end. Sampling only at the end would let a
    ///     mid-window desync that a later <c>DEM_FullPacket</c> heals slip through unnoticed.
    /// </summary>
    private static IReadOnlyList<int> SampleTicks(TrimWindow window)
    {
        SortedSet<int> ticks = [];
        foreach (int t in window.BoundaryTicks)
        {
            if (t > window.StartTick && t <= window.EndTick)
            {
                ticks.Add(t);
            }
        }

        ticks.Add(window.EndTick);
        return [.. ticks];
    }

    private static string Signature(GameEvent e) => string.Create(CultureInfo.InvariantCulture,
        $"{e.Name}@{e.GameTick}/{e.ServerTick}#{e.EventId}:" +
        $"{string.Join(",", e.GetDecodedFields().Select(f => $"{f.Name}={f.Value}"))}");

    private static void Compare<T>(VerificationReport report, string field, T expected, T actual) =>
        report.Check(EqualityComparer<T>.Default.Equals(expected, actual),
            $"metadata.{field}",
            string.Create(CultureInfo.InvariantCulture, $"expected '{expected}', got '{actual}'"));
}
