#region

using System.Globalization;
using System.Text;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     The strat canvas's step row (step-authoring.md §3.10): a marker per step on the path, its glyph the
///     step's number with a fork where a branch hangs from it, its tooltip the call sheet's line for the
///     step; and a band per step window. Content comes from the projection the canvas hands it, so the
///     timeline data it is queried with is only the axis.
/// </summary>
public sealed class StepTrack : ITimelineTrack
{
    /// <summary>The track's stable id.</summary>
    public const string TrackId = "strat.steps";

    /// <summary>What a step with a branch hanging from it shows after its number.</summary>
    public const string ForkGlyph = "⑂";

    private List<TimelineBand> _bands = [];
    private List<TimelineMarker> _markers = [];

    /// <inheritdoc />
    public string Id => TrackId;

    /// <inheritdoc />
    public string DisplayName => "Steps";

    /// <inheritdoc />
    public event Action? MarkersChanged;

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data) => _markers.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => _markers;

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => _bands;

    /// <summary>Re-reads a projection: one marker and one band per path step. Raises <see cref="MarkersChanged" />.</summary>
    /// <param name="projection">The canvas's projection, or null for no strat.</param>
    /// <param name="document">The open strat, whose branches put forks on the steps.</param>
    /// <param name="callouts">The owner's words for the map's places, for the tooltips.</param>
    public void Update(StratSceneProjection? projection, StratDocument? document, CalloutResolver? callouts)
    {
        List<TimelineMarker> markers = [];
        List<TimelineBand> bands = [];

        if (projection is not null)
        {
            for (int i = 0; i < projection.Path.Count; i++)
            {
                StratStep step = projection.Path[i].Step;
                StepWindow window = projection.Schedule.Windows[i];
                List<StratBranch> forks = document?.Branches.Where(b => b.AfterStepId == step.Id).ToList() ?? [];

                string number = (i + 1).ToString(CultureInfo.InvariantCulture);
                StringBuilder tooltip = new(StratClock.Format(step.AtSeconds) + "  " + StratStepPhrasing.Phrase(step, callouts));
                foreach (StratBranch fork in forks)
                {
                    tooltip.Append('\n').Append(ForkGlyph + " if " + fork.Condition.Text);
                }

                markers.Add(new TimelineMarker(TrackId, window.FromTick, window.FromTick, TimelineMarkerKind.Custom,
                    forks.Count > 0 ? number + ForkGlyph : number, tooltip.ToString(), 0u));

                // A step sharing its tick with the next owns no tick of its own, so it has no band to draw.
                if (!window.IsEmpty)
                {
                    int until = window.UntilTick ?? projection.LastTick;
                    bands.Add(new TimelineBand(TrackId, window.FromTick, Math.Max(window.FromTick, until), number,
                        tooltip.ToString(), 0u));
                }
            }
        }

        _markers = markers;
        _bands = bands;
        MarkersChanged?.Invoke();
    }
}
