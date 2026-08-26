#region

using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     Everything the draw op is allowed to see, captured on the UI thread inside the render gate and
///     then immutable (plan §5.8).
///     <para>
///         The mutable <c>PaneSet</c> / <c>LevelPane</c> never cross the thread boundary: each pane
///         arrives as a <see cref="LevelPaneSnapshot" /> value. <see cref="Frame" /> is a reference but
///         immutable by contract; <see cref="Palette" /> is swapped wholesale on a theme change rather
///         than edited.
///     </para>
///     <para>
///         <see cref="SubmissionId" /> is monotonic. It is what the gate stress test uses to prove that
///         a rendered frame corresponds to exactly one submitted state and never a blend of two
///         (design risk 2).
///     </para>
/// </summary>
/// <param name="SubmissionId">Monotonic id of this submission.</param>
/// <param name="Frame">The world state to draw.</param>
/// <param name="Time">The injected clock.</param>
/// <param name="Panes">One snapshot per arranged pane, lowest level first.</param>
/// <param name="Palette">Resolved theme colours and stroke widths.</param>
/// <param name="Purpose">
///     Why this scene is being rendered. <b>Reserved</b> — the compositor copies it into every
///     <c>SceneRenderContext</c> and no layer branches on it, so Export and Interactive produce the same
///     pixels. See <see cref="RenderPurpose" />.
/// </param>
/// <param name="HostBounds">The whole host surface, in host coordinates.</param>
/// <param name="RenderScaling">Device pixels per DIP; exactly 1.0 offscreen.</param>
/// <param name="Levels">
///     The level set, so a pane can answer <c>BelongsHere</c> with the pre-v2 nearest-band fallback.
///     Null on a single-level submission, where the sentinel already passes everything.
/// </param>
public readonly record struct SceneSubmission(
    long SubmissionId,
    Scene2DFrame Frame,
    SceneTime Time,
    IReadOnlyList<LevelPaneSnapshot> Panes,
    ScenePalette Palette,
    RenderPurpose Purpose,
    SKRect HostBounds,
    float RenderScaling,
    MapSpace? Levels = null);
