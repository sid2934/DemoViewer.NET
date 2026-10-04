#region

using System.Globalization;
using System.Text.RegularExpressions;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     The zip check: an authored departure on a travel or lurk step that the token cannot reach
///     at a run. The leg is read from the projection with that entry taken out: from where the token last stood or
///     arrived before the step's tick (a run's arrival, a run's start when it is still running, or a placed entry) to
///     the departure, along the route when the projection routes, else straight. Faster than
///     <see cref="StratSceneProjection.RunUnitsPerSecond" /> warns at <c>/steps/i/positions/j</c>.
/// </summary>
public static partial class StratDepartureCheck
{
    /// <summary>The check with straight lines and no map: what a host without zones can say.</summary>
    /// <param name="document">The strat.</param>
    public static IReadOnlyList<StratIssue> Check(StratDocument document) =>
        Check(document, d => StratSceneProjection.Build(d, StratPath.MainLine(d)));

    /// <summary>The check, each leg read from <paramref name="project" />'s main-line projection.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="project">Projects a document's main line with the host's places and routes.</param>
    public static IReadOnlyList<StratIssue> Check(StratDocument document, Func<StratDocument, StratSceneProjection> project)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(project);
        List<StratIssue> issues = [];

        // A legacy capture reads every entry as seen: none is a departure.
        if (StratSceneProjection.IsLegacyObserved(document))
        {
            return issues;
        }

        // From the first step: a start is somewhere to have come from.
        for (int i = 0; i < document.Steps.Count; i++)
        {
            StratStep step = document.Steps[i];
            if (StratStepFields.MotionOf(step.Verb) is not (StepMotion.Travel or StepMotion.Lurk))
            {
                continue;
            }

            for (int k = 0; k < step.Positions.Count; k++)
            {
                StepPosition position = step.Positions[k];
                if (position.Carried == true || position.Observed == true || !StratVocabulary.Slots.Contains(position.Slot)
                    || !StratStepLines.Involves(step, position.Slot)
                    || step.Positions.FindLastIndex(p => p.Slot == position.Slot) != k)
                {
                    continue;
                }

                StratSceneProjection without = project(StratHistory.Apply(document,
                    [PatchOp.RemoveOp(string.Create(CultureInfo.InvariantCulture, $"/steps/{i}/positions/{k}"), null)]));
                if (i >= without.Ticks.Count || without.LastStand(position.Slot, without.Ticks[i]) is not { } last)
                {
                    continue;
                }

                double distance = Length(without.Paths, last.At, position);
                double seconds = Math.Max(without.Ticks[i] - last.Tick, 1) / (double)StepSchedule.TicksPerSecond;
                if (distance / seconds > StratSceneProjection.RunUnitsPerSecond)
                {
                    issues.Add(new StratIssue(StratIssueSeverity.Warning,
                        string.Create(CultureInfo.InvariantCulture, $"/steps/{i}/positions/{k}"),
                        string.Create(CultureInfo.InvariantCulture,
                            $"{position.Slot} would cross {distance:0} u in {seconds:0.0} s to where it leaves from, faster than a run")));
                }
            }
        }

        return issues;
    }

    /// <summary>The step and entry a check's pointer names, or null for another pointer.</summary>
    /// <param name="field">An issue's field.</param>
    public static (int Step, int Position)? PositionOf(string field)
    {
        Match m = PositionPointer().Match(field ?? "");
        return m.Success
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static double Length(PathResolver? paths, TokenKeyframe from, StepPosition to)
    {
        if (paths?.Route(from.X, from.Y, from.LevelMinZ, to.X, to.Y, to.LevelMinZ, null) is { Count: >= 2 } route)
        {
            return NavPathfinder.Length(route);
        }

        double dx = to.X - from.X, dy = to.Y - from.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [GeneratedRegex(@"^/steps/(\d+)/positions/(\d+)$")]
    private static partial Regex PositionPointer();
}
