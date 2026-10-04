using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DemoViewer.NET.Extensions.Sdk.Playback;

/// <summary>A toolbar button a contribution adds. Its label and tooltip may change; refresh them on <see cref="IPlaybackSurface.KeymapChanged" />.</summary>
public sealed class ToolbarItem : INotifyPropertyChanged
{
    private string _label;
    private string? _menuHeader;
    private string _tooltip;

    /// <summary>Creates the item.</summary>
    /// <param name="id">Unique within the tab.</param>
    /// <param name="label">The button text.</param>
    /// <param name="tooltip">The button tooltip.</param>
    /// <param name="run">Runs it at the moment shown; true when it acted.</param>
    /// <param name="actionId">The action that also runs it, or null.</param>
    /// <param name="order">Position among contributed items.</param>
    /// <param name="icon">The icon key, or null.</param>
    /// <param name="menuHeader">The overflow menu's text; null for the label.</param>
    public ToolbarItem(string id, string label, string tooltip, Func<PlaybackMoment, bool> run, string? actionId = null,
        int order = 0, string? icon = null, string? menuHeader = null)
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

    /// <summary>Unique within the tab.</summary>
    public string Id { get; }

    /// <summary>The button text.</summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>The overflow menu's text.</summary>
    public string? MenuHeader
    {
        get => _menuHeader;
        set => Set(ref _menuHeader, value);
    }

    /// <summary>The button tooltip.</summary>
    public string Tooltip
    {
        get => _tooltip;
        set => Set(ref _tooltip, value);
    }

    /// <summary>Runs it.</summary>
    public Func<PlaybackMoment, bool> Run { get; }

    /// <summary>The action that also runs it.</summary>
    public string? ActionId { get; }

    /// <summary>Position among contributed items.</summary>
    public int Order { get; }

    /// <summary>The icon key.</summary>
    public string? Icon { get; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
