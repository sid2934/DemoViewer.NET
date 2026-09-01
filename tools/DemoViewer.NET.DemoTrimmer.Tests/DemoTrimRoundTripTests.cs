#region

using System.Globalization;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using Google.Protobuf;

#endregion

namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     End-to-end round trip: trim a real demo, re-parse the emitted file, and prove it is semantically
///     the same window of the source (metadata, game-event stream, and the decoded
///     entity stream via <c>EntityTracker</c>, which is the real test).
///     <para>
///         <b>Not parallel, one heavy parse at a time.</b> The source demo is 170-450 MB and each test
///         additionally replays two <c>EntityTracker</c>s; running these concurrently gets the process
///         OS-killed on a 16 GB machine.
///     </para>
///     <para>
///         These tests <b>cannot</b> assert CS2 playability. That is only answerable by loading a
///         candidate in the game. See <c>the design notes in git history</c> for the manual protocol.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public sealed class DemoTrimRoundTripTests
{
    /// <summary>Round count kept small so a test run stays within a few seconds of replay per variant.</summary>
    private const int Rounds = 1;

    private static readonly Lock SourceLock = new();
    private static (byte[] Raw, ParsedDemo Demo)? _source;

    /// <summary>
    ///     Parses the reference demo once for the whole class, keeping the raw bytes alongside (the
    ///     shared <c>DemoTestHelper.GetOrParse</c> cache does not expose them, and the trimmer needs
    ///     them to copy frames verbatim).
    /// </summary>
    private static (byte[] Raw, ParsedDemo Demo) Source()
    {
        string path = DemoTestHelper.RequireDemo();
        lock (SourceLock)
        {
            if (_source is null)
            {
                byte[] raw = File.ReadAllBytes(path);
                _source = (raw, DemoParser.Parse(raw.AsMemory()));
            }

            return _source.Value;
        }
    }

    /// <summary>
    ///     The cheapest strong test in the suite: V0's emitted frames are exactly source frames
    ///     0..EndIndex, so the from-frame-0 baseline (D1 == D0) must hold as well as D2 == D1.
    /// </summary>
    [Test]
    public async Task V0_Contiguous_ParsesAndMatchesTheSourceWindow() =>
        await RunVariant(TrimVariant.V0, true);

    /// <summary>The recommended shipping artifact: smallest candidate a sequential reader can consume.</summary>
    [Test]
    public async Task V3C_ContiguousWithUserCmdsStripped_ParsesAndMatchesTheSourceWindow() =>
        await RunVariant(TrimVariant.V3C, true);

    [Test]
    public async Task V1_VerbatimCheckpointEntry_ParsesAndMatchesTheSourceWindow() =>
        await RunVariant(TrimVariant.V1);

    [Test]
    public async Task V2_WithoutAnimationFrames_ParsesAndMatchesTheSourceWindow() =>
        await RunVariant(TrimVariant.V2);

    [Test]
    public async Task V3_WithUserCmdsStripped_ParsesAndMatchesTheSourceWindow() =>
        await RunVariant(TrimVariant.V3);

    [Test]
    public async Task V3_EncoderIsBitIdentityOnRealPacketsBeforeAnythingIsDropped()
    {
        (byte[] raw, ParsedDemo demo) = Source();
        TrimWindow window = WindowSelector.Select(demo, Rounds, true);

        // Sample real packets spread across the window rather than the first N, so an encoding branch
        // that only appears late (large type ids, long payloads) is still exercised.
        int step = Math.Max(1, (window.EndIndex - window.EntryIndex) / 400);
        int exact = 0, shorter = 0, mismatch = 0;
        for (int i = window.EntryIndex; i <= window.EndIndex; i += step)
        {
            DemoFrame frame = demo.Frames[i];
            if (frame.Command is not ("DEM_Packet" or "DEM_FullPacket"))
            {
                continue;
            }

            byte[] payload = DownstreamUtilities.GetDecompressedPayload(frame, raw);
            ByteString? data = string.Equals(frame.Command, "DEM_FullPacket", StringComparison.Ordinal)
                ? CDemoFullPacket.Parser.ParseFrom(payload).Packet?.Data
                : CDemoPacket.Parser.ParseFrom(payload).Data;
            if (data is not { Length: > 0 })
            {
                continue;
            }

            switch (PacketRewriter.CheckEncoderIdentity(data.Span, out _))
            {
                case IdentityOutcome.Exact: exact++; break;
                case IdentityOutcome.ExactPrefixShorter: shorter++; break;
                default: mismatch++; break;
            }
        }

        await Assert.That(exact + shorter).IsGreaterThan(0);
        await Assert.That(mismatch).IsEqualTo(0);
    }

    [Test]
    public async Task EmittedCandidatesShrinkMonotonicallyDownTheLadder()
    {
        (byte[] raw, ParsedDemo demo) = Source();
        string dir = CreateTempDir();
        try
        {
            long previous = long.MaxValue;
            foreach (TrimVariant variant in new[]
                     {
                         TrimVariant.V1, TrimVariant.V2, TrimVariant.V3
                     })
            {
                TrimWindow window = WindowSelector.Select(demo, Rounds, variant.EnterAtCheckpoint);
                TrimResult result = DemoTrimWriter.Write(
                    demo, raw, window, variant, Path.Combine(dir, variant.Id + ".dem"), false);

                await Assert.That(result.BytesWritten).IsLessThan(raw.LongLength);
                await Assert.That(result.BytesWritten).IsLessThanOrEqualTo(previous);
                previous = result.BytesWritten;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task RunVariant(TrimVariant variant, bool includeFromZeroBaseline = false)
    {
        (byte[] raw, ParsedDemo demo) = Source();
        TrimWindow window = WindowSelector.Select(demo, Rounds, variant.EnterAtCheckpoint);
        string dir = CreateTempDir();
        try
        {
            string outPath = Path.Combine(dir, $"{variant.Id}-{Rounds}r.dem");
            TrimResult result = DemoTrimWriter.Write(demo, raw, window, variant, outPath);

            // 1. It parses.
            byte[] trimmedRaw = File.ReadAllBytes(outPath);
            ParsedDemo trimmed = DemoParser.Parse(trimmedRaw.AsMemory());

            // 2. It is the same window of the source: container tail + header offsets (which the parser
            //    never reads), metadata, game events, and the decoded entity stream (D2 == D1). The
            //    from-frame-0 baseline (D1 == D0) is opt-in: it doubles the replay cost, and it is only a
            //    hard expectation for the contiguous variants.
            VerificationReport report = TrimVerifier.Verify(
                demo, raw, result, trimmed, trimmedRaw, includeFromZeroBaseline);

            await Assert.That(report.Failures)
                .IsEmpty()
                .Because(string.Create(CultureInfo.InvariantCulture,
                    $"{variant.Id} failed verification: {string.Join(" ;; ", report.Failures)}"));

            // 3. It is actually smaller.
            await Assert.That(result.BytesWritten).IsLessThan(raw.LongLength);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-trimmer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
