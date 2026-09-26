#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;

#endregion

// In Pipeline, namespace kept as DemoViewer.NET.Services.Export (split out of IExportJobService.cs,
// which stays in the App): the record itself carries nothing App-only, and PackPlanner.BuildClipRequest
// (Services.Export.Pack, moved here for the same reason) returns one. Every existing `using
// DemoViewer.NET.Services.Export;` in the App keeps resolving it unchanged.
namespace DemoViewer.NET.Services.Export;

/// <summary>
///     A render request over one demo range: the Core request plus what a source needs to build the
///     scene.
/// </summary>
/// <param name="Core">
///     Size, format, layers, camera and the <b>source-relative</b> frame range. Frame 0 of the source is
///     <paramref name="DemoStartFrame" />; the runner re-stamps the range from the built source's own
///     frame count, so the two can never disagree.
/// </param>
/// <param name="OutputPath">Where the encoded file goes.</param>
/// <param name="DemoPath">The demo being exported, for the status text and diagnostics.</param>
/// <param name="DemoStartFrame">
///     First DEMO frame index. Distinct from <c>Core.StartFrame</c> on purpose: an export renders at a
///     fixed timestep, so one output frame is not one demo frame, and conflating the two indices is how a
///     range silently becomes the wrong length.
/// </param>
/// <param name="DemoEndFrame">Last demo frame index, inclusive.</param>
/// <param name="EncoderOverride">
///     <c>auto</c> (the default), <c>software</c>, or an <c>EncoderLadder</c> rung's ffmpeg name, plan
///     P2 D4. It rides the request rather than the runner so two exports in one process can disagree,
///     which is the per-session shape the plan's §7 export node needs.
/// </param>
/// <param name="Quality">
///     <c>draft</c>, <c>standard</c> (the default) or <c>best</c>. A string for the same reason the
///     setting is one: an unknown value degrades to the default rather than throwing.
/// </param>
/// <param name="Ink">
///     The annotation document to burn in, frozen before the render starts, or null for no ink.
///     <para>
///         <b>On the request, not a mutable field.</b> The App's job service awaits the heavy-job gate
///         before the runner's setup closure reads the document, so a field the dialog wrote could be
///         replaced by a second Start before the first, still-parked export ever read it. The request is
///         the only object that is one-per-run, so it is the only safe place to carry it.
///     </para>
/// </param>
/// <param name="Palette">
///     The scene colours to render with, resolved before the render starts, or null to let the setup
///     decide.
///     <para>
///         <b>Here for a harder reason than the ink.</b> The App resolves the palette from
///         <c>Application.Current.ActualThemeVariant</c>, a styled property reachable only on the UI
///         thread, while the export itself runs on a pool thread. A <see cref="ScenePalette" /> is a
///         plain record of <c>SKColor</c>, so once resolved it crosses threads freely: the theme is only
///         reachable where it is read, so the read has to happen at Start and travel with the request.
///     </para>
/// </param>
public sealed record Scene2DExportRequest(
    ExportRequest Core,
    string OutputPath,
    string DemoPath,
    int DemoStartFrame = 0,
    int DemoEndFrame = 0,
    string? EncoderOverride = null,
    string? Quality = null,
    AnnotationSession? Ink = null,
    ScenePalette? Palette = null);
