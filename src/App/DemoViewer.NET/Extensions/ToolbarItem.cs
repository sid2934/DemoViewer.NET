#region

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Playback2D.Core;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A button a contribution adds to the 2D Playback toolbar (<see cref="IPlaybackSurface.AddToolbarItem" />):
///     the view lists it beside the core toolbar buttons and again as an entry in the camera-mode overflow
///     menu, so one registration reaches both. <see cref="Label" />, <see cref="MenuHeader" /> and
///     <see cref="Tooltip" /> are mutable so an owner can refresh them on a keymap rebind through
///     <see cref="IPlaybackSurface.KeymapChanged" /> and <see cref="IPlaybackSurface.GestureHint" />, the
///     way the gesture hint in the text used to.
/// </summary>
public sealed class ToolbarItem : ObservableObject
{
    private string _label;
    private string? _menuHeader;
    private string _tooltip;

    /// <param name="id">A stable id, a lookup key for tests.</param>
    /// <param name="label">The button's text.</param>
    /// <param name="tooltip">The button's tooltip.</param>
    /// <param name="run">Runs the item against the frame on screen at the moment it is invoked. True when it did something.</param>
    /// <param name="actionId">The id of the keymap action that also runs this item, or null for a toolbar/menu-only item.</param>
    /// <param name="order">Among toolbar items, lower first.</param>
    /// <param name="icon">An icon key, shown before <see cref="Label" /> in the toolbar; null for text alone. Not shown in the menu entry.</param>
    /// <param name="menuHeader">The menu entry's header; null to use <see cref="Label" />.</param>
    public ToolbarItem(string id, string label, string tooltip, Func<Scene2DFrame, bool> run,
        string? actionId = null, int order = 0, string? icon = null, string? menuHeader = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(run);
        Id = id;
        _label = label;
        _tooltip = tooltip ?? "";
        _menuHeader = menuHeader;
        Run = run;
        ActionId = actionId;
        Order = order;
        Icon = icon;
    }

    /// <summary>The stable id.</summary>
    public string Id { get; }

    /// <summary>The button's text.</summary>
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>The menu entry's header, or null to use <see cref="Label" />.</summary>
    public string? MenuHeader
    {
        get => _menuHeader;
        set => SetProperty(ref _menuHeader, value);
    }

    /// <summary>The button's tooltip.</summary>
    public string Tooltip
    {
        get => _tooltip;
        set => SetProperty(ref _tooltip, value);
    }

    /// <summary>Runs the item against the frame on screen. True when it did something.</summary>
    public Func<Scene2DFrame, bool> Run { get; }

    /// <summary>The id of the keymap action that also runs this item, or null.</summary>
    public string? ActionId { get; }

    /// <summary>Among toolbar items, lower first.</summary>
    public int Order { get; }

    /// <summary>An icon key, or null. The toolbar's DataTemplate renders it before the label; the menu entry does not.</summary>
    public string? Icon { get; }

    /// <summary>The view's binding target. Wired by <see cref="IPlaybackSurface.AddToolbarItem" />; null until then.</summary>
    public ICommand? Command { get; internal set; }
}
