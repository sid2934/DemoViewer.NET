#region

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;

/// <summary>What a probe learned about one encoder on this machine.</summary>
/// <param name="Encoder">The ffmpeg encoder id that was asked about.</param>
/// <param name="Works">True when it encoded two frames without complaint.</param>
/// <param name="Detail">
///     Why, in ffmpeg's own words where there are any. <c>"verified"</c>, <c>"not built into this
///     ffmpeg"</c>, <c>"listed (software)"</c>, or the encoder's stderr tail.
/// </param>
/// <remarks>
///     <b>There is no duration on this record, deliberately.</b> Pipeline is banned from
///     <c>System.Diagnostics.Stopwatch</c> outside <c>…Benchmarking</c> and <c>…Export</c>
///     (<c>BannedApiTests</c>), and widening a determinism exemption to carry a diagnostic number would
///     be paying the wrong price for it. What a probe costs is one number for the whole ladder walk, and
///     the front end that calls <c>EncoderSelector.Select</c> is the thing that can time it —
///     <c>dv2d export --json</c> reports it as <c>encoder_probe_ms</c>.
/// </remarks>
public readonly record struct EncoderProbeResult(string Encoder, bool Works, string Detail)
{
    /// <summary>A one-line description for a log or a JSON payload.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Encoder}: {(Works ? "ok" : "unavailable")} — {Detail}");
}

/// <summary>
///     Answers "can this machine actually run this encoder" — plan <c>P2-export-throughput</c> D1.
///     <para>
///         The seam exists so <see cref="EncoderSelector" />'s ladder walk can be tested with no ffmpeg,
///         no GPU and no subprocess, which is the only way the fallback behaviour can be asserted on a
///         CI runner that has none of the three.
///     </para>
/// </summary>
public interface IEncoderProbe
{
    /// <summary>Verifies one encoder. Never throws; a failure is a <c>Works: false</c> result.</summary>
    /// <param name="encoderName">The ffmpeg encoder id.</param>
    /// <param name="binaryFolder">Where ffmpeg lives, or null to use <c>PATH</c>.</param>
    /// <param name="trustListing">
    ///     True for software rungs: presence in <c>ffmpeg -encoders</c> is accepted without a test encode.
    ///     The failure mode a test encode exists for — listed, initialises, then dies on a missing device
    ///     — is a driver fact, and paying 600 ms per export on a GPU-less runner to re-learn that libvpx
    ///     is still libvpx would be a tax on the one lane that can never benefit.
    /// </param>
    /// <param name="ct">Cancels the probe.</param>
    EncoderProbeResult Verify(string encoderName, string? binaryFolder, bool trustListing,
        CancellationToken ct);
}

/// <summary>
///     The real probe: asks <c>ffmpeg -encoders</c> what the build has, then makes the candidate encode
///     two frames.
///     <para>
///         <b>The listing is necessary and not sufficient.</b> It is a BUILD manifest. On the machine this
///         was written against, <c>av1_qsv</c>, <c>h264_qsv</c> and <c>av1_amf</c> are all listed and all
///         fail at device creation — and <c>av1_amf</c> fails on the same silicon where <c>h264_amf</c>
///         works, because that Radeon iGPU has an H.264 encode block and no AV1 one. A ladder that trusted
///         the listing would pick a broken encoder and discover it an hour into a full-match export.
///     </para>
///     <para>
///         <b>The test encode has nothing in it but the encoder.</b> Two 256×256 <c>yuv420p</c> frames of
///         zeros on stdin as <c>rawvideo</c>, out to <c>-f null -</c>: no <c>lavfi</c>, no filter graph, no
///         container, no temp file, nothing that could fail for a reason that is not the encoder. 256×256
///         clears every hardware minimum (AV1 NVENC's is 160×128).
///     </para>
/// </summary>
public sealed class FfmpegEncoderProbe : IEncoderProbe
{
    /// <summary>How long a probe may run before it is killed and reported as unavailable.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The square edge of the test frames. Above every hardware encoder's minimum.</summary>
    public const int ProbeSize = 256;

    /// <summary>Frames fed to the test encode. Two, so an encoder that needs a second frame gets one.</summary>
    public const int ProbeFrames = 2;

    private static readonly char[] _newlines = ['\r', '\n'];

    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _listings =
        new(StringComparer.Ordinal);

    /// <summary>How many test encodes this probe has actually spawned. Diagnostics and tests.</summary>
    public int TestEncodes => _testEncodes;

    private int _testEncodes;

    /// <inheritdoc />
    public EncoderProbeResult Verify(string encoderName, string? binaryFolder, bool trustListing,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderName);

        IReadOnlySet<string> listed = ListEncoders(binaryFolder, ct);

        if (!listed.Contains(encoderName))
        {
            return new EncoderProbeResult(encoderName, false,
                listed.Count == 0
                    ? "could not read `ffmpeg -encoders`"
                    : "not built into this ffmpeg");
        }

