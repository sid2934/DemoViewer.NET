namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The seam a Result Card seeks 2D playback through. The Situations tab references no shell, so
///     it resolves this from the container and a test hands it a recorder; the shipped implementation
///     is below.
/// </summary>
public interface ISituationPlayback
{
    /// <summary>
    ///     Opens the demo in the workspace when it is not the loaded one, seeks the shared clock to the
    ///     tick, and shows the 2D Playback tab. Returns false when the demo could not be opened or the
    ///     tab is gated off; the card then says so instead of pretending.
    /// </summary>
    /// <param name="demoPath">The demo's path as the library knows it.</param>
    /// <param name="tick">Frame clock: the matched tick minus the seek offset.</param>
    Task<bool> SeekAsync(string demoPath, int tick);
}

/// <summary>
///     The shipped seam over the shell's own funnels: <c>LoadDemoFromPathAsync</c> when the card's demo
///     is not the loaded one (the interactive open, with the progress ring the open already has), the
///     controller's <c>SeekToTick</c> (the one position-move code path LiveSync observes), and the tab
///     switch by the 2D tab's persisted id. Delegate-injected so the same-demo shortcut and the tab
///     switch are testable without a shell.
/// </summary>
public sealed class SituationPlaybackSeek : ISituationPlayback
{
    /// <summary>The 2D Playback tab's persisted id, the one <c>Playback2DModule</c> contributes.</summary>
    public const string PlaybackTabId = "playback2d.viewport";

    private readonly Func<string?> _loadedDemoPath;
    private readonly Func<string, Task<bool>> _open;
    private readonly Action<int> _seekToTick;
    private readonly Func<string, bool> _selectTab;

    /// <param name="loadedDemoPath">The path of the demo the shell holds, or null.</param>
    /// <param name="open">Opens a demo through the shared load core; false when the load did not land.</param>
    /// <param name="seekToTick">The controller's seek, frame clock.</param>
    /// <param name="selectTab">Shows a workspace tab by id; false when no such tab is on the strip.</param>
    public SituationPlaybackSeek(Func<string?> loadedDemoPath, Func<string, Task<bool>> open,
        Action<int> seekToTick, Func<string, bool> selectTab)
    {
        ArgumentNullException.ThrowIfNull(loadedDemoPath);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(seekToTick);
        ArgumentNullException.ThrowIfNull(selectTab);
        _loadedDemoPath = loadedDemoPath;
        _open = open;
        _seekToTick = seekToTick;
        _selectTab = selectTab;
    }

    /// <inheritdoc />
    public async Task<bool> SeekAsync(string demoPath, int tick)
    {
        ArgumentNullException.ThrowIfNull(demoPath);

        // The same demo is the common case while walking a result set: hits cluster by demo, and a
        // re-open would throw away the parse the clock is already standing on.
        if (!IsLoaded(demoPath) && !await _open(demoPath))
        {
            return false;
        }

        if (!IsLoaded(demoPath))
        {
            return false; // the open landed on something else, or was cancelled under us
        }

        _seekToTick(tick);
        return _selectTab(PlaybackTabId);
    }

    private bool IsLoaded(string demoPath) =>
        _loadedDemoPath() is { Length: > 0 } loaded
        && string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(demoPath), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     The seam the 2D Playback tab walks the current result set through on <c>J</c> / <c>K</c>. The
///     playback tab is built by a bare <c>new()</c> and references no other module, so it resolves this
///     from the container and a test hands it a fake; the shipped implementation is below.
/// </summary>
public interface ISituationResultWalk
{
    /// <summary>
    ///     Moves the selection by <paramref name="direction" /> (+1 next, -1 previous) and seeks
    ///     playback to the new card. Returns false when there is no result set or no card in that
    ///     direction, so the caller leaves the key unhandled.
    /// </summary>
    /// <param name="direction">+1 or -1.</param>
    bool Walk(int direction);
}

/// <summary>
///     The shipped walk: the Situations tab's result cards, resolved lazily (the container singleton
///     the module's own factory returns, so the set the key walks is the set the tab shows, whether or
///     not the tab has ever been opened). The walk never switches tabs on its own: the card's seek
///     shows the 2D tab, which is where the key was pressed.
/// </summary>
public sealed class SituationResultWalk : ISituationResultWalk
{
    private readonly Func<ViewModels.Situations.SituationsTabViewModel> _tab;

    /// <param name="tab">The Situations tab VM, resolved on first use.</param>
    public SituationResultWalk(Func<ViewModels.Situations.SituationsTabViewModel> tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
    }

    /// <inheritdoc />
    public bool Walk(int direction) => _tab().Results.Walk(direction);
}
