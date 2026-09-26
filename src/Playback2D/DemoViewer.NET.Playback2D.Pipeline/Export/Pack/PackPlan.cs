#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.Review;
using SkiaSharp;

#endregion

// In Pipeline, namespace kept as DemoViewer.NET.Services.Export.Pack: see the note on PackExporter.cs.
namespace DemoViewer.NET.Services.Export.Pack;

/// <summary>
///     What a pack is rendered as: one file for the video formats, one GIF per clip for GIF.
/// </summary>
/// <param name="OutputPath">
///     The pack's file for a video format. For GIF, the folder the clips go in is this path without its
///     extension.
/// </param>
/// <param name="FormatId">One of <see cref="ExportFormats" />.</param>
/// <param name="Side">The square frame's side in pixels; the radar is square.</param>
/// <param name="Fps">Output frame rate.</param>
/// <param name="TitleSeconds">How long a section's title card holds.</param>
/// <param name="EncoderOverride">A ladder rung to force, or null for the ladder's choice.</param>
/// <param name="Quality">An <c>ExportQualities</c> id, or null for the default.</param>
public sealed record PackSettings(
    string OutputPath,
    string FormatId,
    int Side,
    int Fps,
    double TitleSeconds = PackPlanner.DefaultTitleSeconds,
    string? EncoderOverride = null,
    string? Quality = null)
{
    /// <summary>
    ///     The defaults for a format. MP4 at 1080 and 30 fps is the one that plays on any phone (H.264,
    ///     <c>yuv420p</c>, the <c>moov</c> atom first); GIF takes the Lineup Clip size and rate, which keeps
    ///     a clip under the GIF frame cap for a minute and a half.
    /// </summary>
    /// <param name="outputPath">The pack's path.</param>
    /// <param name="formatId">One of <see cref="ExportFormats" />.</param>
    public static PackSettings For(string outputPath, string formatId) =>
        string.Equals(formatId, ExportFormats.Gif, StringComparison.Ordinal)
            ? new PackSettings(outputPath, formatId, 480, 20)
            : new PackSettings(outputPath, formatId, 1080, 30);

    /// <summary>True for GIF: one file per clip, no title cards.</summary>
    public bool IsGif => string.Equals(FormatId, ExportFormats.Gif, StringComparison.Ordinal);
}

/// <summary>One step of a pack, in play order.</summary>
public abstract record PackSegment;

/// <summary>A section's title card: the Review Queue's title and the line under it, held still.</summary>
/// <param name="Title">The section's title.</param>
/// <param name="Subtitle">The line under it, or empty.</param>
/// <param name="Frames">How many frames it holds for.</param>
public sealed record PackTitleCard(string Title, string Subtitle, int Frames) : PackSegment;

/// <summary>One queued clip.</summary>
/// <param name="Entry">The Review Queue entry.</param>
/// <param name="Number">The clip's one-based number in the pack.</param>
/// <param name="Section">The section it sits in, or null above every title card.</param>
/// <param name="EstimatedFrames">Frames the clip renders to, from its tick range; the render counts the demo's own.</param>
/// <param name="MaxFrames">The frame cap for this clip (GIF), or null.</param>
/// <param name="OutputPath">The clip's own file (GIF), or null when it goes into the pack's one file.</param>
public sealed record PackClip(
    ReviewEntry Entry,
    int Number,
    string? Section,
    int EstimatedFrames,
    int? MaxFrames,
    string? OutputPath) : PackSegment
{
    /// <summary>The clip runs past its frame cap and is cut at it.</summary>
    public bool Trimmed => MaxFrames is int cap && EstimatedFrames > cap;
}

/// <summary>A queued clip the pack leaves out, and why.</summary>
/// <param name="Entry">The Review Queue entry.</param>
/// <param name="Reason">What is wrong with it.</param>
public sealed record PackSkip(ReviewEntry Entry, string Reason);

/// <summary>A planned pack: the segments in play order and the clips left out.</summary>
/// <param name="Settings">What it renders as.</param>
/// <param name="Segments">Title cards and clips, in order.</param>
/// <param name="Skipped">Clips that cannot be rendered.</param>
public sealed record PackPlan(PackSettings Settings, IReadOnlyList<PackSegment> Segments, IReadOnlyList<PackSkip> Skipped)
{
    /// <summary>The clips, in order.</summary>
    public IReadOnlyList<PackClip> Clips => [.. Segments.OfType<PackClip>()];

