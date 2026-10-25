#region

using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Goldens;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     How one corpus entry becomes a render: the entry's size, map and layer set, its four sidecars,
///     and the camera the committed PNG was captured with.
///     <para>
///         <b>One definition, two judges.</b> <c>dv2d golden verify</c> and the direct-execution golden
///         suite both arrive here, so the pixels one of them writes are the pixels the other compares.
///         An entry gaining a sidecar or a layer list reaches both without anyone remembering to copy
///         it across.
///     </para>
/// </summary>
internal static class GoldenEntryPlan
{
    /// <summary>
    ///     The request one entry renders through.
    ///     <para>
    ///         <see cref="RenderBackendPreference.ForceCpu" /> by default. The committed corpus is
    ///         <c>goldens/cpu/</c> and CPU is authoritative, so an unqualified verify must not auto-probe
    ///         onto a GPU and report a rasteriser difference as a pixel regression. A caller with a
    ///         parity lane overrides it.
    ///     </para>
    /// </summary>
    /// <param name="entry">The entry to plan for.</param>
    /// <param name="includeLayers">Layer ids that override the entry's own, or null to take the entry's.</param>
    /// <param name="excludeLayers">Layer ids to subtract, or null.</param>
    /// <param name="backend">The surface backend. See the remark above before changing it.</param>
    /// <param name="assets">Where map art comes from, or null for none.</param>
    /// <param name="log">Where the backend factory narrates its probe, or null to drop it.</param>
    public static ScenePlanOptions OptionsFor(GoldenCorpusEntry entry,
        IReadOnlyList<string>? includeLayers = null, IReadOnlyList<string>? excludeLayers = null,
        RenderBackendPreference backend = RenderBackendPreference.ForceCpu,
        AssetsRoot? assets = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ScenePlanOptions
        {
            // A golden is named for its size, so the entry's size is not negotiable here. A caller that
            // accepts a size override resolves one before it gets to this method.
            Size = entry.Size,
            MapName = entry.MapName,
            IncludeLayers = includeLayers ?? entry.Layers,
            ExcludeLayers = excludeLayers,
            Backend = backend,
            Assets = assets ?? AssetsRoot.None,
            Annotations = CorpusSidecars.Ink(entry.CorpusDirectory, entry.Name),
            ZonesOverlayPath = CorpusSidecars.ZonesOverlay(entry.CorpusDirectory, entry.Name),
            Query = CorpusSidecars.Query(entry.CorpusDirectory, entry.Name),
            Overlay = CorpusSidecars.Overlay(entry.CorpusDirectory, entry.Name),
            Log = log
        };
    }

    /// <summary>
    ///     Renders one entry through a plan built from <see cref="OptionsFor" />. The camera is a
    ///     <b>pin</b> rather than a <c>SetAllCameras</c> call: it is re-applied inside the renderer's
    ///     advance after the panes are reconciled, which is what lets a one-shot render supply its
    ///     camera as data.
    /// </summary>
    /// <param name="plan">The plan. Its camera is overwritten.</param>
    /// <param name="entry">The entry, for the size a golden is named for.</param>
    /// <param name="fixture">The loaded scene.</param>
    public static byte[] Render(SceneRenderPlan plan, GoldenCorpusEntry entry, SceneFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(fixture);

        Scene2DFrame frame = plan.WithRadarArt(fixture.Frame);
        SceneTime time = fixture.Time;
        plan.Renderer.Camera = SceneCameraFit.ForFixture(frame, entry.Size, fixture.Camera);
        return plan.Renderer.RenderPng(frame, in time, entry.Size);
    }

    /// <summary>
    ///     How many marker labels the frame draws: the quantity a glyph allowance is denominated in, and
    ///     reported beside a result so a log reader can relate the ink figures to the frame. Read off the
    ///     scene rather than declared per entry, since a hand-written count could be raised to buy slack.
    /// </summary>
    /// <param name="fixture">The scene about to be drawn.</param>
    public static int LabelCount(SceneFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return fixture.Frame.Markers.Count(static m => !string.IsNullOrEmpty(m.Label));
    }
}