        if (trustListing)
        {
            return new EncoderProbeResult(encoderName, true, "listed (software)");
        }

        Interlocked.Increment(ref _testEncodes);
        (bool ok, string detail) = TestEncode(encoderName, binaryFolder, ct);
        return new EncoderProbeResult(encoderName, ok, detail);
    }

    /// <summary>
    ///     The video encoder ids this ffmpeg build carries. Read once per binary folder and cached; an
    ///     unreadable ffmpeg answers with an empty set rather than throwing.
    /// </summary>
    /// <param name="binaryFolder">Where ffmpeg lives, or null to use <c>PATH</c>.</param>
    /// <param name="ct">Cancels the listing.</param>
    public IReadOnlySet<string> ListEncoders(string? binaryFolder, CancellationToken ct)
    {
        string key = binaryFolder ?? string.Empty;

        if (_listings.TryGetValue(key, out IReadOnlySet<string>? cached))
        {
            return cached;
        }

        HashSet<string> read = ReadListing(binaryFolder, ct);

        // <b>Only a listing that named something is a fact about the build.</b> An empty one means the
        // read did not happen — a token tripped mid-walk, a killed process, an ffmpeg caught mid
        // reinstall — and those are facts about a moment, not about a machine. Remembering one would
        // answer every later question with "not built into this ffmpeg", which walks the whole ladder
        // into the software floor; and because the app holds ONE cache for the session, it would stay
        // that way until the user happened to press Re-check. EncoderProbeCache already refuses to
        // remember a cancelled RESULT for this exact reason, and the listing underneath it must agree.
        if (read.Count > 0)
        {
            _listings[key] = read;
        }

        return read;
    }

    /// <summary>
    ///     Forgets the cached <c>-encoders</c> listings, so the next question re-reads them.
    ///     <para>
    ///         Only non-empty listings are ever held (see <see cref="ListEncoders" />), so this is about
    ///         a build that changed underneath us rather than one that failed to be read — which is
    ///         exactly what the export dialog's Re-check button means, so
    ///         <see cref="EncoderProbeCache.Clear" /> reaches through to this.
    ///     </para>
    /// </summary>
    public void ClearListings() => _listings.Clear();

    private static HashSet<string> ReadListing(string? binaryFolder, CancellationToken ct)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        (int exit, string stdout, _) = Run(binaryFolder, ["-hide_banner", "-encoders"], null, ct);
        if (exit != 0)
        {
            return names;
        }

        // Lines look like " V....D av1_nvenc            NVIDIA NVENC av1 encoder (codec av1)". The first
        // field's leading character is the media type, so 'V' is the filter that keeps audio and subtitle
        // encoders out of a video ladder's namespace.
        foreach (string line in stdout.Split(_newlines, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length < 8 || trimmed[0] != 'V')
            {
                continue;
            }

            string[] fields = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2)
            {
                names.Add(fields[1]);
            }
        }

        return names;
    }

    private static (bool Ok, string Detail) TestEncode(string encoderName, string? binaryFolder,
        CancellationToken ct)
    {
        // yuv420p: one luma plane plus two half-resolution chroma planes. All zeros — the encoder does
        // not care what the picture is, only that it is handed one of the size it was told about.
        byte[] frame = new byte[ProbeSize * ProbeSize * 3 / 2];
        byte[] payload = new byte[frame.Length * ProbeFrames];

        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pix_fmt", "yuv420p",
            "-s", string.Create(CultureInfo.InvariantCulture, $"{ProbeSize}x{ProbeSize}"),
            "-r", "30", "-i", "-",
            "-frames:v", ProbeFrames.ToString(CultureInfo.InvariantCulture),
            "-c:v", encoderName, "-an", "-f", "null", "-"
        ];

        (int exit, _, string stderr) = Run(binaryFolder, arguments, payload, ct);

        if (exit == 0)
        {
            return (true, "verified");
        }

        // ffmpeg's FIRST error line, not its last. A failing encoder says the useful thing immediately
        // ("[av1_qsv] Error creating a MFX session: -9" — no Intel device) and then says several
        // consequences of it, ending with "[out#0/null] Nothing was written into output file", which is
        // true, generic, and sends a reader nowhere. Preferring a line that names the encoder itself is
        // what keeps the cause in front of its own side effects.
        string cause = FirstCause(stderr, encoderName);
        return (false, cause.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"test encode failed (exit {exit})")
            : cause);
    }

    private static string FirstCause(string text, string encoderName)
    {
        string[] lines = text.Split(_newlines, StringSplitOptions.RemoveEmptyEntries);
        string? firstNonEmpty = null;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            firstNonEmpty ??= trimmed;

            if (trimmed.Contains(encoderName, StringComparison.Ordinal))
            {
                return Clip(trimmed);
            }
        }

        return firstNonEmpty is null ? string.Empty : Clip(firstNonEmpty);
    }

    // Long enough to carry the cause, short enough to sit on one console line next to the encoder name.
    private static string Clip(string line) => line.Length > 160 ? line[..160] : line;

    private static (int Exit, string StdOut, string StdErr) Run(string? binaryFolder,
        IReadOnlyList<string> arguments, byte[]? stdin, CancellationToken ct)
    {
        string executable = string.IsNullOrEmpty(binaryFolder)
            ? FfmpegLocator.ExecutableName
            : Path.Combine(binaryFolder, FfmpegLocator.ExecutableName);

        ProcessStartInfo info = new()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Nothing to learn from a walk that has already been abandoned, and starting a process here
        // would only be something to kill on the next line.
        ct.ThrowIfCancellationRequested();

        try
        {
            using Process process = Process.Start(info) ?? throw new FileNotFoundException(executable);

            // Cancellation ENDS THE CHILD; it does not stop us reading it. Handing the token to the two
            // reads below instead would abandon the pipes while ffmpeg was still writing to them, and a
            // full pipe buffer blocks the child forever — so a cancelled probe would sit out the whole
            // 20 s timeout before reporting a failure nobody was waiting for any more.
            using CancellationTokenRegistration kill =
                ct.Register(static state => TryKill((Process)state!), process);

            // Both streams are read before the wait: a full pipe buffer on either one deadlocks the
            // child, and `-encoders` writes several kilobytes.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            if (stdin is not null)
            {
                try
                {
                    process.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                }
                catch (IOException)
                {
                    // The encoder refused before it read anything — a broken pipe here IS the answer,
                    // and it is on stderr. Close the pipe and let the exit code speak.
                }
            }

            process.StandardInput.Close();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return (-1, string.Empty,
                    string.Create(CultureInfo.InvariantCulture,
                        $"the probe did not finish within {Timeout.TotalSeconds:F0} s"));
            }

            // A process we killed ourselves exits non-zero, and reporting that as "this encoder does not
            // work here" would turn a cancellation into a machine fact. Say what actually happened.
            ct.ThrowIfCancellationRequested();

            return (process.ExitCode, SafeResult(stdout), SafeResult(stderr));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No ffmpeg, no permission, a corrupt binary: all "this encoder is unavailable here", which
            // is a fact the ladder handles. A probe must never be the thing that fails an export.
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string SafeResult(Task<string> read)
    {
        try
        {
            return read.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or SystemException)
        {
            // It exited between the timeout and the kill. Nothing to do.
        }
    }
}

