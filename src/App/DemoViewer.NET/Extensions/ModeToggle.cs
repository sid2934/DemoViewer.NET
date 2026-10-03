#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A mode of the 2D Playback tab a contribution owns: a toolbar toggle the view renders, a keymap action
///     that flips it, and the on/off state the owner reads and persists. A right-column panel bound to it
///     (<see cref="IPlaybackSurface.AddPanel" />) shows only while it is on. The owner sets
///     <see cref="IsAvailable" />; off, the toolbar hides the toggle and the action leaves the mode alone
///     unless it is on, so a mode with nothing to show can still be left.
/// </summary>
public sealed class ModeToggle : ObservableObject
{
    private bool _isAvailable = true;
    private bool _isOn;

    /// <param name="id">A stable id, a lookup key for tests.</param>
    /// <param name="label">The toggle's text.</param>
    /// <param name="tooltip">The toggle's tooltip.</param>
    /// <param name="action">The keymap action that flips the mode, or null for a toolbar-only mode.</param>
    /// <param name="icon">An icon key for the toggle, or null for text alone. Reserved; the toolbar shows the label.</param>
    public ModeToggle(string id, string label, string tooltip, Playback2DAction? action = null, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(label);
        Id = id;
        Label = label;
        Tooltip = tooltip ?? "";
        Action = action;
        Icon = icon;
    }

    /// <summary>The stable id.</summary>
    public string Id { get; }

    /// <summary>The toggle's text.</summary>
    public string Label { get; }

    /// <summary>The toggle's tooltip.</summary>
    public string Tooltip { get; }

    /// <summary>The keymap action that flips the mode, or null.</summary>
    public Playback2DAction? Action { get; }

    /// <summary>An icon key, or null.</summary>
    public string? Icon { get; }

    /// <summary>The mode is on. Setting it raises <see cref="Changed" /> on a real flip.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The mode has something to show. The toolbar hides the toggle otherwise.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    /// <summary><see cref="IsOn" /> flipped.</summary>
    public event Action? Changed;

    /// <summary>
    ///     The keymap action's turn: flips the mode when it is available or on. False when the mode is off with
    ///     nothing to show, so the key stays unhandled.
    /// </summary>
    public bool TryToggle()
    {
        if (!IsAvailable && !IsOn)
        {
            return false;
        }

        IsOn = !IsOn;
        return true;
    }
}
