#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Export;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>
///     Which encoder one export will use, at what quality, and why: plan <c>P2-export-throughput</c> D5.
///     <para>
///         <b>A per-session value.</b> It is resolved by the caller, handed to
///         <c>FfmpegSinkOptions</c>, and lives exactly as long as that sink. Two exports in one process
///         may hold two different selections at once; nothing here is static, and nothing here is
///         mutable. That is what a future multi-export node needs from this phase (plan §7), and it is
///         the same argument that already keeps <c>GlobalFFOptions</c> out of the sink.
///     </para>
/// </summary>
/// <param name="Encoder">The rung that will encode.</param>
/// <param name="Quality">The requested quality; <see cref="VideoEncoder.ArgumentsFor" /> maps it.</param>
/// <param name="Reason">Why this rung: the sentence the CLI and the JSON both print.</param>
/// <param name="Attempts">Every rung that was probed, in ladder order, including the one that won.</param>
public sealed record EncoderSelection(
    VideoEncoder Encoder,
    ExportQuality Quality,
    string Reason,
    IReadOnlyList<EncoderProbeResult> Attempts)
{
    /// <summary>True when the encode runs on dedicated silicon rather than on the renderer's cores.</summary>
    public bool IsHardware => Encoder.IsHardware;

    /// <summary>The arguments this selection contributes to the ffmpeg command line.</summary>
    public string Arguments => Encoder.ArgumentsFor(Quality);

    /// <summary>A one-line summary for a log or a human export report.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Encoder.Describe()} at {ExportQualities.ToId(Quality)} — {Reason}");

    /// <summary>
    ///     The selection a caller with no probe would make: the format's software rung, at
    ///     <see cref="ExportQuality.Standard" />. What <c>FfmpegSinkOptions</c> falls back to, so the sink
    ///     is constructible without a subprocess ever running.
    /// </summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    /// <param name="quality">The quality to request.</param>
    public static EncoderSelection SoftwareDefault(string? formatId,
        ExportQuality quality = ExportQuality.Standard) =>
        new(EncoderLadder.SoftwareFor(formatId), quality, "software default (no probe was run)", []);
}

