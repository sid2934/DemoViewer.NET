#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>What does the encoding — plan <c>P2-export-throughput</c> D2.</summary>
public enum EncoderAcceleration
{
    /// <summary>A CPU codec library: <c>libvpx-vp9</c>, <c>libx264</c>, or the GIF palette chain.</summary>
    Software,

    /// <summary>NVIDIA NVENC, a fixed-function block on the die.</summary>
    Nvenc,

    /// <summary>Intel Quick Sync Video.</summary>
    QuickSync,

    /// <summary>AMD Advanced Media Framework.</summary>
    Amf
}

/// <summary>
///     One rung of an <see cref="EncoderLadder" />: an ffmpeg encoder plus the arguments each
///     <see cref="ExportQuality" /> maps to on it.
///     <para>
///         <b>Data, not behaviour.</b> The three argument strings were measured (plan D3's table:
///         throughput, output bitrate and SSIM for every cell) rather than copied from a tutorial, and
///         keeping them as plain strings on a record is what lets a test assert the exact line an export
///         would run without starting anything.
///     </para>
///     <para>
///         Every rung carries its own rate-control flags in full, including the ones that look like
///         defaults. <c>-b:v 0</c> in particular is not optional: without it both libvpx and NVENC ignore
///         the quality target and encode at a constant bitrate, which is the failure mode where an export
///         "works" and looks wrong.
///     </para>
/// </summary>
/// <param name="Name">The ffmpeg encoder id, i.e. what follows <c>-c:v</c>.</param>
/// <param name="Codec">The codec it produces (<c>av1</c>, <c>vp9</c>, <c>h264</c>). Diagnostics only.</param>
/// <param name="Acceleration">Which engine encodes.</param>
/// <param name="DraftArguments">Extra arguments for <see cref="ExportQuality.Draft" />.</param>
/// <param name="StandardArguments">Extra arguments for <see cref="ExportQuality.Standard" />.</param>
/// <param name="BestArguments">Extra arguments for <see cref="ExportQuality.Best" />.</param>
/// <param name="PixelFormat">
///     The pixel format forced on the output. <c>yuv420p</c> everywhere, which is why
///     <c>ExportFormats.RequiresEvenDimensions</c> is true for both video formats.
/// </param>
public sealed record VideoEncoder(
    string Name,
    string Codec,
    EncoderAcceleration Acceleration,
    string DraftArguments,
    string StandardArguments,
    string BestArguments,
    string PixelFormat = "yuv420p")
{
    /// <summary>
    ///     True when the encode runs on dedicated silicon rather than on the cores the renderer is using.
    ///     <para>
    ///         That distinction, not the speed, is the reason the ladder exists: P1 §7 measured the same
    ///         frames rastering 49 % slower with libvpx running beside them. It is also the quantity a
    ///         future export node has to ration — see the plan's §7, NVENC session limits.
    ///     </para>
    /// </summary>
    public bool IsHardware => Acceleration != EncoderAcceleration.Software;

    /// <summary>The arguments this rung uses at one quality.</summary>
    /// <param name="quality">The requested quality.</param>
    public string ArgumentsFor(ExportQuality quality) => quality switch
    {
        ExportQuality.Draft => DraftArguments,
        ExportQuality.Best => BestArguments,
        _ => StandardArguments
    };

    /// <summary>A one-line description for a log or a JSON payload: <c>av1_nvenc (nvenc, av1)</c>.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Name} ({Acceleration.ToString().ToLowerInvariant()}, {Codec})");
}
