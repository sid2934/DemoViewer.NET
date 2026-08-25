#region

using System.Diagnostics.CodeAnalysis;
using DemoViewer.NET.Playback2D.Core.Cameras;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>How the level set is laid out on the host surface.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "'Single' is the canonical member name fixed by the cross-phase registry " +
                    "(plans/00-overview.md §3.4); B3 and the persisted Playback2DSettings.LevelDisplayMode " +
                    "key both spell it that way. Renaming it to satisfy an analyser would break a " +
                    "persisted setting to avoid a coincidence with System.Single.")]
public enum LevelDisplayMode
{
    /// <summary>Every level as a horizontal band, highest on top. The pre-v2 behaviour, and B1's only mode.</summary>
    Stacked,

    /// <summary>One level filling the host. B3 ships the policy and the level strip that drives it.</summary>
    Single,

    /// <summary>Reserved. No policy returns it in v1 and nothing should branch on it yet.</summary>
    SideBySide
}

/// <summary>
///     Decides which levels get panes and where those panes sit. Deliberately separate from
///     <see cref="PaneSet" />, which owns pane <i>lifetime</i> and camera identity: a layout policy
///     answers a geometry question and must not be able to lose a user's pan (plan decision D-4).
/// </summary>
public interface ILevelLayoutPolicy
{
    /// <summary>
    ///     Bumped whenever the policy would arrange the same level set differently — B3's
    ///     <see cref="SingleLayout" /> changing which level it shows, and nothing else today.
    ///     <para>
    ///         <see cref="PaneSet.Reconcile" /> early-outs on the level-set version, the mode and the
    ///         host size, so without this a policy whose <i>own</i> state changed would never be asked
    ///         again. The default is a constant, which is exactly right for a policy that is a pure
    ///         function of its arguments.
    ///     </para>
    /// </summary>
    int Revision => 0;

    /// <summary>Arranges the space's levels over a host surface.</summary>
    /// <param name="space">The level set.</param>
    /// <param name="mode">The requested display mode.</param>
    /// <param name="host">Host surface size in device-independent pixels.</param>
    /// <returns>
    ///     Freshly described panes — geometry only. <see cref="PaneSet" /> reconciles them against the
    ///     live panes by level id and discards these.
    /// </returns>
    IReadOnlyList<LevelPane> Arrange(MapSpace space, LevelDisplayMode mode, SKSize host);
}

/// <summary>
///     The pre-v2 band layout, reproduced exactly: <c>bandHeight = host.Height / max(1, levels)</c>, and
///     the pane for level <c>i</c> (0 = lowest) occupies band <c>count - 1 - i</c> so the highest floor
///     renders on top (parity invariant 2, viewport lines 546-548 and 580-584).
/// </summary>
public sealed class StackedLayout : ILevelLayoutPolicy
{
    private readonly List<LevelPane> _arranged = [];

    /// <inheritdoc />
    public IReadOnlyList<LevelPane> Arrange(MapSpace space, LevelDisplayMode mode, SKSize host)
    {
        ArgumentNullException.ThrowIfNull(space);

        _arranged.Clear();
        int count = space.Levels.Count;
        if (count == 0)
        {
            return _arranged;
        }

        float bandHeight = host.Height / Math.Max(1, count);
        for (int i = 0; i < count; i++)
        {
            int section = count - 1 - i; // highest floor on top
            LevelPane pane = new(space.Levels[i], default, ManualRig.Instance)
            {
                LevelIndex = i,
                ViewportRect = new SKRect(0, section * bandHeight, host.Width, (section + 1) * bandHeight)
            };
            _arranged.Add(pane);
        }

        return _arranged;
    }

    /// <summary>
    ///     The band rectangle for a level index under this policy, without arranging anything. Shared by
    ///     <see cref="PaneSet.PaneAt" /> so hit-testing and drawing cannot disagree about where a band is.
    /// </summary>
    /// <param name="levelIndex">Level index, 0 = lowest.</param>
    /// <param name="levelCount">How many levels are arranged.</param>
    /// <param name="host">Host surface size.</param>
    public static SKRect BandRect(int levelIndex, int levelCount, SKSize host)
    {
        int count = Math.Max(1, levelCount);
        float bandHeight = host.Height / count;
        int section = count - 1 - levelIndex;
        return new SKRect(0, section * bandHeight, host.Width, (section + 1) * bandHeight);
    }
}
