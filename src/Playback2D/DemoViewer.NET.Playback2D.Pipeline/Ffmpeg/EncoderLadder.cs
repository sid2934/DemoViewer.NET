#region

using System.Diagnostics.CodeAnalysis;
using DemoViewer.NET.Playback2D.Core.Export;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>
///     The ordered encoder candidates per output format — plan <c>P2-export-throughput</c> D2.
///     <para>
///         Best rung first. <see cref="EncoderSelector" /> walks the list and takes the first rung that a
///         probe verifies; the last rung of every ladder is software, and is therefore the answer on a
///         machine with no working hardware encoder — which includes every CI runner.
///     </para>
///     <para>
///         <b>AV1 is the WebM rung, and the container does not change.</b> The WebM project added AV1 to
///         the container in 2018, so an <c>av1_nvenc</c> export is still a <c>.webm</c> — the format id,
///         the extension, the dialog and every persisted default stay exactly as they were. It is also
///         the reason the hardware rung is AV1 rather than HEVC: HEVC cannot go in a WebM at all.
///     </para>
///     <para>
///         <b>Vendor order is NVENC, QSV, AMF, and it is not a quality claim.</b> On a machine with a
///         discrete NVIDIA card and an integrated GPU, the discrete card is the one that is not also
///         drawing the desktop. On a machine with only an iGPU the NVENC rung fails its probe in
///         milliseconds and the ladder moves on.
///     </para>
/// </summary>
[SuppressMessage("ReSharper", "StaticMemberInitializerReferesToMemberBelow")]
public static class EncoderLadder
{
    /// <summary>
    ///     <c>--encoder auto</c>: walk the ladder and take the best rung that verifies. The default.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    ///     <c>--encoder software</c>: skip every hardware rung. The machine-independent answer — what a
    ///     bisect, a bitrate comparison or a "why does this file look different on my laptop" wants.
    /// </summary>
    public const string Software = "software";
    
    /// <summary>
    ///     AV1 on NVENC. Ada's AV1 block; on Turing and older the probe fails and the ladder moves on.
    ///     <para>
    ///         B-frames and a look-ahead are on from <see cref="ExportQuality.Standard" /> up because they
    ///         are close to free here — plan D3 measured 0.99785 → 0.99862 SSIM for 135 → 156 kbps at the
    ///         same throughput. <c>-rc vbr</c> with <c>-b:v 0</c> is NVENC's constant-quality mode; without
    ///         the zero bitrate, <c>-cq</c> is ignored.
    ///     </para>
    /// </summary>
    public static VideoEncoder Av1Nvenc { get; } = new(
        "av1_nvenc", "av1", EncoderAcceleration.Nvenc,
        "-preset p1 -rc vbr -cq 40 -b:v 0",
        "-preset p4 -rc vbr -cq 34 -b:v 0 -bf 3 -rc-lookahead 8",
        "-preset p6 -rc vbr -cq 28 -b:v 0 -bf 3 -rc-lookahead 20");

    /// <summary>AV1 on Intel Quick Sync. Shipped unprobed on the development hardware — see the plan's D2.</summary>
    public static VideoEncoder Av1Qsv { get; } = new(
        "av1_qsv", "av1", EncoderAcceleration.QuickSync,
        "-preset veryfast -global_quality 40",
        "-preset medium -global_quality 34",
        "-preset slow -global_quality 28");

    /// <summary>
    ///     AV1 on AMD AMF. Listed by ffmpeg on any AMF-capable machine, including ones whose GPU has no
    ///     AV1 encode block at all — which is precisely the case the probe caught on the development box.
    /// </summary>
    public static VideoEncoder Av1Amf { get; } = new(
        "av1_amf", "av1", EncoderAcceleration.Amf,
        "-quality speed -rc cqp -qp_i 40 -qp_p 40",
        "-quality balanced -rc cqp -qp_i 32 -qp_p 34",
        "-quality quality -rc cqp -qp_i 26 -qp_p 28");

    /// <summary>
    ///     VP9 on libvpx. The WebM floor, and the rung every LGPL ffmpeg build carries.
    ///     <para>
    ///         <b>The <c>-deadline</c>/<c>-cpu-used</c> pair is the whole point of this rung's rewrite.</b>
    ///         Before P2 this invocation carried neither, which is libvpx's slowest setting on a codec
    ///         whose speed control is exactly those two flags: 97 fps for CRF 30.
    ///         <c>
    ///             -deadline realtime
    ///             -cpu-used 5
    ///         </c>
    ///         at CRF 32 is 526 fps for 15 % more bits and an SSIM still above 0.999
    ///         (plan D3). <c>-row-mt 1</c> stays on every rung; it is what lets libvpx use more than one
    ///         core per tile.
    ///     </para>
    /// </summary>
    public static VideoEncoder Vp9 { get; } = new(
        "libvpx-vp9", "vp9", EncoderAcceleration.Software,
        "-b:v 0 -crf 36 -row-mt 1 -deadline realtime -cpu-used 8",
        "-b:v 0 -crf 32 -row-mt 1 -deadline realtime -cpu-used 5",
        "-b:v 0 -crf 30 -row-mt 1 -deadline good -cpu-used 2");

