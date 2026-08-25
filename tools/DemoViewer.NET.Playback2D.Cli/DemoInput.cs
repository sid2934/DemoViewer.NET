#region

using System.Diagnostics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Pipeline.Frames;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Loading a <c>.dem</c> and turning <c>--tick</c>/<c>--frame</c> into a frame index.
///     <para>
///         The parse dominates a <c>--demo</c> render (risk R10) — a 400 MB demo is seconds, not
///         milliseconds — so the elapsed split is always reported. The sub-second exit criterion is the
///         <c>--fixture</c> path, and printing the breakdown is what keeps the difference visible instead
///         of looking like a slow renderer.
///     </para>
/// </summary>
internal static class DemoInput
{
    /// <summary>Parses a demo from disk.</summary>
    /// <param name="path">Path to the <c>.dem</c>.</param>
    /// <param name="parseMs">Wall-clock parse time, for the report.</param>
    /// <exception cref="FileNotFoundException">No file at <paramref name="path" />.</exception>
    public static ParsedDemo Load(string path, out double parseMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"demo not found: {path}", path);
        }

        long started = Stopwatch.GetTimestamp();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        parseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (demo.Frames.Count == 0)
        {
            throw new InvalidDataException($"{path} parsed to zero frames.");
        }

        return demo;
    }

    /// <summary>
    ///     Resolves <c>--tick N</c> (binary search over <c>ServerTick</c>) or <c>--frame N</c> (used
    ///     as-is). Exactly one must be given.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="demo">The parsed demo.</param>
    /// <exception cref="CliUsageException">Neither, both, or an out-of-range value.</exception>
    public static int ResolveFrameIndex(CliArgs args, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(demo);

        string? tickRaw = args.String("tick");
        string? frameRaw = args.String("frame");

        if (tickRaw is not null && frameRaw is not null)
        {
            throw new CliUsageException("--tick and --frame are mutually exclusive.");
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        string span = string.Create(CultureInfo.InvariantCulture,
            $"frames 0..{frames.Count - 1}, ticks {frames[0].ServerTick}..{frames[^1].ServerTick}");

        if (frameRaw is not null)
        {
            if (!int.TryParse(frameRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
            {
                throw new CliUsageException($"--frame expects an integer, got '{frameRaw}'.");
            }

            return frame >= 0 && frame < frames.Count
                ? frame
                : throw new CliUsageException($"--frame {frame} is outside the demo ({span}).");
        }

        if (tickRaw is null)
        {
            throw new CliUsageException("--demo requires --tick N or --frame N.");
        }

        if (!int.TryParse(tickRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tick))
        {
            throw new CliUsageException($"--tick expects an integer, got '{tickRaw}'.");
        }

        int index = TrackerFrameSource.FrameIndexForTick(frames, tick);
        return index >= 0
            ? index
            : throw new CliUsageException($"--tick {tick} is outside the demo ({span}).");
    }

    /// <summary>The demo's tick rate, falling back to 64 when it is not stated.</summary>
    /// <param name="demo">The parsed demo.</param>
    public static int TickRate(ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return demo.TickRate > 0 ? (int)Math.Round((double)demo.TickRate) : 64;
    }
}