    /// <summary>How many demos the clips come from.</summary>
    public int DemoCount => Clips.Select(c => c.Entry.DemoPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Frames the pack renders to, title cards included, caps applied.</summary>
    public int EstimatedFrames => Segments.Sum(s => s switch
    {
        PackTitleCard card => card.Frames,
        PackClip clip => clip.MaxFrames is int cap ? Math.Min(cap, clip.EstimatedFrames) : clip.EstimatedFrames,
        _ => 0
    });

    /// <summary>The pack's running time, in seconds.</summary>
    public double Seconds => EstimatedFrames / (double)Math.Max(1, Settings.Fps);
}

/// <summary>
///     Pack Export's planning (plan.md §3, Phase 4): the Review Queue turned into what one export renders.
///     Pure: no parse, no render, no file written.
///     <para>
///         <b>Sections are title cards.</b> A queue title card becomes a card held for
///         <see cref="PackSettings.TitleSeconds" /> before its clips; a section left with no clip that can
///         render gets no card, so a pack never shows a title over nothing.
///     </para>
///     <para>
///         <b>GIF is one file per clip.</b> The GIF encoders hold the whole stream to build a palette, which
///         is why a GIF has a frame cap at all (<see cref="SceneExportSession.GifMaxFrames" />); a pack of
///         many clips in one GIF would pass it by the third clip. So each clip is its own GIF, numbered in
///         play order in a folder beside the pack's name, its section in its file name, and each is held to
///         the cap on its own.
///     </para>
/// </summary>
public static class PackPlanner
{
    /// <summary>How long a title card holds by default.</summary>
    public const double DefaultTitleSeconds = 2.5;

    /// <summary>The longest a clip's file name part gets.</summary>
    private const int MaxNameLength = 48;

    /// <summary>fps values GIF can express exactly (the session's own list).</summary>
    private static readonly int[] _gifFps = [10, 20, 25, 50];

    /// <summary>fps values every video format accepts (the session's own list).</summary>
    private static readonly int[] _videoFps = [24, 25, 30, 50, 60, 64];

    /// <summary>
    ///     The layers a pack clip draws: the six scene layers that need no solver, and the demo's ink. Named
    ///     explicitly because ink is opt-in; a demo with no annotations has no document, and the catalog then
    ///     leaves the layer out.
    /// </summary>
    public static IReadOnlySet<string> LayerIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SceneLayerIds.Radar,
        SceneLayerIds.Trails,
        SceneLayerIds.AreaEffects,
        SceneLayerIds.Markers,
        SceneLayerIds.Bomb,
        SceneLayerIds.FloorLabel,
        SceneLayerIds.Annotations
    };

