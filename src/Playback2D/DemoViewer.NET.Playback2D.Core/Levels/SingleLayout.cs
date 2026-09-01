#region

using DemoViewer.NET.Playback2D.Core.Cameras;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     One pane covering the whole host, showing exactly one level.
///     <para>
///         The stacked view divides the height by the floor count, which on a two-floor map halves the
///         scale of the floor anyone is actually watching. Single mode is the answer, and the level
///         strip (manual) plus AutoFollow (the followed player's floor) are the two ways to say which.
///     </para>
///     <para>
///         An <see cref="ActiveLevelId" /> that no level answers to (just after a rebuild removed it, or
///         before the first selection) falls back to the <b>top-most</b> level, matching the stacked
///         view's "highest floor on top" reading order rather than silently showing a basement.
///     </para>
/// </summary>
public sealed class SingleLayout : ILevelLayoutPolicy
{
    private readonly List<LevelPane> _arranged = [];
    private MapLevelId _activeLevelId = MapLevelId.None;

    /// <summary>The level to show. Driven by <c>LevelSelection.ActiveLevelId</c>.</summary>
    public MapLevelId ActiveLevelId
    {
        get => _activeLevelId;
        set
        {
            if (_activeLevelId == value)
            {
                return;
            }

            _activeLevelId = value;
            Revision++;
        }
    }

    /// <summary>The level index this policy last arranged, or -1. Diagnostics and tests.</summary>
    public int ArrangedLevelIndex { get; private set; } = -1;

    /// <inheritdoc />
    public int Revision { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<LevelPane> Arrange(MapSpace space, LevelDisplayMode mode, SKSize host)
    {
        ArgumentNullException.ThrowIfNull(space);

        _arranged.Clear();
        ArrangedLevelIndex = -1;

        IReadOnlyList<MapLevel> levels = space.Levels;
        if (levels.Count == 0)
        {
            return _arranged;
        }

        int index = space.IndexOf(_activeLevelId);
        if (index < 0)
        {
            index = levels.Count - 1; // top-most
        }

        ArrangedLevelIndex = index;
        _arranged.Add(new LevelPane(levels[index], default, ManualRig.Instance)
        {
            LevelIndex = index,
            ViewportRect = new SKRect(0, 0, host.Width, host.Height)
        });

        return _arranged;
    }
}

/// <summary>The factory the host, the export session and <c>dv2d</c> all go through.</summary>
public static class LevelLayouts
{
    /// <summary>A fresh policy for a display mode.</summary>
    /// <param name="mode">The requested mode.</param>
    /// <exception cref="NotSupportedException"><see cref="LevelDisplayMode.SideBySide" /> is reserved.</exception>
    public static ILevelLayoutPolicy For(LevelDisplayMode mode) => mode switch
    {
        LevelDisplayMode.Stacked => new StackedLayout(),
        LevelDisplayMode.Single => new SingleLayout(),
        _ => throw new NotSupportedException(
            $"{mode} is reserved; no policy returns it in v1 (registry §3.4).")
    };

    /// <summary>
    ///     Parses a persisted <c>Playback2D:LevelDisplayMode</c> value, falling back to
    ///     <see cref="LevelDisplayMode.Stacked" />. A settings file is user-editable and a typo there
    ///     must not stop the tab from opening.
    /// </summary>
    /// <param name="value">The persisted string, or null.</param>
    /// <remarks>
    ///     <see cref="Enum.TryParse{TEnum}(string,bool,out TEnum)" /> accepts any number inside the
    ///     underlying type's range, so <c>"7"</c> parses to an undefined <see cref="LevelDisplayMode" />
    ///     that <see cref="For" /> then throws on. <see cref="Enum.IsDefined{TEnum}(TEnum)" /> is what
    ///     makes the fallback actually cover a hand-edited settings file.
    /// </remarks>
    public static LevelDisplayMode Parse(string? value) =>
        Enum.TryParse(value, true, out LevelDisplayMode mode)
        && Enum.IsDefined(mode)
        && mode != LevelDisplayMode.SideBySide
            ? mode
            : LevelDisplayMode.Stacked;
}