    /// <summary>H.264 on NVENC. The MP4 hardware rung on every NVIDIA card since Kepler.</summary>
    public static VideoEncoder H264Nvenc { get; } = new(
        "h264_nvenc", "h264", EncoderAcceleration.Nvenc,
        "-preset p1 -rc vbr -cq 32 -b:v 0",
        "-preset p4 -rc vbr -cq 26 -b:v 0 -bf 3 -rc-lookahead 8",
        "-preset p6 -rc vbr -cq 21 -b:v 0 -bf 3 -rc-lookahead 20");

    /// <summary>H.264 on Intel Quick Sync.</summary>
    public static VideoEncoder H264Qsv { get; } = new(
        "h264_qsv", "h264", EncoderAcceleration.QuickSync,
        "-preset veryfast -global_quality 32",
        "-preset medium -global_quality 26",
        "-preset slow -global_quality 21");

    /// <summary>H.264 on AMD AMF. Verified working on the development box's Radeon iGPU.</summary>
    public static VideoEncoder H264Amf { get; } = new(
        "h264_amf", "h264", EncoderAcceleration.Amf,
        "-quality speed -rc cqp -qp_i 32 -qp_p 34",
        "-quality balanced -rc cqp -qp_i 24 -qp_p 26",
        "-quality quality -rc cqp -qp_i 20 -qp_p 21");

    /// <summary>
    ///     H.264 on x264. The MP4 floor.
    ///     <para>
    ///         Today's default was <c>-preset medium -crf 30</c>, which plan D3 measured as beaten on both
    ///         axes by <c>-preset veryfast -crf 21</c>: faster AND a higher SSIM at a higher bitrate. CRF 30
    ///         on x264 throws the quality away before the preset can spend any effort on it.
    ///     </para>
    /// </summary>
    public static VideoEncoder X264 { get; } = new(
        "libx264", "h264", EncoderAcceleration.Software,
        "-preset superfast -crf 26",
        "-preset veryfast -crf 21",
        "-preset medium -crf 18");

    /// <summary>
    ///     The GIF pseudo-rung. There is no <c>-c:v</c> for it — plan D6's palettegen/paletteuse filter
    ///     chain is the encoder — but a ladder entry means a GIF export reports through the same
    ///     <see cref="EncoderSelection" /> shape as every other export instead of a special case in three
    ///     callers.
    /// </summary>
    public static VideoEncoder Gif { get; } = new(
        "gif", "gif", EncoderAcceleration.Software, "", "", "", "");

    private static readonly VideoEncoder[] _webm = [Av1Nvenc, Av1Qsv, Av1Amf, Vp9];
    private static readonly VideoEncoder[] _mp4 = [H264Nvenc, H264Qsv, H264Amf, X264];
    private static readonly VideoEncoder[] _gif = [Gif];
    
    /// <summary>
    ///     The rungs for a format, the best first. An unknown format id gets the WebM ladder, matching
    ///     <c>SceneExportSession.SupportedFps</c>'s treatment of the same case.
    /// </summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    public static IReadOnlyList<VideoEncoder> For(string? formatId)
    {
        return formatId?.Trim().ToLowerInvariant() switch
        {
            ExportFormats.Mp4 => _mp4,
            ExportFormats.Gif => _gif,
            _ => _webm
        };
    }
    
    /// <summary>
    ///     The software rung of a format's ladder — always its last entry. What
    ///     <c>--encoder software</c> resolves to, and the sink's default when no selection was made.
    /// </summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    public static VideoEncoder SoftwareFor(string? formatId)
    {
        IReadOnlyList<VideoEncoder> rungs = For(formatId);
        for (int i = rungs.Count - 1; i >= 0; i--)
        {
            if (!rungs[i].IsHardware)
            {
                return rungs[i];
            }
        }

        // Unreachable while every ladder ends in software, which is an invariant LadderTests asserts.
        return rungs[^1];
    }

    /// <summary>The rung of a format's ladder with this ffmpeg name, or null.</summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    /// <param name="encoderName">The ffmpeg encoder id to look for.</param>
    public static VideoEncoder? Find(string? formatId, string? encoderName)
    {
        if (string.IsNullOrWhiteSpace(encoderName))
        {
            return null;
        }

        string wanted = encoderName.Trim();
        foreach (VideoEncoder rung in For(formatId))
        {
            if (string.Equals(rung.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return rung;
            }
        }

        return null;
    }

    /// <summary>The names a <c>--encoder</c> value may take for a format, for a usage message.</summary>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    public static string DescribeChoices(string? formatId)
    {
        IReadOnlyList<VideoEncoder> rungs = For(formatId);
        string[] names = new string[rungs.Count + 2];
        names[0] = Auto;
        names[1] = Software;
        for (int i = 0; i < rungs.Count; i++)
        {
            names[i + 2] = rungs[i].Name;
        }

        return string.Join(", ", names);
    }
}
