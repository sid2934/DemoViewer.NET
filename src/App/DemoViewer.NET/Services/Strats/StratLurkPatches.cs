#region

using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The one writer of a step's <c>lurk</c>. An edit changes a copy and <see cref="Write" /> turns it into ops in
///     the stored shape: a rotate with no time, condition or place is dropped, and so is a lurk with no areas and no
///     rotate, so a step never carries an empty object.
/// </summary>
public static class StratLurkPatches
{
    /// <summary>A deep copy of the step's lurk, unknown fields included, or a new empty one.</summary>
    /// <param name="step">The step.</param>
    public static StepLurk Copy(StratStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Lurk is { } lurk ? Clone(lurk) : new StepLurk();
    }

    /// <summary>Whether a lurk says nothing: no area, no area point, no rotate and no unknown field.</summary>
    /// <param name="lurk">The lurk.</param>
    public static bool IsEmpty(StepLurk lurk) =>
        lurk.Areas.Count == 0 && lurk.AreaPoints is not { Count: > 0 } && (lurk.Rotate is null || IsEmpty(lurk.Rotate)) && lurk.Extra is not { Count: > 0 };

    /// <summary>Whether a rotate says nothing: no time, condition, place or unknown field.</summary>
    /// <param name="rotate">The rotate.</param>
    public static bool IsEmpty(LurkRotate rotate) =>
        rotate.AtSeconds is null && string.IsNullOrWhiteSpace(rotate.When) && !StratLocations.IsSet(rotate.To)
        && rotate.To?.Extra is not { Count: > 0 } && rotate.Extra is not { Count: > 0 };

    /// <summary>Ops that make the step's lurk <paramref name="target" />; empty when nothing changes.</summary>
    /// <param name="step">The step as it stands.</param>
    /// <param name="stepPath">Its pointer, <c>/steps/{i}</c>.</param>
    /// <param name="target">What the lurk should be; null or empty removes it.</param>
    public static List<PatchOp> Write(StratStep step, string stepPath, StepLurk? target)
    {
        ArgumentNullException.ThrowIfNull(step);
        StepLurk? after = target is null ? null : Canonical(Clone(target));
        JsonNode? beforeNode = step.Lurk is null ? null : Node(step.Lurk);
        JsonNode? afterNode = after is null ? null : Node(after);
        string path = stepPath + "/lurk";
        if (beforeNode is null)
        {
            return afterNode is null ? [] : [PatchOp.AddOp(path, afterNode)];
        }

        if (afterNode is null)
        {
            return [PatchOp.RemoveOp(path, beforeNode)];
        }

        return [.. StratHistory.Diff(beforeNode, afterNode).Select(op => op.Op switch
        {
            PatchOp.Add => PatchOp.AddOp(path + op.Path, op.Value),
            PatchOp.Remove => PatchOp.RemoveOp(path + op.Path, op.From),
            _ => PatchOp.ReplaceOp(path + op.Path, op.From, op.Value)
        })];
    }

    /// <summary><see cref="Write" /> of the step's lurk after <paramref name="edit" /> changes a copy of it.</summary>
    /// <param name="step">The step.</param>
    /// <param name="stepPath">Its pointer.</param>
    /// <param name="edit">Changes the copy.</param>
    public static List<PatchOp> Edit(StratStep step, string stepPath, Action<StepLurk> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        StepLurk lurk = Copy(step);
        edit(lurk);
        return Write(step, stepPath, lurk);
    }

    /// <summary>
    ///     Whether a rotate at <paramref name="rotateAtSeconds" /> is later in the round than a step at
    ///     <paramref name="stepAtSeconds" />, compared in strat ticks: the one rule the validator and the canvas share.
    /// </summary>
    /// <param name="rotateAtSeconds">The rotate's round clock time.</param>
    /// <param name="stepAtSeconds">The step's round clock time.</param>
    /// <param name="roundSeconds">The strat's round length.</param>
    public static bool IsLater(double rotateAtSeconds, double stepAtSeconds, double roundSeconds) =>
        double.IsFinite(rotateAtSeconds) && TickOf(rotateAtSeconds, roundSeconds) > TickOf(stepAtSeconds, roundSeconds);

    /// <summary>A round clock time as a strat tick, never before the round's start.</summary>
    /// <param name="atSeconds">Round clock remaining.</param>
    /// <param name="roundSeconds">The strat's round length.</param>
    public static int TickOf(double atSeconds, double roundSeconds) =>
        Math.Max(0, Playback2D.Core.Keyframes.StepSchedule.TickFor(atSeconds, roundSeconds));

    /// <summary>A copy of a lurk, unknown fields included.</summary>
    /// <param name="lurk">The lurk.</param>
    public static StepLurk Clone(StepLurk lurk) =>
        JsonSerializer.SerializeToNode(lurk, StratJsonContext.Default.StepLurk)!.Deserialize(StratJsonContext.Default.StepLurk)!;

    private static StepLurk? Canonical(StepLurk lurk)
    {
        lurk.Areas = [.. lurk.Areas.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal)];
        if (lurk.AreaPoints is { } points)
        {
            points.RemoveAll(p => !StratLocations.HasPoint(p));
            lurk.AreaPoints = points.Count == 0 ? null : points;
        }

        if (lurk.Rotate is { } rotate)
        {
            if (string.IsNullOrWhiteSpace(rotate.When))
            {
                rotate.When = null;
            }

            if (rotate.To is { } to && !StratLocations.IsSet(to) && to.Extra is not { Count: > 0 })
            {
                rotate.To = null;
            }

            if (IsEmpty(rotate))
            {
                lurk.Rotate = null;
            }
        }

        return IsEmpty(lurk) ? null : lurk;
    }

    private static JsonNode Node(StepLurk lurk) => JsonSerializer.SerializeToNode(lurk, StratJsonContext.Default.StepLurk)!;
}
