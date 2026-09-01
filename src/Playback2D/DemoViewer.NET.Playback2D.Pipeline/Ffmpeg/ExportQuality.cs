namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>
///     How much an export is willing to spend per frame: plan <c>P2-export-throughput</c> D3.
///     <para>
///         It is a request, not a codec setting: every encoder maps the three ids onto its own speed and
///         rate controls (<c>-cq</c> plus a preset for NVENC, <c>-crf</c> plus <c>-deadline</c>/
///         <c>-cpu-used</c> for libvpx, <c>-crf</c> plus a preset for x264). The mapping lives on
///         <see cref="VideoEncoder" />, so "standard" means the same *intent* everywhere while the
///         numbers behind it stay the ones that were measured for that encoder.
///     </para>
/// </summary>
public enum ExportQuality
{
    /// <summary>Fastest useful setting. For a preview, a scratch clip, or a very long range.</summary>
    Draft,

    /// <summary>The default. "Decent bitrate, quick encoding", the product goal, per encoder.</summary>
    Standard,

    /// <summary>Slowest rung. Still not libvpx's unflagged default, which was never a deliberate choice.</summary>
    Best
}

/// <summary>
///     The persisted spelling of an <see cref="ExportQuality" />. <b>These are persisted keys</b>. They
///     appear in <c>AppSettings.Playback2D.ExportQuality</c> and in <c>dv2d export --quality</c>, so they
///     are never renamed.
/// </summary>
public static class ExportQualities
{
    /// <summary>The <see cref="ExportQuality.Draft" /> id.</summary>
    public const string Draft = "draft";

    /// <summary>The <see cref="ExportQuality.Standard" /> id.</summary>
    public const string Standard = "standard";

    /// <summary>The <see cref="ExportQuality.Best" /> id.</summary>
    public const string Best = "best";

    /// <summary>Every id, fastest first: dialog order and <c>--help</c> order.</summary>
    public static IReadOnlyList<string> All { get; } = [Draft, Standard, Best];

    /// <summary>The id for a value.</summary>
    /// <param name="quality">The value to spell.</param>
    public static string ToId(ExportQuality quality) => quality switch
    {
        ExportQuality.Draft => Draft,
        ExportQuality.Best => Best,
        _ => Standard
    };

    /// <summary>
    ///     Parses an id, case-insensitively. Null, empty and unknown all fail. A caller who wants
    ///     "unknown means the default" says so at its own call site rather than having it hidden here,
    ///     because the CLI wants a usage error and a hand-edited settings file wants the default.
    /// </summary>
    /// <param name="id">The spelling to parse.</param>
    /// <param name="quality">The parsed value, or <see cref="ExportQuality.Standard" /> on failure.</param>
    public static bool TryParse(string? id, out ExportQuality quality)
    {
        quality = ExportQuality.Standard;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        switch (id.Trim().ToLowerInvariant())
        {
            case Draft:
                quality = ExportQuality.Draft;
                return true;
            case Standard:
                quality = ExportQuality.Standard;
                return true;
            case Best:
                quality = ExportQuality.Best;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Parses an id, falling back to <see cref="ExportQuality.Standard" />.</summary>
    /// <param name="id">The spelling to parse.</param>
    public static ExportQuality ParseOrDefault(string? id) =>
        TryParse(id, out ExportQuality quality) ? quality : ExportQuality.Standard;
}
