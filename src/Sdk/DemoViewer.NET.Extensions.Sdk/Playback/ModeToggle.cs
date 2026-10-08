using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DemoViewer.NET.Extensions.Sdk.Playback;

/// <summary>A mode of the 2D Playback tab that a contribution owns. The toolbar shows it while available.</summary>
public sealed class ModeToggle : INotifyPropertyChanged
{
    private bool _isAvailable = true;
    private bool _isOn;

    /// <summary>Creates the toggle.</summary>
    /// <param name="id">Unique within the tab.</param>
    /// <param name="label">The toolbar text.</param>
    /// <param name="tooltip">The toolbar tooltip.</param>
    /// <param name="actionId">The action that flips it, or null for none.</param>
    /// <param name="icon">The toolbar icon key, or null.</param>
    public ModeToggle(string id, string label, string tooltip, string? actionId = null, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(label);
        Id = id;
        Label = label;
        Tooltip = tooltip ?? "";
        ActionId = actionId;
        Icon = icon;
    }

    /// <summary>Unique within the tab.</summary>
    public string Id { get; }

    /// <summary>The toolbar text.</summary>
    public string Label { get; }

    /// <summary>The toolbar tooltip.</summary>
    public string Tooltip { get; }

    /// <summary>The action that flips it.</summary>
    public string? ActionId { get; }

    /// <summary>The toolbar icon key.</summary>
    public string? Icon { get; }

    /// <summary>Whether the mode is on. Setting it raises <see cref="Changed" /> on a flip.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            _isOn = value;
            Raise();
            Changed?.Invoke();
        }
    }

    /// <summary>False hides the toggle; its action can then only turn the mode off.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable == value)
            {
                return;
            }

            _isAvailable = value;
            Raise();
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when <see cref="IsOn" /> flips.</summary>
    public event Action? Changed;

    /// <summary>Flips the mode. Refused, returning false, when it is off and unavailable.</summary>
    public bool TryToggle()
    {
        if (!IsAvailable && !IsOn)
        {
            return false;
        }

        IsOn = !IsOn;
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
