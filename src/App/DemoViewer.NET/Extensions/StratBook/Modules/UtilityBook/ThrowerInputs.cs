#region

using CS2DemoKit.Parser.EntityTracking;
using CS2OpenSchema.Protos;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The thrower's inputs around a throw (grenade-walk.md §3.7): the seam the jump-throw flag reads, so
///     a source with better commands replaces the default without touching the walk.
/// </summary>
public interface IThrowerInputSource
{
    /// <summary>The <c>inputDecoder</c> the sidecar header records, so a later decoder can re-index.</summary>
    string Decoder { get; }

    /// <summary>Share of the demo's commands that decoded, for the sidecar header.</summary>
    double Coverage { get; }

    /// <summary>True when at least one command for <paramref name="slot" /> decoded in <c>[fromTick, toTick]</c> (frame clock).</summary>
    bool HasCoverage(int slot, int fromTick, int toTick);

    /// <summary>The latest tick in the window at which <c>IN_JUMP</c> rose, or null.</summary>
    int? FindJumpPress(int slot, int fromTick, int toTick);
}

/// <summary>
///     Commands rebuilt by CS2DemoKit's <see cref="UserCmdReconstructor" /> (0.13, #53), so the delta-encoded
///     commands of current demos count as well as the full ones. Only the commands inside a throw's jump
///     window are kept, one small entry each; a slot's previous button state is carried across so a press
///     on the window's first command is still seen as a press.
/// </summary>
public sealed class ReconstructedInputSource : IThrowerInputSource
{
    /// <summary>The header's decoder name for this source.</summary>
    public const string DecoderName = "user-cmd-reconstructor";

    private readonly Dictionary<int, ulong> _lastButtons = [];
    private readonly Dictionary<int, List<(int Tick, bool JumpRose)>> _kept = [];
    private readonly Dictionary<int, List<(int From, int To)>> _windows = [];
    private double _coverage;

    /// <param name="windows">Per slot, the tick windows whose commands are kept.</param>
    public ReconstructedInputSource(IEnumerable<(int Slot, int FromTick, int ToTick)> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        foreach ((int slot, int from, int to) in windows)
        {
            if (!_windows.TryGetValue(slot, out List<(int, int)>? list))
            {
                list = [];
                _windows[slot] = list;
            }

            list.Add((from, to));
        }
    }

    /// <inheritdoc />
    public string Decoder => DecoderName;

    /// <inheritdoc />
    public double Coverage => _coverage;

    /// <inheritdoc />
    public bool HasCoverage(int slot, int fromTick, int toTick) =>
        _kept.TryGetValue(slot, out List<(int Tick, bool)>? list) && list.Any(c => c.Tick >= fromTick && c.Tick <= toTick);

    /// <inheritdoc />
    public int? FindJumpPress(int slot, int fromTick, int toTick)
    {
        if (!_kept.TryGetValue(slot, out List<(int Tick, bool JumpRose)>? list))
        {
            return null;
        }

        int? latest = null;
        foreach ((int tick, bool rose) in list)
        {
            if (rose && tick >= fromTick && tick <= toTick && (latest is null || tick > latest))
            {
                latest = tick;
            }
        }

        return latest;
    }

    /// <summary>Feeds one rebuilt command.</summary>
    /// <param name="command">A command the reconstructor emitted.</param>
    /// <param name="serverStartTick"><c>ParsedDemo.ServerStartTick</c>: the executed tick minus this is the frame clock.</param>
    public void Observe(ReconstructedUserCmd command, int serverStartTick)
    {
        CBaseUserCmdPB? cmd = command.Command?.Base;
        ulong buttons = cmd?.ButtonsPb?.Buttonstate1 ?? 0;
        bool subtickPress = false;
        if (cmd is not null)
        {
            foreach (CSubtickMoveStep step in cmd.SubtickMoves)
            {
                if (step.Button == GrenadeRules.JumpButton && step.Pressed)
                {
                    subtickPress = true;
                    break;
                }
            }
        }

        Observe(command.PlayerSlot, command.ServerTickExecuted - serverStartTick, buttons, subtickPress);
    }

    /// <summary>Feeds one command's facts: the pure half of <see cref="Observe(ReconstructedUserCmd, int)" />.</summary>
    /// <param name="slot">The command's player slot.</param>
    /// <param name="tick">Frame clock.</param>
    /// <param name="buttons"><c>buttonstate1</c>.</param>
    /// <param name="subtickJumpPress">A sub-tick move pressed <c>IN_JUMP</c>.</param>
    public void Observe(int slot, int tick, ulong buttons, bool subtickJumpPress)
    {
        ulong previous = _lastButtons.GetValueOrDefault(slot);
        _lastButtons[slot] = buttons;
        if (!InWindow(slot, tick))
        {
            return;
        }

        bool rose = subtickJumpPress
                    || ((buttons & GrenadeRules.JumpButton) != 0 && (previous & GrenadeRules.JumpButton) == 0);
        if (!_kept.TryGetValue(slot, out List<(int, bool)>? list))
        {
            list = [];
            _kept[slot] = list;
        }

        list.Add((tick, rose));
    }

    /// <summary>Records the reconstructor's totals as the coverage: decoded over every command that carried a payload.</summary>
    /// <param name="stats">The reconstructor's stats at the end of the walk.</param>
    public void Finish(UserCmdReconstructionStats stats)
    {
        long decoded = stats.Full + stats.Delta;
        long attempted = decoded + stats.MissingBaseline + stats.OutOfOrder + stats.DecodeFailed;
        _coverage = attempted == 0 ? 0 : (double)decoded / attempted;
    }

    /// <summary>Sets the coverage directly, for a source fed by hand.</summary>
    /// <param name="coverage">0 to 1.</param>
    public void SetCoverage(double coverage) => _coverage = Math.Clamp(coverage, 0, 1);

    private bool InWindow(int slot, int tick)
    {
        if (!_windows.TryGetValue(slot, out List<(int From, int To)>? windows))
        {
            return false;
        }

        foreach ((int from, int to) in windows)
        {
            if (tick >= from && tick <= to)
            {
                return true;
            }
        }

        return false;
    }
}