    /// <summary>Why these settings cannot render, or null when they can.</summary>
    /// <param name="settings">The settings.</param>
    public static string? Validate(PackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.OutputPath))
        {
            return "choose where the pack goes";
        }

        if (!ExportFormats.All.Contains(settings.FormatId, StringComparer.Ordinal))
        {
            return $"unknown format {settings.FormatId}";
        }

        if (settings.IsGif)
        {
            if (!_gifFps.Contains(settings.Fps))
            {
                return string.Create(CultureInfo.InvariantCulture, $"GIF cannot run at {settings.Fps} fps exactly; use 10, 20, 25 or 50");
            }

            if (settings.Side is < 16 or > SceneExportSession.GifMaxWidth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"a GIF side of {settings.Side} is outside 16 to {SceneExportSession.GifMaxWidth}");
            }

            return null;
        }

        if (!_videoFps.Contains(settings.Fps))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{settings.Fps} fps is not a video rate; use 24, 25, 30, 50, 60 or 64");
        }

        // yuv420p halves both axes, and phones' hardware decoders stop at 4096.
        return settings.Side is < 16 or > 4096 || settings.Side % 2 != 0
            ? string.Create(CultureInfo.InvariantCulture, $"a video side of {settings.Side} must be even and from 16 to 4096")
            : null;
    }

    /// <summary>
    ///     Plans a pack from the queue's entries in their order. A clip whose demo is not on disk, or whose
    ///     range is empty, is skipped with its reason; the pack renders what is left.
    /// </summary>
    /// <param name="entries">The queue, title cards and clips, in order.</param>
    /// <param name="settings">What the pack renders as.</param>
    /// <param name="fileExists">The existence probe; <see cref="File.Exists" /> in the app.</param>
    public static PackPlan Plan(IReadOnlyList<ReviewEntry> entries, PackSettings settings, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fileExists);

        List<PackSegment> segments = [];
        List<PackSkip> skipped = [];
        ReviewEntry? section = null;
        bool sectionCarded = false;
        int number = 0;
        int titleFrames = Math.Max(1, (int)Math.Round(settings.TitleSeconds * settings.Fps));

        foreach (ReviewEntry entry in entries)
        {
            if (entry.Kind == ReviewEntryKind.Section)
            {
                section = entry;
                sectionCarded = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.DemoPath) || !fileExists(entry.DemoPath))
            {
                skipped.Add(new PackSkip(entry, "demo not found"));
                continue;
            }

            if (entry.ToTick <= entry.FromTick)
            {
                skipped.Add(new PackSkip(entry, "empty range"));
                continue;
            }

            if (section is not null && !sectionCarded && !settings.IsGif)
            {
                segments.Add(new PackTitleCard(section.Title, section.Note, titleFrames));
            }

            sectionCarded = true;
            number++;
            int estimated = EstimateFrames(entry, settings.Fps);
            segments.Add(settings.IsGif
                ? new PackClip(entry, number, section?.Title, estimated, SceneExportSession.GifMaxFrames,
                    ClipPath(settings.OutputPath, number, section?.Title, entry))
                : new PackClip(entry, number, section?.Title, estimated, null, null));
        }

        return new PackPlan(settings, segments, skipped);
    }

    /// <summary>Frames a clip renders to at <paramref name="fps" />, from its ticks and tick rate (64 when unknown).</summary>
    /// <param name="entry">The clip.</param>
    /// <param name="fps">Output frame rate.</param>
    public static int EstimateFrames(ReviewEntry entry, int fps)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int rate = entry.TickRate > 0 ? entry.TickRate : 64;
        return Math.Max(1, (int)Math.Floor((entry.ToTick - entry.FromTick) / (double)rate * fps) + 1);
    }

    /// <summary>The folder a GIF pack's clips go in: the pack path without its extension.</summary>
    /// <param name="outputPath">The pack's path.</param>
    public static string GifFolder(string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        string directory = Path.GetDirectoryName(outputPath) ?? "";
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath));
    }

    /// <summary>
    ///     A GIF clip's file: "03 B executes - smoke into CT.gif" in <see cref="GifFolder" />, numbered so a
    ///     folder listing is play order.
    /// </summary>
    /// <param name="outputPath">The pack's path.</param>
    /// <param name="number">The clip's number.</param>
    /// <param name="section">Its section, or null.</param>
    /// <param name="entry">The clip.</param>
    public static string ClipPath(string outputPath, int number, string? section, ReviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string label = string.Join(" - ", new[] { section, entry.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        string name = SafeName(label);
        string stem = name.Length == 0
            ? number.ToString("D2", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{number:D2} {name}");
        return Path.Combine(GifFolder(outputPath), stem + "." + ExportFormats.Gif);
    }

    /// <summary>
    ///     The export request for one clip over its demo's parsed frames: the queue's tick range resolved to
    ///     demo frames and clamped to the demo, the whole map in frame. Null when the range lies outside it.
    /// </summary>
    /// <param name="clip">The planned clip.</param>
    /// <param name="settings">The pack's settings.</param>
    /// <param name="frames">The demo's frames.</param>
    /// <param name="tickRate">The parsed demo's tick rate.</param>
    public static Scene2DExportRequest? BuildClipRequest(PackClip clip, PackSettings settings,
        IReadOnlyList<DemoFrame> frames, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(frames);
        ReviewEntry entry = clip.Entry;
        if (frames.Count == 0 || entry.ToTick < frames[0].ServerTick || entry.FromTick > frames[^1].ServerTick)
        {
            return null;
        }

        int start = Math.Max(0, TrackerFrameSource.FrameIndexForTick(frames, Math.Max(entry.FromTick, frames[0].ServerTick)));
        int end = TrackerFrameSource.FrameIndexForTick(frames, Math.Min(entry.ToTick, frames[^1].ServerTick));
        if (end < start)
        {
            return null;
        }

        int count = TrackerFrameSource.OutputFrameCount(frames, start, end, settings.Fps, 1.0, tickRate);
        if (clip.MaxFrames is int cap)
        {
            count = Math.Min(count, cap);
        }

        ExportRequest core = new(0, Math.Max(0, count - 1), settings.Fps, new SKSizeI(settings.Side, settings.Side), 1.0,
            settings.FormatId, LayerIds, new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>()));
        return new Scene2DExportRequest(core, clip.OutputPath ?? settings.OutputPath, entry.DemoPath, start, end,
            settings.EncoderOverride, settings.Quality, Palette: ScenePalette.Dark);
    }

    // Letters, digits and a few separators; everything else a space, runs collapsed, trimmed to length.
    private static string SafeName(string text)
    {
        StringBuilder builder = new(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            bool keep = char.IsLetterOrDigit(c) || c is '-' or '_' or '(' or ')' or ',';
            if (keep)
            {
                builder.Append(c);
                space = false;
            }
            else if (!space && builder.Length > 0)
            {
                builder.Append(' ');
                space = true;
            }

            if (builder.Length >= MaxNameLength)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }
}