/// <summary>
///     Memoises probe answers per (ffmpeg directory, encoder) — plan <c>P2-export-throughput</c> D1.
///     <para>
///         <b>It caches facts about the machine, not decisions about a session.</b> That is the whole
///         reason it is safe to share one across concurrent exports, and why <see cref="Shared" /> exists
///         at all while <c>EncoderSelection</c> is deliberately a per-session value (plan D5). Nothing a
///         session can do changes what a probe would answer.
///     </para>
/// </summary>
/// <param name="inner">The probe to memoise. Defaults to a real <see cref="FfmpegEncoderProbe" />.</param>
public sealed class EncoderProbeCache(IEncoderProbe? inner = null) : IEncoderProbe
{
    private readonly ConcurrentDictionary<(string Folder, string Encoder), EncoderProbeResult> _results =
        new();

    private readonly IEncoderProbe _inner = inner ?? new FfmpegEncoderProbe();

    /// <summary>
    ///     A process-wide cache, for callers that have no better place to keep one. The app's composition
    ///     root holds this one; <c>dv2d</c> builds its own per invocation, because a CLI process runs one
    ///     export and then exits.
    /// </summary>
    public static EncoderProbeCache Shared { get; } = new();

    /// <summary>Answers this cache has served without asking the underlying probe. Diagnostics and tests.</summary>
    public int Hits => _hits;

    private int _hits;

    /// <inheritdoc />
    public EncoderProbeResult Verify(string encoderName, string? binaryFolder, bool trustListing,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderName);

        (string, string) key = (binaryFolder ?? string.Empty, encoderName);
        if (_results.TryGetValue(key, out EncoderProbeResult cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        EncoderProbeResult result = _inner.Verify(encoderName, binaryFolder, trustListing, ct);

        // A cancelled probe is not a fact about the machine; caching it would poison the next export.
        if (!ct.IsCancellationRequested)
        {
            _results[key] = result;
        }

        return result;
    }

    /// <summary>
    ///     Forgets everything. For tests, and for an app-side "re-check" after an install.
    ///     <para>
    ///         It reaches through to the wrapped probe's own <c>-encoders</c> listing cache when there is
    ///         one: a <c>Clear</c> that emptied this dictionary and left the listing behind would answer
    ///         the next question from the build that was installed before the user pressed Re-check,
    ///         which is the one moment it exists for.
    ///     </para>
    /// </summary>
    public void Clear()
    {
        _results.Clear();
        Interlocked.Exchange(ref _hits, 0);
        (_inner as FfmpegEncoderProbe)?.ClearListings();
    }
}