/// <summary>
///     An explicitly requested encoder does not exist on this machine. Distinct from a validation error
///     because nothing about the <c>ExportRequest</c> is wrong: the environment is.
/// </summary>
public sealed class EncoderUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing copy, normally carrying ffmpeg's own words.</param>
    public EncoderUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public EncoderUnavailableException() : base("The requested video encoder is unavailable.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing copy.</param>
    /// <param name="innerException">The cause.</param>
    public EncoderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Walks an <see cref="EncoderLadder" /> and returns the first rung this machine can actually run,
///     plan <c>P2-export-throughput</c> D1 and D4.
///     <para>
///         <b>Stateless.</b> The only thing it holds is the probe, which is where the caching lives. Two
///         threads may select concurrently; that is a requirement, not an accident (plan §7).
///     </para>
/// </summary>
/// <param name="probe">
///     How rungs are verified. Defaults to <see cref="EncoderProbeCache.Shared" />, which is a process
///     memo of machine facts. See that type for why sharing it is safe when sharing a selection is not.
/// </param>
public sealed class EncoderSelector(IEncoderProbe? probe = null)
{
    private readonly IEncoderProbe _probe = probe ?? EncoderProbeCache.Shared;

    /// <summary>
    ///     Resolves the encoder for one export.
    /// </summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />. GIF short-circuits to its pseudo-rung.</param>
    /// <param name="request">
    ///     <c>auto</c> (or null/empty), <c>software</c>, or a rung's ffmpeg name. Anything else is a
    ///     <see cref="ExportValidationException" /> naming the valid choices. A typo in a persisted
    ///     setting or a CI invocation must fail loudly, not silently encode with something else.
    /// </param>
    /// <param name="quality">The requested quality.</param>
    /// <param name="binaryFolder">Where ffmpeg lives, or null to use <c>PATH</c>.</param>
    /// <param name="ct">Cancels the probes.</param>
    /// <exception cref="ExportValidationException"><paramref name="request" /> names nothing on the ladder.</exception>
    /// <exception cref="EncoderUnavailableException">
    ///     A rung was named explicitly and does not verify. <b>Never substituted silently</b> (plan D4):
    ///     a user who asked for <c>h264_nvenc</c> and quietly got <c>libx264</c> has been told a lie about
    ///     what their file is. <c>auto</c> is the default so that this refusal is opt-in.
    /// </exception>
    public EncoderSelection Select(string? formatId, string? request, ExportQuality quality,
        string? binaryFolder, CancellationToken ct = default)
    {
        if (string.Equals(formatId, ExportFormats.Gif, StringComparison.Ordinal))
        {
            // Plan D6's palettegen/paletteuse chain is not an encoder choice, so there is nothing to
            // probe and nothing to override. Reporting it as a rung keeps the JSON one shape.
            return new EncoderSelection(EncoderLadder.Gif, quality,
                "gif is the palette filter chain; it has no encoder ladder", []);
        }

        string wanted = string.IsNullOrWhiteSpace(request)
            ? EncoderLadder.Auto
            : request.Trim().ToLowerInvariant();

        if (string.Equals(wanted, EncoderLadder.Software, StringComparison.Ordinal))
        {
            // No probe at all: the software rung is the floor, and probing it would only be able to say
            // "your ffmpeg cannot encode video", which the encode itself says better and for free.
            return new EncoderSelection(EncoderLadder.SoftwareFor(formatId), quality,
                "software was requested; hardware rungs were not probed", []);
        }

        if (!string.Equals(wanted, EncoderLadder.Auto, StringComparison.Ordinal))
        {
            return SelectNamed(formatId, request!.Trim(), quality, binaryFolder, ct);
        }

        return SelectAuto(formatId, quality, binaryFolder, ct);
    }

    private EncoderSelection SelectAuto(string? formatId, ExportQuality quality, string? binaryFolder,
        CancellationToken ct)
    {
        IReadOnlyList<VideoEncoder> rungs = EncoderLadder.For(formatId);
        List<EncoderProbeResult> attempts = new(rungs.Count);

        for (int i = 0; i < rungs.Count; i++)
        {
            // Between rungs, not just before the walk: each hardware rung is a subprocess that can take
            // most of a second, and a Ctrl+C during a four-rung walk on a machine where nothing verifies
            // should not have to wait the walk out.
            ct.ThrowIfCancellationRequested();

            VideoEncoder rung = rungs[i];
            EncoderProbeResult result = _probe.Verify(rung.Name, binaryFolder, !rung.IsHardware, ct);
            attempts.Add(result);

            if (!result.Works)
            {
                continue;
            }

            string reason = i == 0
                ? "the best rung verified first time"
                : string.Create(CultureInfo.InvariantCulture,
                    $"rung {i + 1} of {rungs.Count}; {DescribeRejects(attempts)}");

            return new EncoderSelection(rung, quality, reason, attempts);
        }

        // Nothing verified, including the software floor, which means the ffmpeg on this machine cannot
        // encode this format at all. Selecting the floor anyway is deliberate: the export then fails with
        // ffmpeg's own message about the real problem instead of ours about the probe.
        VideoEncoder floor = EncoderLadder.SoftwareFor(formatId);
        return new EncoderSelection(floor, quality,
            string.Create(CultureInfo.InvariantCulture,
                $"nothing on the ladder verified ({DescribeRejects(attempts)}); falling back to {floor.Name}"),
            attempts);
    }

    /// <summary>
    ///     Pure argument validation: no ffmpeg lookup, no probes. A request that names nothing on the
    ///     format's ladder throws the same refusal <see cref="Select" /> would, which lets a front end
    ///     refuse a bad <c>--encoder</c> BEFORE its ffmpeg-presence gate: a wrong name must be answered
    ///     with the ladder's choices even on a machine with no ffmpeg at all. <c>auto</c>, <c>software</c>,
    ///     blank, and GIF (whose <c>--encoder</c> is documented as ignored) all pass.
    /// </summary>
    /// <exception cref="ExportValidationException"><paramref name="request" /> names nothing on the ladder.</exception>
    public static void ValidateRequest(string? formatId, string? request)
    {
        if (string.IsNullOrWhiteSpace(request) ||
            string.Equals(formatId, ExportFormats.Gif, StringComparison.Ordinal))
        {
            return;
        }

        string wanted = request.Trim().ToLowerInvariant();
        if (string.Equals(wanted, EncoderLadder.Auto, StringComparison.Ordinal) ||
            string.Equals(wanted, EncoderLadder.Software, StringComparison.Ordinal))
        {
            return;
        }

        if (EncoderLadder.Find(formatId, request.Trim()) is null)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"'{request.Trim()}' is not an encoder for {formatId ?? ExportFormats.WebM}. " +
                $"Choose one of: {EncoderLadder.DescribeChoices(formatId)}."));
        }
    }

    private EncoderSelection SelectNamed(string? formatId, string request, ExportQuality quality,
        string? binaryFolder, CancellationToken ct)
    {
        ValidateRequest(formatId, request);
        VideoEncoder rung = EncoderLadder.Find(formatId, request)!;

        EncoderProbeResult result = _probe.Verify(rung.Name, binaryFolder, !rung.IsHardware, ct);
        if (result.Works)
        {
            return new EncoderSelection(rung, quality, "requested explicitly, and it verified", [result]);
        }

        throw new EncoderUnavailableException(string.Create(CultureInfo.InvariantCulture,
            $"{rung.Name} was requested but does not work here: {result.Detail}. " +
            $"Use --encoder auto to fall back to the best rung this machine can run."));
    }

    private static string DescribeRejects(IReadOnlyList<EncoderProbeResult> attempts)
    {
        List<string> rejected = [];
        foreach (EncoderProbeResult attempt in attempts)
        {
            if (!attempt.Works)
            {
                rejected.Add(attempt.Describe());
            }
        }

        return rejected.Count == 0 ? "nothing was rejected" : string.Join("; ", rejected);
    }
}
