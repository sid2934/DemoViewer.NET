#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     Find Rounds Like This and the Situations result walk in 2D Playback, as one playback contribution
///     (item 20): the toolbar item (also the overflow menu's entry and Ctrl+F, one funnel through the
///     surface's action dispatch) and the J/K result-walk keys. The toolbar item is present only while the
///     open demo has a map name, as the key always required;
///     checked at attach (a live pack toggle with a demo already open) and on every demo change. Nothing
///     here exists while the pack is off.
/// </summary>
public sealed class SituationsPlaybackContribution : IPlaybackContribution
{
    private const string ToolbarItemId = "stratbook.findroundslikethis";

    private IDisposable? _actionHandler;
    private IModuleContext? _context;
    private IDisposable? _demoChanged;
    private IFindRoundsLikeThis? _findRounds;
    private ToolbarItem? _item;
    private ISituationResultWalk? _situationResults;
    private IPlaybackSurface? _surface;
    private IDisposable? _toolbarRegistration;

    /// <summary>The toolbar item while attached, or null before attach or while no demo's map is open. For tests.</summary>
    public ToolbarItem? ToolbarItem => _item;

    /// <inheritdoc />
    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(context);
        Detach();
        _surface = surface;
        _context = context;
        _findRounds = context.GetService<IFindRoundsLikeThis>();
        _situationResults = context.GetService<ISituationResultWalk>();

        _item = new ToolbarItem(ToolbarItemId, Label(surface), ToolTip(surface), RunFindRoundsLikeThis,
            Playback2DAction.FindRoundsLikeThis);
        surface.KeymapChanged += OnKeymapChanged;
        _demoChanged = surface.OnDemoChanged(RefreshAvailability);
        _actionHandler = surface.AddActionHandler(OnAction);

        // Covers the live-toggle case: a pack turned on while the tab is already active never raises
        // OnDemoChanged again (OnActivated's resync already ran), so the current map must be read now too.
        RefreshAvailability();
    }

    /// <inheritdoc />
    public void Detach()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        surface.KeymapChanged -= OnKeymapChanged;
        _demoChanged?.Dispose();
        _actionHandler?.Dispose();
        _toolbarRegistration?.Dispose();
        _demoChanged = null;
        _actionHandler = null;
        _toolbarRegistration = null;
        _item = null;
        _surface = null;
        _context = null;
        _findRounds = null;
        _situationResults = null;
    }

    private void RefreshAvailability()
    {
        bool available = _context?.MapName is { Length: > 0 };
        if (available && _toolbarRegistration is null && _item is { } item && _surface is { } surface)
        {
            _toolbarRegistration = surface.AddToolbarItem(item);
        }
        else if (!available && _toolbarRegistration is not null)
        {
            _toolbarRegistration.Dispose();
            _toolbarRegistration = null;
        }
    }

    // Hands the CURRENT scene frame over, not the live player states: the markers are the copied-out
    // scalars the scene built inside the last Advanced callback, place included, and the pooled entities
    // behind them are not safe to read from a key handler. Refused with no seam, no map, or no frame
    // pushed yet; the seam refuses on its own when nobody is alive.
    private bool RunFindRoundsLikeThis(Scene2DFrame frame) =>
        _findRounds is { } find && _context?.MapName is { Length: > 0 } map
        && frame.Markers.Count > 0 && find.Show(map, frame.Time, frame.Markers);

    // J/K: the walk seeks through the seam's own funnel, so the shared clock and LiveSync's observer see
    // it as any other seek. Gated by the Situations tab's own feature, which the pack cascades off with.
    private bool OnAction(Playback2DAction action)
    {
        bool situationsOn = _context?.Features?.IsEnabled(SituationsModule.TabFeatureId) ?? true;
        return action switch
        {
            Playback2DAction.NextSituationResult => situationsOn && (_situationResults?.Walk(+1) ?? false),
            Playback2DAction.PrevSituationResult => situationsOn && (_situationResults?.Walk(-1) ?? false),
            _ => false
        };
    }

    private void OnKeymapChanged()
    {
        if (_item is not { } item || _surface is not { } surface)
        {
            return;
        }

        item.Label = Label(surface);
        item.Tooltip = ToolTip(surface);
    }

    private static string Label(IPlaybackSurface surface) =>
        $"Find rounds like this{surface.GestureHint(Playback2DAction.FindRoundsLikeThis)}";

    private static string ToolTip(IPlaybackSurface surface) =>
        $"Find rounds like this{surface.GestureHint(Playback2DAction.FindRoundsLikeThis)}: snapshot the alive players by "
        + "side onto the Situations query canvas and search the library's round index for this setup";
}
